using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemoryHub.Service;

internal sealed record GuardReconcileInput(string Role,string Path,string Sha256);
internal sealed record GuardReconcileManifest(int Schema,string ShopRoot,string NativeDirectory,string RequestId,
    SourceIdentity Source,GuardReconcileInput[] Inputs);
internal sealed record NativeGuardRejectedRequestEvidence(string RequestId,SourceIdentity Source,ulong OwnerId,string Direction,
    string NativeLog,string NativeSha256,string SessionSha256,string ResponseSha256,bool NoEntryConfirmed,GuardReconcileInput[] Inputs,
    bool SourceMatched,bool SafeToSettle,string FailureCode,string FailureScope,string FailurePhase);
internal sealed record GuardReconcileResult(string RequestId,string State,string BackupDirectory,
    NativeGuardRejectedRecoveryEvidence Proof,bool NativeEntered,bool NativeReturned,bool GameInput,bool Replayed);

// This reconciles durable local records only. It never launches a helper, reads
// LC, changes a guard, or creates another shop request.
internal static class ShopNativeGuardRejectedReconciliation
{
    private sealed record Bound(SourceIdentity Source,MarketOwner Owner,string Direction,ulong CurrentOwner,string NativeLog);
    private const string SharedJournal=@"D:\lineage\LC_PlayerShopListAction_v1\journal";
    private static readonly string[] Roles=["Pending","Request","Response","Native","Intent","Plan","OriginalEvent","ForceArchive","SingleArchive"];

    internal static NativeGuardRejectedRequestEvidence VerifyResolvedRequest(string shopRoot,string nativeDirectory,
        string requestId,SourceIdentity expectedSource)
        => VerifyResolvedRequestCore(shopRoot,nativeDirectory,requestId,expectedSource,null);

    // Filesystem-only test seam: production has no archive redirection argument.
    // Tests retain the complete original native receipt and validate copied
    // archive contents through exactly the same path/content contract.
    internal static NativeGuardRejectedRequestEvidence VerifyResolvedRequestIsolated(string shopRoot,string nativeDirectory,
        string requestId,SourceIdentity expectedSource,IReadOnlyDictionary<string,string> archiveCopies)
    {
        string prefix=Path.GetFullPath(shopRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        Need(archiveCopies.Count==2 && archiveCopies.Values.All(p=>Path.GetFullPath(p).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)),
            "Isolated archive copies must belong to the isolated shop root.");
        return VerifyResolvedRequestCore(shopRoot,nativeDirectory,requestId,expectedSource,archiveCopies);
    }

    private static NativeGuardRejectedRequestEvidence VerifyResolvedRequestCore(string shopRoot,string nativeDirectory,
        string requestId,SourceIdentity expectedSource,IReadOnlyDictionary<string,string>? archiveCopies)
    {
        shopRoot=Path.GetFullPath(shopRoot);nativeDirectory=Path.GetFullPath(nativeDirectory);string key=Key(requestId);
        string sessions=Path.Combine(shopRoot,"sessions"),stem=$"{expectedSource.ProcessId}-{expectedSource.CreationFileTime}.force-{key}";
        Need(!File.Exists(Path.Combine(sessions,stem+".pending.json")),"Matching session is still pending.");
        string[] resolved=Directory.EnumerateFiles(sessions,stem+".resolved-*.json").Take(2).ToArray();
        Need(resolved.Length==1,"Resolved request session is absent or ambiguous.");
        string session=resolved[0],request=Path.Combine(shopRoot,"requests",key+".request.json"),response=Path.Combine(shopRoot,"requests",key+".response.json");
        var first=new[]{Pin("Pending",session),Pin("Request",request),Pin("Response",response)};
        var bound=BindRequest(shopRoot,nativeDirectory,requestId,expectedSource,session,request,response,false);
        var native=Pin("Native",bound.NativeLog);
        var proof=Parse(bound.NativeLog,bound,expectedSource);
        Need(ShopNativeOutcome.IsGuardRejected(proof),"Resolved request lacks complete guard-refusal proof.");
        var rows=Rows(bound.NativeLog);var end=Data(rows,"END");var state=end.GetProperty("NativeResult");
        string forcePath=Data(rows,"JOURNAL_SETTLED").GetProperty("Path").GetString()!;
        string singlePath=Data(rows,"SINGLE_TRIAL_JOURNAL_SETTLED").GetProperty("Path").GetString()!;
        string forceFile=archiveCopies is null?forcePath:archiveCopies[forcePath],singleFile=archiveCopies is null?singlePath:archiveCopies[singlePath];
        var force=Pin("ForceArchive",forceFile);var single=Pin("SingleArchive",singleFile);
        ValidateArchive(forceFile,forcePath,false,expectedSource,bound,state,bound.NativeLog,end,archiveCopies is not null);
        ValidateArchive(singleFile,singlePath,true,expectedSource,bound,state,bound.NativeLog,end,archiveCopies is not null);
        var inputs=first.Concat(new[]{native,force,single}).ToArray();
        foreach(var input in inputs)Check(input);
        return new(requestId,expectedSource,bound.Owner.Id,bound.Direction,bound.NativeLog,native.Sha256,first[0].Sha256,first[2].Sha256,true,inputs,
            true,true,"NativeGuardRejected","Page","NativeGuard");
    }

