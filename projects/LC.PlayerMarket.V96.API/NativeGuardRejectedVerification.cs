using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class NativeGuardRejectedVerification
{
    private const string OriginalRequest="scan-9456DC8EA9EDDF9214F1CF4583444EB8-35";
    private static string Fixture(string name)
    {var assembly=Assembly.GetExecutingAssembly();using var stream=assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(x=>x.EndsWith("guard-rejected-"+name+".jsonl",StringComparison.Ordinal)))!;using var reader=new StreamReader(stream);return reader.ReadToEnd();}
    private static List<JsonObject> Rows()=>Fixture("native").Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(x=>JsonNode.Parse(x)!.AsObject()).ToList();
    private static JsonObject Data(List<JsonObject> rows,string kind)=>rows.Single(x=>x["Kind"]!.GetValue<string>()==kind)["Data"]!.AsObject();
    private static SourceIdentity Source=>JsonNode.Parse(Fixture("session"))!["source"]!.Deserialize<SourceIdentity>(HubProtocol.Json)!;
    private static string Text(List<JsonObject> rows)=>string.Join("\n",rows.Select(x=>x.ToJsonString()))+"\n";
    private static ShopNativeOutcome Parse(List<JsonObject> rows)=>ShopNativeOutcome.Parse(Text(rows),Source,0x7FF7DB2E0000,74013643,1);
    private static void State(List<JsonObject> rows,string key,JsonNode value)
    {foreach(var pair in new[]{("EVENT_RESULT","State"),("NATIVE_FINAL_STATE","State"),("END","NativeResult")})Data(rows,pair.Item1)[pair.Item2]![key]=value.DeepClone();}
    private static void Normalize(List<JsonObject> rows)
    {for(int i=0;i<rows.Count;i++)rows[i]["Sequence"]=i+1;var end=Data(rows,"END");int disk=rows.Take(rows.Count-1).Count(r=>r["DiskFlush"]!.GetValue<bool>());end["LogRecordsBeforeEnd"]=rows.Count-1;end["LogDiskFlushesBeforeEnd"]=disk;end["LogBufferedRecordsBeforeEnd"]=rows.Count-1-disk;}

    internal static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action action){try{action();checks.Add(new("native_guard_rejected_"+name,true,"Exact retained V17 receipt; isolated file replay; no LC/helper/input."));}catch(Exception ex){checks.Add(new("native_guard_rejected_"+name,false,ex.ToString()));}}
        void Reject(string name,Action<List<JsonObject>> mutate)=>Check(name,()=>{var rows=Rows();mutate(rows);Normalize(rows);Need(!ShopNativeOutcome.IsGuardRejected(Parse(rows)),"Mutated evidence classified as guard rejection: "+name);});
        Check("actual_receipt_closed_noentry",()=>{var p=Parse(Rows());Need(ShopNativeOutcome.IsGuardRejected(p)&&p.SafeToSettle&&p.NoEntryConfirmed&&!p.Entered&&!p.Returned,p.Detail);});
        foreach(var key in new[]{"Entered","Returned","Stage","Active","Exception","ExceptionStage","ExceptionAddress","ExceptionAccessAddress",
            "ExceptionAccessKind","ExceptionCount","ReleaseExceptionCode","ReleaseExceptionAddress","LeaseAcquired","LeaseReleaseAttempted",
            "LeaseReleaseCompleted","RequestMetadataBefore","RequestMetadataAfter","RequestUnchanged","DestructorCalls","PostCodeConfirmed"})
        {string field=key;Reject("reject_nonzero_"+key,r=>State(r,field,JsonValue.Create(1)!));}
        foreach(var pair in new[]{("Completed",0),("Claimed",0),("Status",-4),("Sequence",2),("ObservedTid",18805),("Nonce",7),("Callback",0),("Module",0)})
        {var p=pair;Reject("reject_state_"+p.Item1,r=>State(r,p.Item1,JsonValue.Create(p.Item2)!));}
        Reject("reject_nondurable_result",r=>Data(r,"END")["EventResultDurable"]=false);
        Reject("reject_false_cleanup",r=>Data(r,"END")["CleanupConfirmed"]=false);
        Reject("reject_session_blocked",r=>Data(r,"END")["SessionBlocked"]=true);
        Reject("reject_disarmed_claim",r=>Data(r,"NATIVE_FINAL_STATE")["DisarmedBeforeClaim"]=true);
        foreach(var pair in new[]{("Code","NativeCodeUnavailable"),("Phase","BeforePublish"),("Type","NativePreflightException"),("Message","different failure")})
        {var p=pair;Reject("reject_error_"+p.Item1,r=>Data(r,"ERROR")[p.Item1]=p.Item2);}
        Reject("reject_error_rva",r=>Data(r,"ERROR")["Rva"]="0x72D300");
        Reject("reject_duplicate_error",r=>r.Insert(r.FindIndex(x=>x["Kind"]!.GetValue<string>()=="ERROR"),r.Single(x=>x["Kind"]!.GetValue<string>()=="ERROR").DeepClone().AsObject()));
        foreach(string kind in new[]{"JOURNAL_SETTLED","SINGLE_TRIAL_JOURNAL_SETTLED"})
        {string k=kind;Reject("reject_missing_"+k,r=>r.Remove(r.Single(x=>x["Kind"]!.GetValue<string>()==k)));Reject("reject_path_"+k,r=>Data(r,k)["Path"]="D:\\lineage\\unrelated.resolved-00000000000000000000000000000000.jsonl");}
        foreach(string kind in new[]{"PACKET_CODE_CONFIRMED","BORROWED_SOURCE_VERIFIED"})
        {string k=kind;Reject("reject_false_success_"+k,r=>{var row=r.Single(x=>x["Kind"]!.GetValue<string>()=="ERROR").DeepClone().AsObject();row["Kind"]=k;row["DiskFlush"]=false;r.Insert(r.Count-1,row);});}
        Reject("reject_post_recheck",r=>Data(r,"EVENT_RESULT")["PostCodeRecheck"]!["Attempts"]=1);
        Reject("reject_wrong_cert",r=>Data(r,"END")["ColdCodeCertificateSha256"]=new string('0',64));
        Reject("reject_wrong_build",r=>Data(r,"SOURCE")["Sha256"]=new string('A',64));
        Reject("reject_wrong_target",r=>Data(r,"SOURCE")["TargetOwner"]=74013644);
        Check("force_continues_nonforce_stops_sourcechange_stops",()=>
        {var p=Parse(Rows());var response=p.Failure!.Attach(new(){State="NativeRejected"});Need(ShopScanPolicy.Decide(response,true,true)==ShopScanDisposition.Continue&&ShopScanPolicy.Decide(response,true)==ShopScanDisposition.StopBatch&&ShopScanPolicy.Decide(response,false,true)==ShopScanDisposition.StopBatch,"Policy boundaries differ.");});
        Check("new_guard_event_skips_same_plan_page",()=>
        {var f=new Files(false);f.Scans.NativeGuardRejected(OriginalRequest,"strict test");f.Scans.Result(OriginalRequest,Parse(Rows()).Failure!.Attach(new(){State="NativeRejected"}));var replay=new ShopScanProgressStore(f.ScanRoot);Need(!replay.BlocksSource(Source)&&replay.PendingPages(f.Targets,true).All(x=>x!=f.Page)&&replay.View(Source,f.Targets,false,false,true)!.SearchedPages==0,"Guard page became searchable or counted successful.");});
        Check("reconcile_preserves_failure_appends_and_idempotent",()=>
        {var f=new Files();var before=Hash(f.OriginalEvent);var response=Hash(f.Response);var result=f.Run();Need(result.State=="RecoveredStopped"&&Hash(f.OriginalEvent)==before&&Hash(f.Response)==response&&!File.Exists(f.Pending),"Original failure changed or pending remains.");Need(f.Run()==result,"Second reconciliation differs.");var replay=new ShopScanProgressStore(f.ScanRoot);Need(!replay.BlocksSource(Source)&&replay.PendingPages(f.Targets,true).All(x=>x!=f.Page)&&replay.View(Source,f.Targets,false,false,true)!.SearchedPages==0,"Reconciled page was retried/confirmed.");});
        foreach(string stage in new[]{"ProofSaved","EventAppended","OwnerSettled","PendingArchived"})
        {string s=stage;Check("interrupted_"+stage+"_resumes_only_same_transaction",()=>{var f=new Files();try{f.Run(p=>{if(p==s)throw new IOException("injected transaction interruption");});}catch(IOException){}var result=f.Run();Need(result.State=="RecoveredStopped"&&!File.Exists(f.Pending),"Interrupted reconciliation did not close.");});}
        Check("busy_keeps_original_pending",()=>{var f=new Files();RejectAction(()=>ShopNativeGuardRejectedReconciliation.Run(f.Manifest,()=>true,isolatedTest:true));Need(File.Exists(f.Pending)&&!Directory.Exists(Path.Combine(f.Root,"guard-reconciliation")),"Busy check wrote recovery.");});
        Check("changed_pinned_native_keeps_pending",()=>{var f=new Files();File.AppendAllText(f.Native,"\n");RejectAction(()=>f.Run());Need(File.Exists(f.Pending),"Changed input cleared pending.");});
        Check("wrong_archive_completion_keeps_pending",()=>{var f=new Files();var lines=File.ReadAllLines(f.ForceArchive).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonNode.Parse(x)!).ToArray();lines[1]["Completion"]!["Entered"]=1;File.WriteAllText(f.ForceArchive,string.Join("\n",lines.Select(x=>x.ToJsonString()))+"\n");f.SaveManifest();RejectAction(()=>f.Run());Need(File.Exists(f.Pending),"Contradictory archive cleared pending.");});
        Check("false_single_positive_noentry_keeps_pending",()=>{var f=new Files();var lines=File.ReadAllLines(f.SingleArchive).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonNode.Parse(x)!).ToArray();lines[1]["PositiveNoEntry"]=false;File.WriteAllText(f.SingleArchive,string.Join("\n",lines.Select(x=>x.ToJsonString()))+"\n");f.SaveManifest();RejectAction(()=>f.Run());Need(File.Exists(f.Pending),"False noentry archive cleared pending.");});
        Check("resolved_request_fullreceipt_positive_six_hashes",()=>
        {var f=new Files();f.ResolveForAudit();var proof=f.Audit();Need(proof.RequestId==OriginalRequest&&proof.OwnerId==74013643&&proof.Direction==ShopPageModes.ViewerBuy
            &&proof.Source==Source&&proof.SourceMatched&&proof.SafeToSettle&&proof.NoEntryConfirmed&&proof.Inputs.Length==6
            &&proof.Inputs.Select(x=>x.Role).Distinct().Count()==6&&proof.Inputs.All(x=>Hash(x.Path)==x.Sha256),"Resolved full receipt or six input hashes differ.");});
        Check("resolved_request_null_background_source_still_bound",()=>{var f=new Files();f.ResolveForAudit();var r=JsonNode.Parse(File.ReadAllText(f.Response))!;r["currentSourceSessionKey"]=null;File.WriteAllText(f.Response,r.ToJsonString());Need(f.Audit().Source==Source,"Original full source binding was replaced by background null.");});
        foreach(string key in new[]{"ownerId","viewerDirection","expectedSourceSessionKey"})
        {string field=key;Check("resolved_request_wrong_"+field+"_rejected",()=>{var f=new Files();f.ResolveForAudit();var r=JsonNode.Parse(File.ReadAllText(f.Request))!;if(field=="ownerId")r["request"]![field]=74013644;else r["request"]![field]="wrong";var changed=r["request"]!.Deserialize<ShopCommandRequest>(HubProtocol.Json)!;r["signature"]=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(changed,HubProtocol.Json))));File.WriteAllText(f.Request,r.ToJsonString());RejectAction(()=>f.Audit());});}
        Check("resolved_request_still_pending_rejected",()=>{var f=new Files();RejectAction(()=>f.Audit());});
        Check("resolved_request_returned_mutation_rejected",()=>{var f=new Files();f.ResolveForAudit();var r=JsonNode.Parse(File.ReadAllText(f.Response))!;r["nativeReturned"]=true;File.WriteAllText(f.Response,r.ToJsonString());RejectAction(()=>f.Audit());});
        Check("resolved_archive_contradiction_rejected",()=>{var f=new Files();f.ResolveForAudit();var rows=File.ReadAllLines(f.SingleArchive).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonNode.Parse(x)!).ToArray();rows[1]["PositiveNoEntry"]=false;File.WriteAllText(f.SingleArchive,string.Join("\n",rows.Select(x=>x.ToJsonString())));RejectAction(()=>f.Audit());});
        return checks.ToArray();
    }

    internal static int RunReport(string output)
    {
        var checks=Run();string assembly=Assembly.GetExecutingAssembly().Location;
        var report=new{Schema="V82NativeGuardRejectedVerificationV1",Success=checks.All(x=>x.Passed),IsAdministrator=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            Passed=checks.Count(x=>x.Passed),Failed=checks.Count(x=>!x.Passed),Checks=checks,TestedAssembly=assembly,TestedAssemblySHA256=Hash(assembly),LCRead=0,NativeCalls=0,GameInput=0};
        using var f=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(f,report,HubProtocol.Json);f.Flush(true);return report.Success?0:1;
    }
    private sealed class Files
    {
        internal readonly string Root,NativeRoot,Native,Pending,Response,OriginalEvent,ForceArchive,SingleArchive,Manifest,ScanRoot;
        internal readonly string Request;private readonly string Intent,Plan;
        internal readonly ShopScanProgressStore Scans;internal readonly ShopTargetSnapshot Targets;internal readonly SavedScanPage Page;
        internal Files(bool blocked=true)
        {
            Root=Path.Combine(Path.GetTempPath(),"LC-V82-guard-reconcile",Guid.NewGuid().ToString("N"),"shops");NativeRoot=Path.Combine(Root,"native");Directory.CreateDirectory(NativeRoot);Directory.CreateDirectory(Path.Combine(Root,"sessions"));Directory.CreateDirectory(Path.Combine(Root,"requests"));
            Native=Path.Combine(NativeRoot,"58b2b95e45f14df596fb44168a494548.jsonl");File.WriteAllText(Native,Fixture("native"));
            var p=JsonNode.Parse(Fixture("session"))!.AsObject();p["nativeLog"]=Native;var owner=p["owner"]!.Deserialize<MarketOwner>(HubProtocol.Json)!;
            string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OriginalRequest)));
            Pending=ShopForceDispatchPolicy.PendingPath(Root,$"{Source.ProcessId}-{Source.CreationFileTime}",OriginalRequest);File.WriteAllText(Pending,p.ToJsonString());
            Targets=new(){SnapshotId="isolated-guard-targets",SourceSessionKey=Source.SessionKey,SelfId=109537929,SelfPosition=new(32600,32760),RefreshedAt=DateTimeOffset.UtcNow,Owners=[owner]};
            ScanRoot=Path.Combine(Root,"scan-progress");Scans=new(ScanRoot);var owners=new ShopSearchedOwnerStore(Path.Combine(Root,"searched-owners"),ScanRoot);var plan=Scans.StartNew(Source,Targets,true);Page=new(owner.Id,owner.LocalIdentity,ShopPageModes.ViewerBuy);Scans.BeginAttempt(Page,OriginalRequest);Scans.NativeIntent(OriginalRequest);
            owners.Begin(Source,owner,Page.Direction,OriginalRequest,true);
            var request=new ShopCommandRequest{RequestId=OriginalRequest,Command=ShopCommands.OpenShop,ForceDispatch=true,ExpectedSourceSessionKey=Source.SessionKey,OwnerId=owner.Id,ExpectedOwnerIdentity=owner.LocalIdentity,ViewerDirection=Page.Direction,TargetSnapshotId=Targets.SnapshotId};
            Request=Path.Combine(Root,"requests",key+".request.json");Write(Request,new{State="ScanChild_NoAutomaticReplay",Request=request,Signature=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,HubProtocol.Json))))});
            Response=Path.Combine(Root,"requests",key+".response.json");var response=new ShopCommandResponse{RequestId=OriginalRequest,CurrentSourceSessionKey=Source.SessionKey,State="Unverified",SessionBlocked=true,FailureCode="NativeOutcomeUnknown",FailureScope="Session",FailurePhase="Native"};Write(Response,response);
            if(blocked)Scans.Result(OriginalRequest,response);
            Plan=Directory.GetFiles(ScanRoot,"*.plan.json").Single();OriginalEvent=Directory.GetFiles(Path.Combine(ScanRoot,plan.PlanId+".events"),"*.json").Order(StringComparer.Ordinal).Last();
            Intent=Path.Combine(ScanRoot,"source-intents",key+".intent.json");ForceArchive=Path.Combine(Root,"force-archive.jsonl");SingleArchive=Path.Combine(Root,"single-archive.jsonl");
            foreach(var pair in new[]{("force",ForceArchive),("single",SingleArchive)}){var lines=Fixture(pair.Item1).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(x=>JsonNode.Parse(x)!).ToArray();lines[0]["Log"]=Native;File.WriteAllText(pair.Item2,string.Join("\n",lines.Select(x=>x.ToJsonString()))+"\n");}
            Manifest=Path.Combine(Root,"reconcile-input.json");SaveManifest();
        }
        internal void SaveManifest()=>Write(Manifest,new GuardReconcileManifest(1,Root,NativeRoot,OriginalRequest,Source,
            new[]{("Pending",Pending),("Request",Request),("Response",Response),("Native",Native),("Intent",Intent),("Plan",Plan),("OriginalEvent",OriginalEvent),("ForceArchive",ForceArchive),("SingleArchive",SingleArchive)}.Select(p=>ShopNativeGuardRejectedReconciliation.Pin(p.Item1,p.Item2)).ToArray()));
        internal GuardReconcileResult Run(Action<string>? checkpoint=null)=>ShopNativeGuardRejectedReconciliation.Run(Manifest,()=>false,checkpoint,true);
        internal void ResolveForAudit()
        {Write(Response,Parse(Rows()).Failure!.Attach(new(){RequestId=OriginalRequest,State="NativeRejected",CurrentSourceSessionKey=Source.SessionKey}));File.Move(Pending,Pending[..^".pending.json".Length]+".resolved-"+Guid.NewGuid().ToString("N")+".json");}
        internal NativeGuardRejectedRequestEvidence Audit()
        {var rows=Rows();return ShopNativeGuardRejectedReconciliation.VerifyResolvedRequestIsolated(Root,NativeRoot,OriginalRequest,Source,new Dictionary<string,string>{
            [Data(rows,"JOURNAL_SETTLED")["Path"]!.GetValue<string>()]=ForceArchive,
            [Data(rows,"SINGLE_TRIAL_JOURNAL_SETTLED")["Path"]!.GetValue<string>()]=SingleArchive});}
    }
    private static void Write<T>(string path,T value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,HubProtocol.Json));
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void RejectAction(Action action){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Need(rejected,"Invalid reconciliation accepted.");}
}
