using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class OrphanRecoverySelfTest
{
 internal static int Run(string fixture,string output)
 {
  var checks=new List<OfflineCheck>();
  void Check(string name,bool ok,string detail="offline durable-state copy"){checks.Add(new(name,ok,detail));File.AppendAllText(output+".progress.jsonl",JsonSerializer.Serialize(checks[^1])+"\n");if(!ok)throw new Exception(name+": "+detail);}
  bool smoke=Environment.GetEnvironmentVariable("LC_ORPHAN_REPLAY_MODE")=="smoke";
  string testRoot=Path.Combine(Path.GetDirectoryName(output)!,"cases-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testRoot);
  const string request="scan-8A93EC6082A6EA50AF922B9621A5A29C-79";
  string Copy(string name)
  {
   string dest=Path.Combine(testRoot,name);Directory.CreateDirectory(dest);
   foreach(string f in Directory.GetFiles(fixture,"*",SearchOption.AllDirectories))
   {string to=Path.Combine(dest,Path.GetRelativePath(fixture,f));Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(f,to);}
   foreach(string session in Directory.GetFiles(Path.Combine(dest,"sessions"),"*.resolved-*.json"))
   {var node=JsonNode.Parse(File.ReadAllText(session))!;node["nativeLog"]=Path.Combine(dest,"native",Path.GetFileName(node["nativeLog"]!.GetValue<string>()));File.WriteAllText(session,node.ToJsonString());}
   return dest;
  }
  (ShopScanProgressStore scans,ShopSearchedOwnerStore owners,SourceIdentity source) Stores(string root)
  {var s=new ShopScanProgressStore(Path.Combine(root,"scan-progress"));var o=new ShopSearchedOwnerStore(Path.Combine(root,"searched-owners"),Path.Combine(root,"scan-progress"));if(s.Error is not null||o.Error is not null)throw new Exception(s.Error+o.Error);return(s,o,s.Source!);}
  void Edit(string file,Action<JsonNode> change){var n=JsonNode.Parse(File.ReadAllText(file))!;change(n);File.WriteAllText(file,n.ToJsonString());}
  string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
  try
  {
   string root=Copy("normal");var s=Stores(root);
   Check("actual_orphan_replays_both_source_latches",s.scans.BlocksSource(s.source)&&s.owners.BlocksSource(s.source));
   Check("actual_79_full_chain_binds",ShopOrphanRecovery.TryBind(root,request,s.scans,out var b,out var why),why);
   string beforeSession=Hash(b!.SessionFile),beforeNative=Hash(b.EvidenceFiles.Single(x=>x.EndsWith(".jsonl"))),beforeResponse=Hash(b.EvidenceFiles.Single(x=>x.EndsWith(".response.json")));
   var beforeOwners=s.owners.Summary(false,"");
   var scan=MarketLockToolCommands.Scan(root,()=>false);
   Check("tool_orphan_safe_only_no_force",scan.Entries.Single(x=>x.RequestId==request) is{CanRecoverSafely:true,CanForce:false,SourcePid:14768});
   string requestFile=b.EvidenceFiles.Single(x=>x.EndsWith(".request.json"));byte[] originalRequest=File.ReadAllBytes(requestFile);
   Edit(requestFile,n=>n["at"]="2026-10-01T17:43:55.0805626+00:00");
   Check("tool_fingerprint_includes_original_request_bytes",MarketLockToolCommands.Scan(root,()=>false).Entries.Single(x=>x.RequestId==request).Fingerprint!=scan.Entries.Single(x=>x.RequestId==request).Fingerprint);
   File.WriteAllBytes(requestFile,originalRequest);
   var result=ShopStartupRecovery.Run(root,s.scans,s.owners,()=>false);
   Check("actual_orphan_recovered_stopped",result is[{State:"RecoveredStopped"}],JsonSerializer.Serialize(result));
   Check("all_latches_settled",!s.scans.BlocksSource(s.source)&&!s.owners.BlocksSource(s.source));
   Check("original_resolved_native_response_preserved",Hash(b.SessionFile)==beforeSession&&Hash(b.EvidenceFiles.Single(x=>x.EndsWith(".jsonl")))==beforeNative&&Hash(b.EvidenceFiles.Single(x=>x.EndsWith(".response.json")))==beforeResponse);
   Check("all_six_original_chain_inputs_backed_up",b.EvidenceFiles.All(x=>File.Exists(Path.Combine(result[0].BackupDirectory!,"orphan-"+Path.GetFileName(x)))&&Hash(x)==Hash(Path.Combine(result[0].BackupDirectory!,"orphan-"+Path.GetFileName(x)))));
   Check("historical_owners_preserved",s.owners.Summary(false,"").OwnerCount==beforeOwners.OwnerCount);
   var restart=Stores(root);
   Check("restart_unblocked_and_idempotent",!restart.scans.BlocksSource(s.source)&&!restart.owners.BlocksSource(s.source)&&ShopStartupRecovery.Run(root,restart.scans,restart.owners,()=>false).Length==0);
   if(!smoke)foreach(string stage in new[]{"TerminalSettled","ResultRestored","OwnerSettled"})
   {
    string r=Copy("interrupt-"+stage);var x=Stores(r);
    var first=ShopStartupRecovery.Run(r,x.scans,x.owners,()=>false,at=>{if(at==stage)throw new IOException("isolated interruption");});
    Check(stage+"_interruption_stops",first.Any(y=>y.State=="Deferred"));
    var next=Stores(r);var resumed=ShopStartupRecovery.Run(r,next.scans,next.owners,()=>false);
    Check(stage+"_restart_finishes_once",resumed is[{State:"RecoveredStopped"}]&&!next.scans.BlocksSource(next.source)&&!next.owners.BlocksSource(next.source),JsonSerializer.Serialize(resumed));
   }
   if(!smoke)foreach(string name in new[]{"source","request-signature","owner","response","native-proof","ambiguous","busy","race"})
   {
    string r=Copy("reject-"+name);var x=Stores(r);
    Check(name+"_baseline_binds",ShopOrphanRecovery.TryBind(r,request,x.scans,out var binding,out _));
    if(name=="source")Edit(binding!.SessionFile,n=>n["source"]!["processId"]=1);
    if(name=="request-signature")Edit(binding!.EvidenceFiles.Single(z=>z.EndsWith(".request.json")),n=>n["signature"]="bad");
    if(name=="owner")Edit(binding!.SessionFile,n=>n["owner"]!["localIdentity"]="wrong");
    if(name=="response")Edit(binding!.EvidenceFiles.Single(z=>z.EndsWith(".response.json")),n=>n["nativeEntered"]=true);
    if(name=="native-proof")
    {string log=binding!.EvidenceFiles.Single(z=>z.EndsWith(".jsonl"));File.WriteAllText(log,File.ReadAllText(log).Replace("\"RequestPublicationPossible\":false","\"RequestPublicationPossible\":true"));}
    if(name=="ambiguous")File.Copy(binding!.SessionFile,Path.Combine(r,"sessions","14768-134353380915597425.resolved-"+Guid.NewGuid().ToString("N")+".json"));
    bool busy=name=="busy";
    if(name=="race")
    {bool refused=false;Edit(binding!.EvidenceFiles.Single(z=>z.EndsWith(".request.json")),n=>n["at"]="2026-10-01T00:00:00Z");try{ShopOrphanRecovery.CheckUnchanged(binding);}catch(InvalidDataException){refused=true;}Check("race_check_refuses_changed_evidence",refused);continue;}
    var rejected=ShopStartupRecovery.Run(r,x.scans,x.owners,()=>busy);
    Check(name+"_never_settles",!rejected.Any(z=>z.State=="RecoveredStopped")&&x.scans.BlocksSource(x.source)&&x.owners.BlocksSource(x.source));
   }
  }
  catch(Exception ex){checks.Add(new("suite_exception",false,ex.ToString()));}
  var report=new{At=DateTimeOffset.UtcNow,Passed=checks.All(x=>x.Passed),Checks=checks.Count,Results=checks,Fixture=fixture,TestRoot=testRoot,GameReads=0,GameInputs=0,CreatedWindow=false,FormalDataModified=false};
  File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return report.Passed?0:1;
 }
}