    internal static GuardReconcileResult Run(string manifestPath,Func<bool>? busyCheck=null,Action<string>? checkpoint=null,
        bool isolatedTest=false)
    {
        var manifestPin=Pin("Manifest",manifestPath,256*1024);
        var manifest=Read<GuardReconcileManifest>(manifestPath,256*1024);
        Need(manifest.Schema==1 && manifest.Source is not null && manifest.Inputs.Length==Roles.Length
            && manifest.Inputs.Select(x=>x.Role).Distinct(StringComparer.Ordinal).Count()==Roles.Length
            && Roles.All(role=>manifest.Inputs.Any(x=>x.Role==role)),"Reconciliation input schema differs.");
        string root=Path.GetFullPath(manifest.ShopRoot),nativeDirectory=Path.GetFullPath(manifest.NativeDirectory),key=Key(manifest.RequestId);
        RejectLinks(root);RejectLinks(nativeDirectory);
        GuardReconcileInput I(string role)=>manifest.Inputs.Single(x=>x.Role==role);
        string P(string role)=>Path.GetFullPath(I(role).Path);
        string pending=P("Pending");
        string expectedPending=ShopForceDispatchPolicy.PendingPath(root,$"{manifest.Source.ProcessId}-{manifest.Source.CreationFileTime}",manifest.RequestId);
        Need(Same(pending,expectedPending),"Pending filename is not the exact request hash.");
        string backup=Path.Combine(root,"guard-reconciliation",key),archived=pending[..^".pending.json".Length]+".resolved-guard-"+key+".json";
        string evidencePending=File.Exists(pending)?pending:archived;
        if(evidencePending!=pending)Need(File.Exists(Path.Combine(backup,"proof.json")),"Pending disappeared without a prepared transaction.");
        void CheckInputs(){foreach(var input in manifest.Inputs)Check(input.Role=="Pending"?input with{Path=evidencePending}:input);}
        CheckInputs();
        var bound=BindRequest(root,nativeDirectory,manifest.RequestId,manifest.Source,evidencePending,P("Request"),P("Response"),true);
        Need(Same(bound.NativeLog,P("Native")),"Pinned native LOG differs from pending.");
        var native=Parse(P("Native"),bound,manifest.Source);Need(ShopNativeOutcome.IsGuardRejected(native),"Original receipt is not strict V17 guard refusal.");
        var rows=Rows(P("Native"));var end=Data(rows,"END");var state=end.GetProperty("NativeResult");
        ValidateArchive(P("ForceArchive"),Data(rows,"JOURNAL_SETTLED").GetProperty("Path").GetString()!,false,
            manifest.Source,bound,state,P("Native"),end,isolatedTest);
        ValidateArchive(P("SingleArchive"),Data(rows,"SINGLE_TRIAL_JOURNAL_SETTLED").GetProperty("Path").GetString()!,true,
            manifest.Source,bound,state,P("Native"),end,isolatedTest);
        Need(Same(P("Intent"),Path.Combine(root,"scan-progress","source-intents",key+".intent.json")),"Source-intent filename differs.");
        var intent=Read<ScanIntentEvidence>(P("Intent"),32768);
        var plan=Read<SavedScanPlan>(P("Plan"),512*1024);
        var original=Read<ScanProgressEvent>(P("OriginalEvent"),64*1024);
        Need(intent.Source==manifest.Source && intent.RequestId==manifest.RequestId && !intent.Settled && !intent.Entered && !intent.Returned
            && plan.Source==manifest.Source && plan.PlanId==intent.PlanId && original.PlanId==plan.PlanId
            && plan.Owners.Count(x=>x.Id==bound.Owner.Id && x.LocalIdentity==bound.Owner.LocalIdentity)==1,
            "Original source, plan or owner differs.");
        Need(Same(Path.GetDirectoryName(P("Plan"))!,Path.Combine(root,"scan-progress"))
            && Same(P("OriginalEvent"),Path.Combine(root,"scan-progress",plan.PlanId+".events",original.Sequence.ToString("D8",CultureInfo.InvariantCulture)+".json"))
            && original.RequestId==manifest.RequestId && original.OwnerId==bound.Owner.Id && original.OwnerIdentity==bound.Owner.LocalIdentity
            && original.Direction==bound.Direction && original.Kind=="Result" && original.SessionBlocked
            && !original.NativeEntered && !original.NativeReturned && !original.ResponseVerified,"Original blocked result differs.");
        using(var request=Document(P("Request"),128*1024))
            Need(request.RootElement.GetProperty("request").GetProperty("targetSnapshotId").GetString()==plan.OriginalSnapshotId,"Original snapshot differs.");
        var proof=new NativeGuardRejectedRecoveryEvidence(manifest.Source,plan.PlanId,manifest.RequestId,bound.Owner.Id,bound.Owner.LocalIdentity,
            bound.Direction,original.Sequence,I("OriginalEvent").Sha256,I("Native").Sha256,I("Pending").Sha256,I("Request").Sha256,
            I("Response").Sha256,I("ForceArchive").Sha256,I("SingleArchive").Sha256,manifestPin.Sha256);
        bool Busy()=> (busyCheck??ShopStartupRecovery.OtherWorkRunning)();
        void Stable()
        {
            Need(!Busy(),"Another market/helper work exists; no reconciliation.");
            if(!isolatedTest)CheckShared(manifest.Source);
            Check(manifestPin);CheckInputs();
        }
        Stable();
        using var lease=new FileStream(Path.Combine(root,"scan-progress","run.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var scans=ShopScanProgressStore.OpenReadOnly(Path.Combine(root,"scan-progress"));
        var owners=ShopSearchedOwnerStore.OpenReadOnly(Path.Combine(root,"searched-owners"));
        Need(scans.Error is null && scans.Source==manifest.Source && scans.PlanId==plan.PlanId,"Current stored plan differs.");
        owners.ValidateUnpublishedRecovery(manifest.RequestId,manifest.Source,bound.Owner.Id,bound.Owner.LocalIdentity,bound.Direction);
        Directory.CreateDirectory(backup);
        foreach(var input in manifest.Inputs)Backup(input.Role=="Pending"?evidencePending:input.Path,Path.Combine(backup,input.Role+Path.GetExtension(input.Path)),input.Sha256);
        Backup(manifestPath,Path.Combine(backup,"input-manifest.json"),manifestPin.Sha256);
        string ledgerBackup=Path.Combine(backup,"owner-ledger-manifest.json");
        if(!File.Exists(ledgerBackup))
        {
            string ownerRoot=Path.Combine(root,"searched-owners");var ownerFiles=Directory.EnumerateFiles(ownerRoot,"*",SearchOption.AllDirectories)
                .Where(p=>!p.EndsWith(".lock",StringComparison.OrdinalIgnoreCase)&&!p.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase)).Take(20001).ToArray();
            Need(ownerFiles.Length<=20000 && ownerFiles.Sum(p=>new FileInfo(p).Length)<=128L*1024*1024,"Owner evidence backup bound exceeded.");
            var ledger=ownerFiles.Select(p=>Pin(Path.GetRelativePath(ownerRoot,p),p)).ToArray();
            foreach(var input in ledger)Backup(input.Path,Path.Combine(backup,"owner-ledger",input.Role),input.Sha256);
            WriteNew(ledgerBackup,ledger);
        }
        else foreach(var input in Read<GuardReconcileInput[]>(ledgerBackup,8*1024*1024))
            Need(Hash(Path.Combine(backup,"owner-ledger",input.Role))==input.Sha256,"Original owner backup changed.");
        string proofPath=Path.Combine(backup,"proof.json");
        if(!File.Exists(proofPath))WriteNew(proofPath,proof);else Need(Read<NativeGuardRejectedRecoveryEvidence>(proofPath,64*1024)==proof,"Prepared transaction differs.");
        Stable();checkpoint?.Invoke("ProofSaved");
        scans.RecoverGuardRejected(proof,"完整 V17 原收據已確認 callback 拒絕、未進入查詢且清理完成；保留原失敗，不重送本計畫此頁。");
        checkpoint?.Invoke("EventAppended");Stable();
        owners.SettleUnpublishedRecovery(manifest.RequestId,manifest.Source,bound.Owner.Id,bound.Direction);
        checkpoint?.Invoke("OwnerSettled");Stable();
        scans.Reload();Need(scans.Error is null && !scans.BlocksSource(manifest.Source) && !owners.BlocksSource(manifest.Source),"Other unresolved evidence remains.");
        // Preserve the original pending bytes, original failure response, original
        // terminal event and frozen diagnostic report. Only this exact file moves.
        if(File.Exists(pending))File.Move(pending,archived);checkpoint?.Invoke("PendingArchived");
        var result=new GuardReconcileResult(manifest.RequestId,"RecoveredStopped",backup,proof,false,false,false,false);
        string completed=Path.Combine(backup,"completed.json");
        if(!File.Exists(completed))WriteNew(completed,result);else Need(Read<GuardReconcileResult>(completed,128*1024)==result,"Committed result differs.");
        return result;
    }

    private static Bound BindRequest(string root,string nativeDirectory,string requestId,SourceIdentity source,
        string pendingPath,string requestPath,string responsePath,bool historicalBlocked)
    {
        string key=Key(requestId);
        Need(Same(requestPath,Path.Combine(root,"requests",key+".request.json"))
            && Same(responsePath,Path.Combine(root,"requests",key+".response.json")),"Request/response filenames differ.");
        using var pending=Document(pendingPath,32768);var p=pending.RootElement;
        Need(p.GetProperty("state").GetString()=="ForceNativeLaunchIntent_NoAutomaticReplay" && p.GetProperty("forceDispatch").GetBoolean()
            && p.GetProperty("requestId").GetString()==requestId && p.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json)==source,
            "Pending source or request differs.");
        var owner=p.GetProperty("owner").Deserialize<MarketOwner>(HubProtocol.Json)??throw new InvalidDataException("Owner missing.");
        string direction=p.GetProperty("viewerDirection").GetString()!,native=p.GetProperty("nativeLog").GetString()!;
        Need(direction is "ViewerBuy" or "ViewerSell" && p.GetProperty("targetOwner").GetUInt64()==owner.Id
            && owner.Id>0 && owner.LocalIdentity.Length>0 && Same(Path.GetDirectoryName(native)!,nativeDirectory)
            && Path.GetFileName(native).Length==38 && Guid.TryParseExact(Path.GetFileNameWithoutExtension(native),"N",out _)
            && native.EndsWith(".jsonl",StringComparison.Ordinal),"Pending page or native path differs.");
        using var accepted=Document(requestPath,128*1024);var a=accepted.RootElement;
        var request=a.GetProperty("request").Deserialize<ShopCommandRequest>(HubProtocol.Json);
        Need(request is not null && request.RequestId==requestId && request.Command==ShopCommands.OpenShop && request.ForceDispatch
            && request.ExpectedSourceSessionKey==source.SessionKey && request.OwnerId==owner.Id && request.ExpectedOwnerIdentity==owner.LocalIdentity
            && request.ViewerDirection==direction && a.GetProperty("state").GetString()=="ScanChild_NoAutomaticReplay"
            && a.GetProperty("signature").GetString()==Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,HubProtocol.Json)))),
            "Accepted request identity/signature differs.");
        using var response=Document(responsePath,16*1024*1024);var r=response.RootElement;
        Need(r.GetProperty("requestId").GetString()==requestId
            && (r.GetProperty("currentSourceSessionKey").ValueKind==JsonValueKind.Null || r.GetProperty("currentSourceSessionKey").GetString()==source.SessionKey)
            && !r.GetProperty("nativeEntered").GetBoolean() && !r.GetProperty("nativeReturned").GetBoolean()
            && !r.GetProperty("responseVerified").GetBoolean() && !r.GetProperty("emptyPageObserved").GetBoolean()
            && r.GetProperty("emptyPageObservation").ValueKind==JsonValueKind.Null,"Response contains query evidence or a different source.");
        if(historicalBlocked)Need(r.GetProperty("state").GetString()=="Unverified" && r.GetProperty("sessionBlocked").GetBoolean()
            && !r.GetProperty("workSettled").GetBoolean() && r.GetProperty("failureCode").GetString()=="NativeOutcomeUnknown"
            && r.GetProperty("failureScope").GetString()=="Session" && r.GetProperty("failurePhase").GetString()=="Native", "Original failure differs.");
        else Need(r.GetProperty("state").GetString()=="NativeRejected" && !r.GetProperty("sessionBlocked").GetBoolean()
            && r.GetProperty("failureCode").GetString()=="NativeGuardRejected" && r.GetProperty("failureScope").GetString()=="Page"
            && r.GetProperty("failurePhase").GetString()=="NativeGuard","Response is not a classified guard refusal.");
        Need(r.GetProperty("failureRva").ValueKind==JsonValueKind.Null,"Guard refusal claims a specific RVA.");
        return new(source,owner,direction,p.GetProperty("currentOwnerIdRaw").GetUInt64(),native);
    }

    private static ShopNativeOutcome Parse(string path,Bound bound,SourceIdentity source)
    {
        var pin=Pin("Native",path);ulong image=Convert.ToUInt64(source.ImageBase.Replace("0x","",StringComparison.OrdinalIgnoreCase),16);
        var value=ShopNativeOutcome.ParseFile(path,source,image,bound.Owner.Id,1);
        var rows=Rows(path);var start=Data(rows,"START");var nativeSource=Data(rows,"SOURCE");
        Need(start.GetProperty("Mode").GetString()==(bound.Direction=="ViewerBuy"?"buy":"sell")
            && nativeSource.GetProperty("Owner").GetUInt64()==bound.CurrentOwner
            && rows.Count(r=>r.GetProperty("Kind").GetString()=="FORCE_DISPATCH")==1,"Native request mode/current-owner differs.");
        Check(pin);return value;
    }

    private static void ValidateArchive(string path,string originalPath,bool single,SourceIdentity source,Bound bound,
        JsonElement state,string nativeLog,JsonElement end,bool isolated)
    {
        Need(isolated || Same(path,originalPath),"Helper archive must be the exact original path.");
        var rows=Rows(path,32768);Need(rows.Length==2,"Helper archive must contain one intent and settlement.");
        var intent=rows[0];var settled=rows[1];
        Need(intent.GetProperty("State").GetString()=="UNRESOLVED_INTENT" && settled.GetProperty("State").GetString()=="SETTLED"
            && ShopNativeV17Contract.IsManagedProducer17(intent)
            && ShopNativeV17Contract.SameManagedProducer(intent,Data(Rows(nativeLog),"START"))
            && intent.GetProperty("ColdCodePolicyId").GetString()==(ShopNativePackage.SupportsBuild(source)?ShopNativeCurrentB47CPolicy.Id:ShopNativeLegacy79D1Package.SupportsBuild(source)?ShopNativeCurrent79D1Policy.Id:ShopNativeColdPolicy.Id)
            && intent.GetProperty("ColdCodeCertificateSha256").GetString()==(ShopNativePackage.SupportsBuild(source)?ShopNativeCurrentB47CPolicy.CertificateSha256:ShopNativeLegacy79D1Package.SupportsBuild(source)?ShopNativeCurrent79D1Policy.CertificateSha256:ShopNativeColdPolicy.CertificateSha256)
            && intent.GetProperty("Pid").GetInt32()==source.ProcessId && intent.GetProperty("Creation").GetInt64()==source.CreationFileTime
            && intent.GetProperty("Image").GetUInt64()==Convert.ToUInt64(source.ImageBase.Replace("0x","",StringComparison.OrdinalIgnoreCase),16)
            && intent.GetProperty("Mode").GetInt32()==(bound.Direction=="ViewerBuy"?1:2)
            && intent.GetProperty("Owner").GetUInt64()==bound.CurrentOwner && intent.GetProperty("TargetOwner").GetUInt64()==bound.Owner.Id
            && Same(intent.GetProperty("Log").GetString()!,nativeLog) && settled.GetProperty("CleanupConfirmed").GetBoolean()
            && JsonElement.DeepEquals(settled.GetProperty("Completion"),state),"Archived helper identity/completion differs.");
        var nativeSource=Data(Rows(nativeLog),"SOURCE");
        Need(intent.GetProperty("Shop").GetUInt64()==nativeSource.GetProperty("Shop").GetUInt64()
            && intent.GetProperty("Hwnd").GetUInt64()==nativeSource.GetProperty("Hwnd").GetUInt64(),"Helper archive source differs.");
        Need(DateTimeOffset.TryParse(intent.GetProperty("At").GetString(),out var began)
            && DateTimeOffset.TryParse(settled.GetProperty("At").GetString(),out var at) && at>=began,"Helper archive time differs.");
        if(single)Need(intent.GetProperty("Schema").GetString()=="V15SingleTrialPendingV1"
            && settled.GetProperty("Schema").GetString()=="V15SingleTrialPendingV1" && intent.GetProperty("Version").GetString()=="15.0.0"
            && Same(intent.GetProperty("PendingJournal").GetString()!,end.GetProperty("PendingJournal").GetString()!)
            && intent.GetProperty("NoAutomaticRetransmission").GetBoolean() && settled.GetProperty("PositiveNoEntry").GetBoolean()
            && !settled.GetProperty("PostCodeConfirmed").GetBoolean(),"Single-trial no-entry proof differs.");
    }

    private static void CheckShared(SourceIdentity source)
    {
        RejectLinks(SharedJournal);string stem=$"{source.ProcessId}-{source.CreationFileTime}";
        Need(!Directory.EnumerateFiles(SharedJournal,stem+"*.pending.json").Any(),"A native pending remains.");
        string transaction=Path.Combine(SharedJournal,stem+".delayed-postcode-recovery-v1.transaction.json");
        Need(!File.Exists(transaction)||ShopPostCodeRecoveryProof.CommittedTransaction(transaction,source,SharedJournal),"Native recovery transaction remains.");
    }
    internal static GuardReconcileInput Pin(string role,string path,int maximum=16*1024*1024)
    {var before=new FileInfo(path);long length=before.Length;var modified=before.LastWriteTimeUtc;using var stream=Open(path,maximum);string hash=Convert.ToHexString(SHA256.HashData(stream));var after=new FileInfo(path);Need(after.Length==length&&after.LastWriteTimeUtc==modified,"Evidence changed while reading.");return new(role,Path.GetFullPath(path),hash);}
    internal static string Hash(string path)=>Pin("Hash",path).Sha256;
    private static void Check(GuardReconcileInput input)=>Need(Pin(input.Role,input.Path).Sha256==input.Sha256,"Evidence hash changed: "+input.Role);
    private static FileStream Open(string path,int maximum)
    {RejectLinks(path);var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(f.Length<=0||f.Length>maximum){f.Dispose();throw new InvalidDataException("Evidence length out of bounds.");}return f;}
    private static JsonDocument Document(string path,int maximum)
    {using var f=Open(path,maximum);var d=JsonDocument.Parse(f);if(!Unique(d.RootElement)){d.Dispose();throw new InvalidDataException("Duplicate evidence property.");}return d;}
    private static T Read<T>(string path,int maximum){using var d=Document(path,maximum);return d.RootElement.Deserialize<T>(HubProtocol.Json)??throw new InvalidDataException("Evidence missing.");}
    private static JsonElement[] Rows(string path,int maximum=4*1024*1024)
    {
        using var f=Open(path,maximum);using var reader=new StreamReader(f,Encoding.UTF8,true);var result=new List<JsonElement>();string? line;
        while((line=reader.ReadLine()) is not null){if(string.IsNullOrWhiteSpace(line))continue;Need(result.Count<8192,"Too many evidence rows.");using var d=JsonDocument.Parse(line);Need(Unique(d.RootElement),"Duplicate evidence property.");result.Add(d.RootElement.Clone());}
        return result.ToArray();
    }
    private static JsonElement Data(JsonElement[] rows,string kind)=>rows.Single(r=>r.GetProperty("Kind").GetString()==kind).GetProperty("Data");
    private static bool Unique(JsonElement value)
    {if(value.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in value.EnumerateObject())if(!keys.Add(p.Name)||!Unique(p.Value))return false;}else if(value.ValueKind==JsonValueKind.Array)foreach(var v in value.EnumerateArray())if(!Unique(v))return false;return true;}
    private static bool Same(string left,string right)=>string.Equals(Path.GetFullPath(left),Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);
    private static string Key(string id){Need(id.Length is >0 and <=128 && !id.Any(char.IsControl),"Request id invalid.");return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));}
    private static void Backup(string source,string target,string hash)
    {RejectLinks(target);Directory.CreateDirectory(Path.GetDirectoryName(target)!);if(!File.Exists(target))File.Copy(source,target);Need(Hash(target)==hash,"Backup differs.");}
    private static void WriteNew<T>(string path,T value)
    {RejectLinks(path);using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(f,value,HubProtocol.Json);f.Flush(true);}
    private static void RejectLinks(string path)
    {for(string? at=Path.GetFullPath(path);at is not null;at=Path.GetDirectoryName(at)){try{Need((File.GetAttributes(at)&FileAttributes.ReparsePoint)==0,"Linked evidence path.");}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}}}
    private static void Need(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
}
