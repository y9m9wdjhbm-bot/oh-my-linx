using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class ShopNativeReadinessVerification
{
    private static readonly DateTimeOffset Epoch = new(2026,10,16,12,0,0,TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(2100,123456,@"C:\offline\LC.exe","0x10000000",
        ShopNativeHistoricalPackage.SupportedTimestamp,ShopNativeHistoricalPackage.SupportedImageSize,ShopNativeHistoricalPackage.SupportedSha,"readiness-offline-source");
    private const ulong Image=0x10000000, Hwnd=0x22000, Shop=0x33000, Owner=ulong.MaxValue;

    internal static OfflineCheck[] Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task<OfflineCheck[]> RunAsync()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action test)
        { try {test();checks.Add(new("native_readiness_"+name,true,"Synthetic readonly receipts/owned helper lifecycle only; no LC or native action."));}
          catch(Exception error){checks.Add(new("native_readiness_"+name,false,error.ToString()));} }
        async Task Async(string name,Func<Task> test)
        { try {await test();checks.Add(new("native_readiness_"+name,true,"Fake owned helper only; no process started."));}
          catch(Exception error){checks.Add(new("native_readiness_"+name,false,error.ToString()));} }
        ShopNativeReadiness Parse(List<JsonObject> rows,int exit=0,SourceIdentity? expected=null,DateTimeOffset? now=null)
            => ShopNativeReadinessProof.Parse(Text(rows),expected??Source,Hwnd,Shop,Owner,exit,Epoch,now??Epoch.AddMilliseconds(500),"probe-offline");
        Check("complete_fresh_five_body_preflight_is_ready_not_game_success",()=>{
            var result=Parse(Good());Need(result.Ready&&result.Fresh(Epoch.AddMilliseconds(500))&&result.Source==Source
                &&result.NativeRequests==0&&result.ReadOnly&&result.Code=="NativeReady",result.Detail);
            Need(!result.Fresh(result.ExpiresAt.AddTicks(1))&&!result.Fresh(result.SampledAt.AddTicks(-1)),"Freshness extended");
        });
        Check("noninvoked_ack_destructor_noaccess_does_not_gate_ready",()=>{
            var rows=Good();foreach(var row in rows.Where(r=>r["Kind"]!.GetValue<string>()=="NONINVOKED_CODE_OBSERVATION"))
                ((JsonObject)row["Data"]!)["Pages"]=JsonSerializer.SerializeToNode(new[]{new{Protect=1,Readable=false}});
            Need(Parse(rows).Ready,"Unused ACK/destructor page was promoted to prerequisite");
        });
        var mutations=new (string Name,Action<List<JsonObject>> Change)[] {
            ("network_missing_sample",r=>r.RemoveAll(x=>x["Kind"]!.GetValue<string>()=="NETWORK_SHARED_COUNTS_SAMPLE")),
            ("network_missing_ab",r=>r.RemoveAll(x=>x["Kind"]!.GetValue<string>()=="NETWORK_SHARED_COUNTS_AB")),
            ("network_control_mismatch",r=>Data(r,"NETWORK_SHARED_COUNTS_AB")["NetworkControl"]=0x55001UL),
            ("network_counts_not_alive",r=>Data(r,"NETWORK_SHARED_COUNTS_SAMPLE")["Alive"]=false),
            ("network_wrong_changed_claim",r=>Data(r,"NETWORK_SHARED_COUNTS_AB")["CountsChanged"]=false),
            ("end_flush_counter_wrong",r=>Data(r,"END")["LogDiskFlushesBeforeEnd"]=44),
            ("version_wrong",r=>Data(r,"START")["Version"]="16.0.0"),
            ("payload_wrong",r=>Data(r,"START")["NativePayloadVersion"]="16.0.0"),
            ("log_policy_wrong",r=>Data(r,"START")["LogPolicy"]="Unknown"),
            ("mode_buy",r=>Data(r,"START")["Mode"]="buy"),
            ("request_one",r=>Data(r,"START")["MaximumNativeRequests"]=1),
            ("helper_version",r=>Data(r,"START")["ManagedProgramVersion"]="16.0.1"),
            ("source_pid",r=>Data(r,"SOURCE")["Pid"]=2101),
            ("source_creation",r=>Data(r,"SOURCE")["Creation"]=123457L),
            ("source_base",r=>Data(r,"SOURCE")["Image"]=Image+0x1000),
            ("source_sha",r=>Data(r,"SOURCE")["Sha256"]=new string('0',64)),
            ("source_path",r=>Data(r,"SOURCE")["Path"]=@"C:\different\LC.exe"),
            ("source_owner",r=>Data(r,"SOURCE")["Owner"]=1UL),
            ("source_target",r=>Data(r,"SOURCE")["TargetOwner"]=1),
            ("source_handle",r=>Data(r,"SOURCE")["ReadOnlyProcessHandle"]=false),
            ("only_four_bodies",r=>Data(r,"CURRENT_NATIVE_CODE_GUARDS")["Bodies"]=4),
            ("missing_body_byte",r=>Data(r,"CURRENT_NATIVE_CODE_GUARDS")["Bytes"]=4261),
            ("deferred_mask",r=>Data(r,"CURRENT_NATIVE_CODE_GUARDS")["DeferredMask"]=1),
            ("noninvoked_gate",r=>Data(r,"NONINVOKED_CODE_OBSERVATION")["DispatchPrerequisite"]=true),
            ("ab_not_complete",r=>Data(r,"PREFLIGHT_AB_CLOSED")["EverySynchronousGuardBodyCurrentlyMatched"]=false),
            ("ab_copy_pointer",r=>Data(r,"PREFLIGHT_AB_CLOSED")["PacketCopy"]=Image+0x76BEB1),
            ("ab_owner",r=>Data(r,"PREFLIGHT_AB_CLOSED")["Owner64"]=3UL),
            ("ab_empty_proof",r=>Data(r,"PREFLIGHT_AB_CLOSED")["ProofCount"]=0),
            ("hook_installed",r=>Data(r,"READ_ONLY_PREFLIGHT_PASSED")["HooksInstalled"]=1),
            ("native_request",r=>Data(r,"READ_ONLY_PREFLIGHT_PASSED")["NativeRequests"]=1),
            ("game_verified",r=>Data(r,"READ_ONLY_PREFLIGHT_PASSED")["LiveShopQueryVerified"]=true),
            ("cleanup_false",r=>Data(r,"END")["CleanupConfirmed"]=false),
            ("native_result",r=>Data(r,"END")["NativeResult"]=new JsonObject()),
            ("native_returned",r=>Data(r,"END")["NativeAlreadyReturned"]=true),
            ("exit_mismatch",r=>Data(r,"END")["ExitCode"]=1),
            ("sequence_gap",r=>r[2]["Sequence"]=100),
            ("missing_source",r=>r.RemoveAll(x=>x["Kind"]!.GetValue<string>()=="SOURCE")),
            ("missing_ab",r=>r.RemoveAll(x=>x["Kind"]!.GetValue<string>()=="PREFLIGHT_AB_CLOSED")),
            ("missing_end",r=>r.RemoveAt(r.Count-1)),
            ("duplicate_pass",r=>r.Insert(r.Count-1,(JsonObject)r[^2].DeepClone())),
            ("intent_event",r=>r[3]["Kind"]="EVENT_INTENT_DURABLE"),
            ("deferred_event",r=>r[3]["Kind"]="PACKET_CODE_PENDING")
        };
        foreach(var mutation in mutations)Check("reject_"+mutation.Name,()=>{var rows=Good();mutation.Change(rows);Need(!Parse(rows).Ready,"Accepted "+mutation.Name);});
        Check("blocked_terminal_never_grants_ready",()=>{var rows=Good();Data(rows,"END")["SessionBlocked"]=true;var result=Parse(rows);Need(!result.Ready&&result.Code=="NativeReadinessBusy",result.Detail);});
        Check("old_passed_log_is_not_fresh_after_wait",()=>Need(!Parse(Good(),now:Epoch.AddSeconds(5)).Ready,"Old pass became current"));
        Check("prior_launch_log_rejected",()=>{
            var result=ShopNativeReadinessProof.Parse(Text(Good()),Source,Hwnd,Shop,Owner,0,Epoch.AddSeconds(1),Epoch.AddSeconds(2),"next-probe");
            Need(!result.Ready,"Previous log accepted by new launch");
        });
        Check("duplicate_json_property_rejected",()=>{
            string text=Text(Good()).Replace("\"Mode\":\"--preflight\"","\"Mode\":\"buy\",\"Mode\":\"--preflight\"");
            Need(!ShopNativeReadinessProof.Parse(text,Source,Hwnd,Shop,Owner,0,Epoch,Epoch.AddSeconds(1),"p").Ready,"Duplicate property accepted");
        });
        Check("missing_final_newline_rejected",()=>Need(!ShopNativeReadinessProof.Parse(Text(Good()).TrimEnd('\n'),Source,Hwnd,Shop,Owner,0,Epoch,Epoch.AddSeconds(1),"p").Ready,"Truncated END accepted"));
        Check("captured_current_helper_receipt_replay",()=>{
            var rows=ActualReceipt.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(x=>JsonNode.Parse(x)!.AsObject()).ToList();
            var original=Data(rows,"SOURCE");var first=DateTimeOffset.Parse(rows[0]["At"]!.GetValue<string>());var last=DateTimeOffset.Parse(rows[^1]["At"]!.GetValue<string>());
            var expected=new SourceIdentity(original["Pid"]!.GetValue<int>(),original["Creation"]!.GetValue<long>(),original["Path"]!.GetValue<string>(),
                "0x"+original["Image"]!.GetValue<ulong>().ToString("X"),0x6ACEDB9A,0x3F5F000,original["Sha256"]!.GetValue<string>(),"historical-replay-only");
            var result=ShopNativeReadinessProof.Parse(ActualReceipt,expected,original["Hwnd"]!.GetValue<ulong>(),original["Shop"]!.GetValue<ulong>(),original["Owner"]!.GetValue<ulong>(),0,first.AddSeconds(-1),last.AddMilliseconds(1),"historical-replay");
            Need(result.Ready&&result.ReadOnly&&result.NativeRequests==0,result.Detail);
        });
        await Async("actual_broker_start_recovery_preserved_then_readiness_rejects_without_plan_mutation",ActualBrokerAsync);
        await Async("dispose_joins_whole_readiness_before_binding_finishes",()=>OwnedReadinessDisposalAsync(false));
        await Async("dispose_joins_whole_readiness_after_cancelled_binding",()=>OwnedReadinessDisposalAsync(true));
        Check("oversize_log_rejected",()=>Need(!ShopNativeReadinessProof.Parse(new string(' ',ShopNativeReadinessProof.MaximumLogBytes+1),Source,Hwnd,Shop,Owner,0,Epoch,Epoch.AddSeconds(1),"p").Ready,"Oversize accepted"));
        Check("copy_noaccess_is_current_unavailable_without_initialization_claim",()=>{
            var result=Parse(Failure(),1);
            Need(result.Code=="NativeCodeUnavailable"&&!result.Ready&&result.FailureRva=="0x76BEB0"
                &&result.Detail.Contains("原進度")&&result.Detail.Contains("重新檢查")
                &&!result.Detail.Contains("初始化")&&!result.Detail.Contains("正常開啟")
                &&result.ReadOnly&&result.NativeRequests==0&&!result.Fresh(Epoch.AddMilliseconds(500)),result.Detail);
        });
        Check("prior_ready_does_not_override_later_copy_noaccess",()=>{
            var before=Parse(Good());Need(before.Ready,"Initial complete proof rejected");
            var rows=Failure();
            foreach(var row in rows)row["At"]=DateTimeOffset.Parse(row["At"]!.GetValue<string>()).AddSeconds(1).ToString("O");
            var after=Parse(rows,1,now:Epoch.AddMilliseconds(1500));
            Need(after.Source==before.Source&&after.Code=="NativeCodeUnavailable"&&!after.Ready
                &&after.ReadOnly&&after.NativeRequests==0&&after.SampledAt==default
                &&!after.Fresh(Epoch.AddMilliseconds(1500)),"Prior successful proof replaced a later unavailable observation");
        });
        Check("unavailable_presentation_preserves_progress_without_initialization_claim",()=>{
            var result=Parse(Failure(),1);
            var response=new ShopCommandResponse{State="Rejected",FailureCode=result.Code,FailureRva=result.FailureRva,
                FailureScope="Global",FailurePhase="BeforePublish",Detail=result.Detail,NativeReadiness=result,WorkSettled=true};
            foreach(bool force in new[]{false,true})
            {
                string detail=MarketFailurePresentation.Format(response,force);
                Need(detail.Contains("NativeCodeUnavailable")&&detail.Contains("0x76BEB0")&&detail.Contains("原進度")
                    &&!detail.Contains("初始化")&&!detail.Contains("正常開啟"),detail);
            }
        });
        foreach(var change in new[]{"different_page","foreign_image","guard_page","not_image","missing_page_flag"})
            Check("unavailable_"+change+"_is_not_initialization",()=>{
                var rows=Failure();var page=(JsonObject)Data(rows,"NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!;
                if(change=="different_page"){Data(rows,"NATIVE_CODE_PAGE_UNAVAILABLE")["Rva"]="0x72D300";Data(rows,"ERROR")["Rva"]="0x72D300";page["Base"]=Image+0x72D000;}
                else if(change=="foreign_image")page["AllocationBase"]=Image+0x1000;
                else if(change=="guard_page")page["Protect"]=0x101;
                else if(change=="not_image")page["Type"]=0x20000;
                else page.Remove("Protect");
                var result=Parse(rows,1);
                Need(!result.Ready&&result.ReadOnly&&result.NativeRequests==0
                    &&result.Code==(change=="missing_page_flag"?"NativeReadinessUnknown":"NativeCodeUnavailable"),
                    "Unavailable page classification or strict missing-field rejection changed: "+result.Detail);
            });
        Check("readonly_arguments_have_no_force_or_query",()=>{
            var binding=new ShopActionSourceBinding(Source,Epoch,Shop,Owner,1,2,3,4);
            ProcessStartInfo info=ShopBroker.ReadinessStart(@"C:\offline\helper.exe",binding,Hwnd,@"C:\offline\new.jsonl");
            Need(!info.UseShellExecute&&info.CreateNoWindow&&info.ArgumentList.Count==9&&info.ArgumentList[0]=="--preflight"
                &&info.ArgumentList[^1]=="0"&&!info.ArgumentList.Contains("--force-dispatch")&&!info.ArgumentList.Contains("buy")&&!info.ArgumentList.Contains("sell"),"Readonly arguments changed");
        });
        await Async("cancel_before_start_launches_nothing",async()=>{
            var flight=new ShopNativeHelperFlight();int starts=0;using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{await ShopNativeReadinessRunner.RunAsync(flight,()=>{starts++;return new FakeProcess();},TimeSpan.FromSeconds(1),cancel.Token);throw new Exception("Expected cancellation");}
            catch(OperationCanceledException){}
            Need(starts==0&&!flight.IsActive,"Cancelled check launched helper");
        });
        await Async("cancel_wait_retains_owned_flight_until_exit",async()=>{
            var flight=new ShopNativeHelperFlight();var process=new FakeProcess();using var cancel=new CancellationTokenSource();int starts=0;
            var wait=ShopNativeReadinessRunner.RunAsync(flight,()=>{starts++;return process;},TimeSpan.FromSeconds(5),cancel.Token);cancel.Cancel();
            try{await wait;throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
            Need(flight.IsActive&&process.Disposals==0&&starts==1,"Cancelled wait lost helper ownership");
            try{flight.RequireAvailable();throw new Exception("Expected busy");}catch(ShopNativeHelperBusyException){}
            var joined=flight.WaitUntilExitAsync();process.Exit(0);await joined.WaitAsync(TimeSpan.FromSeconds(1));
            Need(!flight.IsActive&&process.Disposals==1&&starts==1,"Late exit restarted or leaked helper");
        });
        await Async("timeout_retains_flight_and_never_retries",async()=>{
            var flight=new ShopNativeHelperFlight();var process=new FakeProcess();int starts=0;
            try{await ShopNativeReadinessRunner.RunAsync(flight,()=>{starts++;return process;},TimeSpan.Zero,CancellationToken.None);throw new Exception("Expected timeout");}catch(ShopNativeHelperWaitTimeoutException){}
            Need(starts==1&&flight.IsActive,"Timed out check retried or released ownership");
            var joined=flight.WaitUntilExitAsync();process.Exit(1);await joined.WaitAsync(TimeSpan.FromSeconds(1));Need(starts==1,"Retried on late exit");
        });
        return checks.ToArray();
    }

    private static List<JsonObject> Good()
    {
        var rows=new List<JsonObject>();
        Add(rows,"START",new { Version="15.0.0",NativePayloadVersion="15.0.0",LogPolicy="DurableDispatchIntent_Result_End_Error;BufferedVerboseV2",Mode="--preflight",ManagedProgramVersion="16.0.2",NativeProtocolVersion=15,NativeGuardContract=15,CurrentOwnerBits=64,OpenProcessAccess="0x410",DeferredCodePolicy=ShopNativeHistoricalPackage.LegacyStrictV16Policy,MaximumNativeRequests=0,MouseMessages=0,KeyMessages=0,TouchCalls=0,ForegroundChanges=0,DirectSelectedOwnerWrites=0 });
        Add(rows,"SOURCE",new { Pid=Source.ProcessId,Creation=Source.CreationFileTime,Image,Hwnd,Tid=2201,Shop,Owner,TargetOwner=0,Path=Source.ImagePath,Sha256=Source.DiskSha256,Timestamp="0x6ACEDB9A",ImageSize="0x3F5F000",ReadOnlyProcessHandle=true });
        Add(rows,"CURRENT_NATIVE_CODE_GUARDS",new { Bodies=5,Bytes=4262,DeferredMask=0,DeferredCodeBytes=0,CurrentPendingBytesClaimedMatched=false,BorrowedScalarSource=true,AdditionalDestructorCalls=0 });
        foreach(string rva in new[]{"0x73B790","0x104870"})Add(rows,"NONINVOKED_CODE_OBSERVATION",new{Rva=rva,DispatchPrerequisite=false,BodyCallableClaimed=false,HistoricalBytesSubstituted=false,Pages=Array.Empty<object>()});
        var ownA=new {Strong=2,Weak=1};var ownB=new {Strong=3,Weak=2};
        Add(rows,"NETWORK_SHARED_COUNTS_SAMPLE",new{Phase="A",NetworkControl=0x55000UL,AddressHex="0x55008",Length=8,Counts=ownA,Alive=true,CountEqualityRequired=false});
        Add(rows,"NETWORK_SHARED_COUNTS_SAMPLE",new{Phase="B",NetworkControl=0x55000UL,AddressHex="0x55008",Length=8,Counts=ownB,Alive=true,CountEqualityRequired=false});
        Add(rows,"NETWORK_SHARED_COUNTS_AB",new{NetworkControl=0x55000UL,CountsAddress="0x55008",Length=8,OwnCountsA=ownA,OwnCountsB=ownB,CountsChanged=true,StableControlVtableAbClosed=true,CountEqualityRequired=false});
        Add(rows,"PREFLIGHT_AB_CLOSED",new { Network=0x44000UL,NetworkControl=0x55000UL,NetworkChild=0x66000UL,OwnCountsA=ownA,OwnCountsB=ownB,CountEqualityRequired=false,Owner64=Owner,CurrentShop=Shop,TargetOwner=0,PacketCreate=Image+0x72D300,PacketCopy=Image+0x76BEB0,ProofCount=42,ReadCalls=200,ReadBytes=14000,SubsystemCount=16,DeferredMask=0,DeferredCodeBytes=0,EverySynchronousGuardBodyCurrentlyMatched=true,ColdCodeBytesPending=false,AsyncAckNotInvokedByHelper=true });
        Add(rows,"READ_ONLY_PREFLIGHT_PASSED",new { NativeRequests=0,HooksInstalled=0,DeferredMask=0,NativeCallableVerified=false,LiveShopQueryVerified=false });
        End(rows,0);return rows;
    }
    private static List<JsonObject> Failure()
    {
        var rows=Good().Take(2).ToList();
        Add(rows,"NATIVE_CODE_PAGE_UNAVAILABLE",new { Rva="0x76BEB0",Page=new { Base=Image+0x76B000,AllocationBase=Image,Size=4096,State=4096,Protect=1,Type=16777216 },Phase="Preflight",Code="NativeCodeUnavailable" });
        Add(rows,"ERROR",new { Type="NativePreflightException",Message="Native code is not currently readable/executable MEM_IMAGE: 0x76BEB0",Code="NativeCodeUnavailable",Phase="Preflight",Rva="0x76BEB0" });
        End(rows,1);return rows;
    }
    private static void End(List<JsonObject> rows,int code)=>Add(rows,"END",new { ExitCode=code,LogRecordsBeforeEnd=rows.Count,LogDiskFlushesBeforeEnd=rows.Count(r=>r["DiskFlush"]!.GetValue<bool>()),LogStreamFlushesBeforeEnd=0,LogBufferedRecordsBeforeEnd=rows.Count(r=>!r["DiskFlush"]!.GetValue<bool>()),CleanupConfirmed=true,SessionBlocked=false,PostCodeConfirmed=false,EventResultDurable=false,BorrowedSourceContractVerified=false,NativeAlreadyReturned=false,AckObserved=false,ListReceivedVerified=false,NativeResult=(object?)null });
    private static void Add(List<JsonObject> rows,string kind,object data)=>rows.Add(new JsonObject { ["At"]=Epoch.AddMilliseconds(rows.Count*10).ToString("O"),["Sequence"]=rows.Count+1,["Kind"]=kind,["DiskFlush"]=kind is "END" or "ERROR",["Data"]=JsonSerializer.SerializeToNode(data) });
    private static JsonObject Data(List<JsonObject> rows,string kind)=>(JsonObject)rows.First(r=>r["Kind"]!.GetValue<string>()==kind)["Data"]!;
    private static string Text(List<JsonObject> rows)=>string.Join("\n",rows.Select(r=>r.ToJsonString()))+"\n";
    private static void Need(bool value,string detail){if(!value)throw new InvalidOperationException(detail);}
    private sealed class FakeProcess:IShopNativeHelperProcess
    {
        private readonly TaskCompletionSource<bool> done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited{get;private set;} public int ExitCode{get;private set;} internal int Disposals;
        public Task WaitForExitAsync()=>done.Task;
        public void Dispose()=>Disposals++;
        internal void Exit(int code){ExitCode=code;HasExited=true;done.TrySetResult(true);}
    }

    private static async Task ActualBrokerAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"LC-V82-readiness-SYNTHETIC-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var fake=new NoReadSource();await using var runtime=new HubRuntime(fake,Path.Combine(root,"runtime"),enableTransport:false);runtime.SetLogging(false);
        using var reader=new CurrentLcSource(profileDirectory:Path.Combine(root,"absent-profile"));
        await using var broker=new ShopBroker(runtime,reader,Path.Combine(root,"shops"));
        var source=Source with {DiskSha256=new string('A',64),SessionKey="synthetic-unsupported"};var now=DateTimeOffset.UtcNow;
        var targets=new ShopTargetSnapshot{SnapshotId="synthetic-targets",SourceSessionKey=source.SessionKey,RefreshedAt=now,SelfId=11,SelfPosition=new(100,100),Owners=[new(){Id=21,LocalIdentity="synthetic-owner",Name="synthetic",Position=new(100,101),ObservedAt=now,ShopState=ShopEligibilityPolicy.ShopActionObserved,ActionType=".?AVShopAction@lineage@@",ShopStateSampledAt=now,ShopStateEvidence=ShopEligibilityPolicy.ActionEvidence}]};
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        void Set(object o,string n,object v)=>o.GetType().GetField(n,flags)!.SetValue(o,v);
        Set(runtime,"latest",new HubSnapshot{Source=source});Set(runtime,"sampler",Task.CompletedTask);
        Set(broker,"targetSource",source);Set(broker,"targetSnapshot",targets);
        var saved=(ShopScanProgressStore)typeof(ShopBroker).GetField("savedScans",flags)!.GetValue(broker)!;
        string plan=saved.StartNew(source,targets,true).PlanId;
        var oldScan=new MarketScanState{JobId="preserved-old-job",Detail="preserved",Running=false};Set(broker,"scan",oldScan);
        string[] Files()=>Directory.GetFiles(Path.Combine(root,"shops","scan-progress"),"*",SearchOption.AllDirectories).Order().Select(p=>p+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        var before=Files();int recoveries=0;bool pending=true;
        broker.NativePendingObserver=s=>new(s,pending?ShopNativePendingState.ReturnedPostCodePending:ShopNativePendingState.None,pending?"old-returned":null,"synthetic pending");
        broker.NativePendingRecovery=(s,t)=>{recoveries++;pending=false;return Task.CompletedTask;};
        var check=await broker.HandleAsync(new(){Command=ShopCommands.CheckNativeReadiness,ExpectedSourceSessionKey=source.SessionKey},CancellationToken.None);
        Need(check.NativeReadiness is {Ready:false,Code:"NativeBuildUnsupported"}&&recoveries==0&&pending,"Explicit check recovered or dispatched pending");
        foreach(string command in new[]{ShopCommands.ScanVisible,ShopCommands.ContinueSavedScan})
        {
            pending=true;
            var result=await broker.HandleAsync(new(){Command=command,ForceDispatch=true,ExpectedSourceSessionKey=source.SessionKey,TargetSnapshotId=targets.SnapshotId,ExpectedScanPlanId=plan},CancellationToken.None);
            Need(result.State=="Rejected"&&result.NativeReadiness is {Ready:false,Code:"NativeBuildUnsupported"}&&result.Scan==oldScan&&result.SavedScan?.PlanId==plan&&result.WorkSettled,result.Detail);
            Need(Files().SequenceEqual(before),"Readiness rejection mutated saved plan");
        }
        Need(recoveries==2&&!pending&&fake.Reads==0,"Original start recovery changed or source read unexpectedly");
        pending=true;
        var prepare=new ShopCommandRequest {Command=ShopCommands.PrepareNativeReadiness,ExpectedSourceSessionKey=source.SessionKey};
        var prepared=await broker.HandleAsync(prepare,CancellationToken.None);
        Need(recoveries==3&&!pending&&fake.Reads==0&&prepared.NativeReadiness is {Ready:false,Code:"NativeBuildUnsupported"},
            "Patrol preparation did not preserve the original recovery before checking capability");
        Need(prepared.Scan==oldScan&&prepared.SavedScan?.PlanId==plan&&Files().SequenceEqual(before),"Preparation changed a scan plan");
        await broker.HandleAsync(prepare,CancellationToken.None);
        Need(recoveries==3,"Accepted preparation replayed its recovery for the same request ID");
        Set(runtime,"sampler",Task.CompletedTask);
    }
    private static async Task OwnedReadinessDisposalAsync(bool cancellationObserved)
    {
        string root=Path.Combine(Path.GetTempPath(),"LC-V82-readiness-disposal-SYNTHETIC-"+Guid.NewGuid().ToString("N"));
        var fake=new NoReadSource();await using var runtime=new HubRuntime(fake,Path.Combine(root,"runtime"),enableTransport:false);runtime.SetLogging(false);
        using var reader=new CurrentLcSource(profileDirectory:Path.Combine(root,"absent-profile"));
        var broker=new ShopBroker(runtime,reader,Path.Combine(root,"shops"));
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var nativeGate=(SemaphoreSlim)typeof(ShopBroker).GetField("nativeGate",flags)!.GetValue(broker)!;
        var stop=(CancellationTokenSource)typeof(ShopBroker).GetField("stop",flags)!.GetValue(broker)!;
        var gate=typeof(ShopBroker).GetField("gate",flags)!.GetValue(broker)!;
        var bindingCanFinish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelSeen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await nativeGate.WaitAsync();
        async Task<ShopCommandResponse> BindingThenReceipt()
        {
            try
            {
                if(cancellationObserved)
                {
                    try{await Task.Delay(Timeout.Infinite,stop.Token);}catch(OperationCanceledException){cancelSeen.TrySetResult();}
                }
                await bindingCanFinish.Task;
                return new(){State="Cancelled"};
            }
            finally{nativeGate.Release();}
        }
        Task<ShopCommandResponse> owned=BindingThenReceipt();
        lock(gate)typeof(ShopBroker).GetMethod("TrackNativeReadiness",flags)!.Invoke(broker,[owned]);
        Task dispose=broker.DisposeAsync().AsTask();
        if(cancellationObserved)await cancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(25);
        bool joined=!dispose.IsCompleted;
        bindingCanFinish.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Need(joined&&owned.IsCompletedSuccessfully&&fake.Reads==0,"Broker disposed before whole readiness work completed");
    }
    private sealed class NoReadSource:IMemorySource
    {
        internal int Reads;
        public ReadResult Read(CancellationToken token){Reads++;throw new InvalidOperationException("No LC reads in synthetic broker test");}
        public void Dispose(){}
    }

    private const string ActualReceipt = "{\"At\":\"2026-10-16T12:10:24.8433366+00:00\",\"Sequence\":1,\"Kind\":\"START\",\"DiskFlush\":false,\"Data\":{\"Version\":\"15.0.0\",\"ManagedProgramVersion\":\"16.0.2\",\"NativeProtocolVersion\":15,\"NativeGuardContract\":15,\"NativePayloadVersion\":\"15.0.0\",\"PostCodeRecheckContract\":1,\"PostCodeRecheckMaximumMilliseconds\":1000,\"ReturnedLifecycleRecoveryContract\":1,\"SubmitPreflightRecheckMaximumMilliseconds\":250,\"Mode\":\"--preflight\",\"MaximumNativeRequests\":0,\"MouseMessages\":0,\"KeyMessages\":0,\"TouchCalls\":0,\"ForegroundChanges\":0,\"DirectSelectedOwnerWrites\":0,\"SharedOwnershipCounterUpdates\":true,\"OpenProcessAccess\":\"0x410\",\"DeferredCodePolicy\":\"Current 38A7 exact seven A/B bodies; every invoked body must be currently readable/executable; no deferred NOACCESS call; immediate strict full postbody; maxmask0\",\"Binding\":\"Caller-supplied runtime identity, independently verified\",\"CurrentOwnerBits\":64,\"LogPolicy\":\"DurableDispatchIntent_Result_End_Error;BufferedVerboseV2\",\"ResearchOnly\":true}}\n{\"At\":\"2026-10-16T12:10:25.2919659+00:00\",\"Sequence\":2,\"Kind\":\"SOURCE\",\"DiskFlush\":false,\"Data\":{\"Pid\":22784,\"Creation\":134366161905656319,\"Image\":140702580801536,\"Hwnd\":722590,\"Tid\":20572,\"Shop\":1773506616576,\"Owner\":75099602,\"TargetOwner\":0,\"Path\":\"C:\\\\Program Files (x86)\\\\NCSOFT\\\\Lineage Classic\\\\LC.exe\",\"Sha256\":\"38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1\",\"Timestamp\":\"0x6ACEDB9A\",\"ImageSize\":\"0x3F5F000\",\"ReadOnlyProcessHandle\":true}}\n{\"At\":\"2026-10-16T12:10:25.3478646+00:00\",\"Sequence\":3,\"Kind\":\"CURRENT_NATIVE_CODE_GUARDS\",\"DiskFlush\":false,\"Data\":{\"Bodies\":5,\"Bytes\":4262,\"DeferredMask\":0,\"DeferredCodeBytes\":0,\"CurrentPendingBytesClaimedMatched\":false,\"BorrowedScalarSource\":true,\"AdditionalDestructorCalls\":0}}\n{\"At\":\"2026-10-16T12:10:25.3522093+00:00\",\"Sequence\":4,\"Kind\":\"NONINVOKED_CODE_OBSERVATION\",\"DiskFlush\":false,\"Data\":{\"Rva\":\"0x73B790\",\"Role\":\"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies\",\"DispatchPrerequisite\":false,\"BodyCallableClaimed\":false,\"HistoricalBytesSubstituted\":false,\"Pages\":[{\"Rva\":\"0x73B790\",\"Length\":73,\"Page\":{\"Base\":140702588383232,\"AllocationBase\":140702580801536,\"AllocationProtect\":128,\"PartitionId\":0,\"Size\":4096,\"State\":4096,\"Protect\":32,\"Type\":16777216},\"CurrentlyReadableExecutable\":true,\"CurrentBytesMatched\":true}]}}\n{\"At\":\"2026-10-16T12:10:25.3844628+00:00\",\"Sequence\":5,\"Kind\":\"NONINVOKED_CODE_OBSERVATION\",\"DiskFlush\":false,\"Data\":{\"Rva\":\"0x104870\",\"Role\":\"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies\",\"DispatchPrerequisite\":false,\"BodyCallableClaimed\":false,\"HistoricalBytesSubstituted\":false,\"Pages\":[{\"Rva\":\"0x104870\",\"Length\":80,\"Page\":{\"Base\":140702581866496,\"AllocationBase\":140702580801536,\"AllocationProtect\":128,\"PartitionId\":0,\"Size\":20480,\"State\":4096,\"Protect\":32,\"Type\":16777216},\"CurrentlyReadableExecutable\":true,\"CurrentBytesMatched\":true}]}}\n{\"At\":\"2026-10-16T12:10:25.3874097+00:00\",\"Sequence\":6,\"Kind\":\"NETWORK_SHARED_COUNTS_SAMPLE\",\"DiskFlush\":false,\"Data\":{\"Phase\":\"A\",\"NetworkControl\":1773415025072,\"AddressHex\":\"0x19CE7C5C9B8\",\"Length\":8,\"Counts\":{\"Strong\":1,\"Weak\":1},\"Alive\":true,\"CountEqualityRequired\":false}}\n{\"At\":\"2026-10-16T12:10:25.3941105+00:00\",\"Sequence\":7,\"Kind\":\"NETWORK_SHARED_COUNTS_SAMPLE\",\"DiskFlush\":false,\"Data\":{\"Phase\":\"B\",\"NetworkControl\":1773415025072,\"AddressHex\":\"0x19CE7C5C9B8\",\"Length\":8,\"Counts\":{\"Strong\":1,\"Weak\":1},\"Alive\":true,\"CountEqualityRequired\":false}}\n{\"At\":\"2026-10-16T12:10:25.3952091+00:00\",\"Sequence\":8,\"Kind\":\"NETWORK_SHARED_COUNTS_AB\",\"DiskFlush\":false,\"Data\":{\"NetworkControl\":1773415025072,\"CountsAddress\":\"0x19CE7C5C9B8\",\"Length\":8,\"OwnCountsA\":{\"Strong\":1,\"Weak\":1},\"OwnCountsB\":{\"Strong\":1,\"Weak\":1},\"CountsChanged\":false,\"StableControlVtableAbClosed\":true,\"CountEqualityRequired\":false}}\n{\"At\":\"2026-10-16T12:10:25.4012679+00:00\",\"Sequence\":9,\"Kind\":\"PREFLIGHT_AB_CLOSED\",\"DiskFlush\":false,\"Data\":{\"ProofCount\":87,\"ReadCalls\":208,\"ReadBytes\":13680,\"Owner64\":75099602,\"CurrentShop\":1773506616576,\"TargetOwner\":0,\"Network\":1773415025088,\"NetworkControl\":1773415025072,\"NetworkChild\":1773422703936,\"PacketCreate\":140702588326656,\"PacketCopy\":140702588583600,\"SubsystemCount\":64,\"DeferredMask\":0,\"DeferredCodeBytes\":0,\"EverySynchronousGuardBodyCurrentlyMatched\":true,\"ColdCodeBytesPending\":false,\"AsyncAckNotInvokedByHelper\":true,\"OwnCountsA\":{\"Strong\":1,\"Weak\":1},\"OwnCountsB\":{\"Strong\":1,\"Weak\":1},\"CountEqualityRequired\":false}}\n{\"At\":\"2026-10-16T12:10:25.4070033+00:00\",\"Sequence\":10,\"Kind\":\"READ_ONLY_PREFLIGHT_PASSED\",\"DiskFlush\":false,\"Data\":{\"NativeRequests\":0,\"HooksInstalled\":0,\"DeferredMask\":0,\"NativeCallableVerified\":false,\"LiveShopQueryVerified\":false}}\n{\"At\":\"2026-10-16T12:10:25.4096502+00:00\",\"Sequence\":11,\"Kind\":\"END\",\"DiskFlush\":true,\"Data\":{\"ExitCode\":0,\"PostCodeConfirmed\":false,\"EventResultDurable\":false,\"BorrowedSourceContractVerified\":false,\"NativeAlreadyReturned\":false,\"AckObserved\":false,\"NativeResult\":null,\"CleanupConfirmed\":true,\"SessionBlocked\":false,\"PendingJournal\":\"D:\\\\lineage\\\\LC_PlayerShopListAction_v1\\\\journal\\\\22784-134366161905656319.pending.json\",\"SingleTrialPendingJournal\":\"D:\\\\lineage\\\\LC_PlayerShopListAction_v1\\\\journal\\\\22784-134366161905656319.v15-singletrial.pending.json\",\"SingleTrialPendingSchema\":\"V15SingleTrialPendingV1\",\"ListReceivedVerified\":false,\"RemoteModuleUnloadProven\":false,\"EntireHookTailExitProven\":false,\"LogRecordsBeforeEnd\":10,\"LogDiskFlushesBeforeEnd\":0,\"LogStreamFlushesBeforeEnd\":0,\"LogBufferedRecordsBeforeEnd\":10,\"LogWriteDurationMsBeforeEnd\":188.01780000000002,\"LogIoWriteDurationMsBeforeEnd\":1.3497999999999999,\"LogSerializationDurationMsBeforeEnd\":186.66800000000003}}\n";
    internal static int RunReport(string output)
    {
        var checks=Run();string assembly=typeof(ShopNativeReadinessVerification).Assembly.Location;
        string sourceRoot=@"D:\lineage\LC_UpdateExecution_20261021_v1\market-v1\LC_PlayerMarket_v85\source-v1";
        var names=new[]{"engine/Core/ShopNativeReadiness.cs","engine/Core/ShopCommandContracts.cs","engine/Broker/ShopNativeReadinessProof.cs","engine/Broker/ShopBroker.NativeReadiness.cs","engine/Broker/ShopBroker.cs","ShopNativeReadinessVerification.cs","Program.cs"};
        var hashes=names.ToDictionary(name=>Path.GetFullPath(Path.Combine(sourceRoot,name)),name=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot,name)))));
        var report=new { Success=checks.All(c=>c.Passed),IsAdministrator=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),Checks=checks,TestedAssembly=assembly,TestedAssemblySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),SourceHashes=hashes,LCRead=0,GameInput=0,NativeCalls=0 };
        using(var stream=new FileStream(output,FileMode.CreateNew))JsonSerializer.Serialize(stream,report,new JsonSerializerOptions{WriteIndented=true});
        return report.Success?0:1;
    }
}
