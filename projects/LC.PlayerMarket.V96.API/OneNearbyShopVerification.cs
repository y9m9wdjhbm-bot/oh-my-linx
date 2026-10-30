using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>One explicitly requested nearby-shop query through the unchanged production broker.</summary>
internal static class OneNearbyShopVerification
{
    private const string UserRoot = @"D:\lineage\LC_PlayerMarket_UserData\V38";
    private const int MaximumDistance = 3;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal static MarketOwner SelectTarget(ShopTargetSnapshot targets, DateTimeOffset now)
    {
        if (!targets.CanDispatch || targets.SelfPosition is not { } self || targets.SelfId == 0
            || targets.RefreshedAt > now || now - targets.RefreshedAt > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            throw new InvalidOperationException("FRESH_TARGET_SNAPSHOT_REQUIRED");
        var eligible = targets.Owners.Where(owner => owner.Id != targets.SelfId && owner.Id is > 0 and <= int.MaxValue
            && owner.Position is not null && owner.Position.GridDistance(self) <= MaximumDistance
            && owner.LocalIdentity.Length > 0 && ShopEligibilityPolicy.CanScan(owner)
            && owner.ShopStateSampledAt is { } at && at <= now
            && now - at <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            .OrderBy(owner => owner.Position!.GridDistance(self)).ThenBy(owner => owner.Id).ToArray();
        if (eligible.Length == 0) throw new InvalidOperationException("NO_FRESH_CONFIRMED_SHOP_WITHIN_THREE_CELLS");
        var selected = eligible[0];
        if (targets.Owners.Count(owner => owner.Id == selected.Id) != 1)
            throw new InvalidOperationException("DUPLICATE_TARGET_ID");
        return selected;
    }

    // The reader can retain allocated Present storage after both banks close.
    // Accept only fully empty Closed bank storage; this does not establish UI
    // visibility/closure or a new response. Never close/replace a viewer here.
    internal static bool IsClosedEmptyViewerBaseline(PersonalShopListing value)
    {
        static bool Hash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
        return value.OwnerIdRaw is 0 or ulong.MaxValue
            && (value.LayoutState == "Absent" && !value.Complete || value.LayoutState == "Present" && value.Complete)
            && value.BankState == "Closed" && Hash(value.BankFingerprint) && value.PageMode is null
            && value.PageModeState == "Unknown" && value.Rows.Length == 0 && value.IncompleteReasons.Length == 0
            && value.RawBankSummary.Length == 2 && value.RawBankSummary.Select(b => b.BankIndex).Distinct().Count() == 2
            && value.RawBankSummary.All(b => b.BankIndex is 0 or 1 && b.OccupiedSlots == 0 && b.TypedPayloadSlots == 0
                && Hash(b.RawSha256) && b.NativeMode == (b.BankIndex == 0 ? 2 : 1)
                && b.PageRole == (b.BankIndex == 0 ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell))
            && value.SlotRows.Length == 16 && value.SlotRows.Select(r => (r.BankIndex, r.RowIndex)).Distinct().Count() == 16
            && value.SlotRows.All(r => r.BankIndex is 0 or 1 && r.RowIndex is >= 0 and <= 7
                && !r.PairPresent && r.State == "EmptyPair");
    }
    internal static async Task<int> RunAsync(string direction, string output)
    {
        using var user = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        if (direction is not (ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell)) return 2;
        output = Path.GetFullPath(output);
        if (File.Exists(output) || Directory.Exists(output)) return 2;
        Directory.CreateDirectory(output);
        using var trace = new StreamWriter(new FileStream(Path.Combine(output,"commands-and-observations.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)){AutoFlush=true};
        object logGate = new();
        void Log(object row) { lock(logGate) trace.WriteLine(JsonSerializer.Serialize(row,HubProtocol.Json)); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var first = new TaskCompletionSource<HubSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        FileStream? lease = null; StandaloneMarketBackend? backend = null; CurrentLcSource? source = null; HubRuntime? runtime = null;
        SourceIdentity? bound = null; PersonalShopListing? before = null; MarketOwner? target = null;
        ShopCommandResponse? readiness = null, refresh = null, response = null, finalState = null;
        var startup = new List<ShopCommandResponse>(); var errors = new List<string>();
        Task watch=Task.CompletedTask, keys=Task.CompletedTask; bool stopped=false, settled=false; int attempts=0;
        async Task<ShopCommandResponse> Send(ShopCommandRequest request,CancellationToken token)
        {
            if(request.Command is not (ShopCommands.RefreshTargets or ShopCommands.PrepareNativeReadiness or ShopCommands.OpenShop or ShopCommands.GetState))
                throw new InvalidOperationException("EXACT_COMMAND_ALLOWLIST");
            Log(new{Kind="REQUEST",At=DateTimeOffset.UtcNow,Request=request});
            var result=await backend!.SendAsync(request,token);
            Log(new{Kind="RESPONSE",At=DateTimeOffset.UtcNow,Response=result}); return result;
        }
        try
        {
            lease=new FileStream(Path.Combine(UserRoot,"market-session-v55.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            backend=new StandaloneMarketBackend(Path.Combine(output,"state"),
                log=>source=(CurrentLcSource)StandaloneMarketBackend.CreateDefaultSource(log),
                (rt,src,shops)=>{runtime=rt;rt.SetLogging(true);return new StandaloneShopCommands(new ShopBroker(rt,(CurrentLcSource)src,shops,nativeLogDirectory:Path.Combine(output,"diagnostics","native")));},
                diagnosticsRoot:Path.Combine(output,"diagnostics"));
            backend.ConfigurePageWaitMilliseconds(new LocalStore(UserRoot).LoadPreferences().EffectivePageOpenIntervalMilliseconds);
            watch=Task.Run(async()=>{try{await foreach(var snapshot in backend.WatchAsync(deadline.Token)){Log(new{Kind="SNAPSHOT",At=DateTimeOffset.UtcNow,Snapshot=snapshot});if(snapshot.Source is not null)first.TrySetResult(snapshot);}}catch(OperationCanceledException)when(deadline.IsCancellationRequested){}catch(Exception error){first.TrySetException(error);throw;}});
            keys=Task.Run(async()=>{while(!deadline.IsCancellationRequested){if((GetAsyncKeyState(0x78)&0x8000)!=0){deadline.Cancel();break;}try{await Task.Delay(25,deadline.Token);}catch(OperationCanceledException){break;}}});
            bound=(await first.Task.WaitAsync(deadline.Token)).Source!;
            if(source is null || runtime is null || runtime.TransportEnabled || !ShopNativePackage.SupportsBuild(bound))throw new InvalidOperationException("CURRENT_PRODUCTION_SOURCE_REQUIRED");
            for(int i=0;i<3;i++)
            {
                refresh=await Send(new(){Command=ShopCommands.RefreshTargets,ExpectedSourceSessionKey=bound.SessionKey},deadline.Token);startup.Add(refresh);
                if(refresh.State=="TargetsRefreshed" && refresh.TargetSnapshot is {CanDispatch:true} && refresh.CurrentSourceSessionKey==bound.SessionKey && runtime.Snapshot.Source==bound)break;
            }
            var baseline=source.ReadSlice(new ReadSelection(["personalShopListing"]),deadline.Token);
            var field=baseline.Fields.GetValueOrDefault("personalShopListing");
            if(baseline.Source!=bound || field?.State!="Observed" || !field.Fresh(DateTimeOffset.UtcNow) || field.Value is null)throw new InvalidOperationException("FRESH_CLOSED_VIEWER_BASELINE_REQUIRED");
            before=field.Value.Value.Deserialize<PersonalShopListing>(HubProtocol.Json);
            if(before is null || !IsClosedEmptyViewerBaseline(before))throw new InvalidOperationException("EXISTING_VIEWER_PRESERVED_REFUSAL");
            readiness=await Send(new(){Command=ShopCommands.PrepareNativeReadiness,ExpectedSourceSessionKey=bound.SessionKey},deadline.Token);
            if(readiness.NativeReadiness is not {Ready:true,PendingMask:0,CurrentRuntimeCodeVerified:true} ready || ready.Source!=bound || !ready.Fresh(DateTimeOffset.UtcNow))throw new InvalidOperationException("CURRENT_STRICT_READONLY_PREFLIGHT_REQUIRED: "+readiness.Detail);
            refresh=await Send(new(){Command=ShopCommands.RefreshTargets,ExpectedSourceSessionKey=bound.SessionKey},deadline.Token);
            if(refresh.State!="TargetsRefreshed" || refresh.CurrentSourceSessionKey!=bound.SessionKey || runtime.Snapshot.Source!=bound || refresh.TargetSnapshot is not { } targets || targets.SourceSessionKey!=bound.SessionKey)throw new InvalidOperationException("FRESH_TARGET_REFRESH_REQUIRED");
            target=SelectTarget(targets,DateTimeOffset.UtcNow);
            Log(new{Kind="SELECTED_SINGLE_TARGET",At=DateTimeOffset.UtcNow,Target=target,SelfPosition=targets.SelfPosition,MaximumDistance,Before=before,MaximumNativeRequests=1});
            attempts++;
            response=await Send(new(){Command=ShopCommands.OpenShop,OwnerId=target.Id,ViewerDirection=direction,ExpectedSourceSessionKey=bound.SessionKey,TargetSnapshotId=targets.SnapshotId,ExpectedOwnerIdentity=target.LocalIdentity},deadline.Token);
        }
        catch(Exception error){errors.Add(error.ToString());}
        finally
        {
            if(backend is not null && runtime is not null && bound is not null)
            {
                using var settle=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try{finalState=await Send(new(){Command=ShopCommands.GetState,ExpectedSourceSessionKey=bound.SessionKey},settle.Token);settled=finalState.State=="State"&&finalState.CurrentSourceSessionKey==bound.SessionKey&&runtime.Snapshot.Source==bound&&finalState.WorkSettled&&!finalState.SessionBlocked&&!finalState.Scan.Running;
                    string stem=$"{bound.ProcessId}-{bound.CreationFileTime}";
                    settled &= !Directory.EnumerateFiles(@"D:\lineage\LC_PlayerShopListAction_v1\journal",stem+"*.pending.json").Any();
                    if(!settled)errors.Add("FINAL_BROKER_SETTLEMENT_NOT_CONFIRMED");}
                catch(Exception error){errors.Add("SETTLEMENT: "+error);}
            }
            deadline.Cancel();try{await watch;await keys;}catch(Exception error){errors.Add(error.ToString());}
            try{if(backend is not null)await backend.DisposeAsync();stopped=runtime is null||!runtime.IsRunning;}catch(Exception error){errors.Add(error.ToString());}
            lease?.Dispose();
        }
        bool success=attempts==1&&settled&&stopped&&errors.Count==0&&response is {NativeEntered:true,NativeReturned:true,ResponseVerified:true,SessionBlocked:false};
        var report=new{Version="85.0.0",At=DateTimeOffset.UtcNow,Success=success,IsAdministrator=true,BoundSource=bound,Before=before,Target=target,ViewerDirection=direction,MaximumDistance,ReadOnlyStartup=startup,Readiness=readiness,Refresh=refresh,Response=response,FinalState=finalState,RequestAttempts=attempts,MaximumNativeRequests=1,AutomaticRetries=0,InputSent=0,NavigationRequests=0,TradeCommands=0,ViewerCloseCommands=0,BrokerSettled=settled,Stopped=stopped,Errors=errors,ServerAcceptanceVerified=false};
        using(var file=new FileStream(Path.Combine(output,"result.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){JsonSerializer.Serialize(file,report,new JsonSerializerOptions(HubProtocol.Json){WriteIndented=true});file.Flush(true);}
        return success?0:1;
    }
}
