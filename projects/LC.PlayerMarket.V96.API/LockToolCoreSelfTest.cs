using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class LockToolCoreSelfTest
{
 internal static int Run(string fixture,string output)
 {
  var checks=new List<OfflineCheck>();void Check(string name,bool ok,string detail="isolated fixture only"){checks.Add(new(name,ok,detail));if(!ok)throw new Exception(name+": "+detail);}
  string testRoot=Path.Combine(Path.GetDirectoryName(output)!,"lock-tool-cases-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testRoot);
  string Copy(string name){string root=Path.Combine(testRoot,name);foreach(var f in Directory.GetFiles(fixture,"*",SearchOption.AllDirectories)){string to=Path.Combine(root,Path.GetRelativePath(fixture,f));Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(f,to);}foreach(string pending in Directory.GetFiles(Path.Combine(root,"sessions"),"*.pending.json")){var n=JsonNode.Parse(File.ReadAllText(pending))!;n["nativeLog"]=Path.Combine(root,"native",Path.GetFileName(n["nativeLog"]!.GetValue<string>()));File.WriteAllText(pending,n.ToJsonString());}return root;}
  string Digest(string root)=>string.Join("\n",Directory.GetFiles(root,"*",SearchOption.AllDirectories).Where(f=>!f.EndsWith(".lock")).Order().Select(f=>Path.GetRelativePath(root,f)+"="+ManualLockReleaseStore.Hash(f)));
  bool Throws(Action action){try{action();return false;}catch{return true;}}
  (ShopScanProgressStore s,ShopSearchedOwnerStore o) Read(string root)=>(ShopScanProgressStore.OpenReadOnly(Path.Combine(root,"scan-progress")),ShopSearchedOwnerStore.OpenReadOnly(Path.Combine(root,"searched-owners")));
  void Unknown(string root){
   // The captured fixture intentionally has only the newest plan's event files.
   // Scope synthetic historical-import cases to that complete plan, in the new copy only.
   string progress=Path.Combine(root,"scan-progress"),planId=Read(root).s.PlanId!;
   foreach(string file in Directory.GetFiles(progress,"*.plan.json"))if(!file.EndsWith("-"+planId+".plan.json",StringComparison.Ordinal))File.Delete(file);
   foreach(string file in Directory.GetFiles(Path.Combine(progress,"source-intents"),"*.json"))if(JsonNode.Parse(File.ReadAllText(file))!["planId"]!.GetValue<string>()!=planId)File.Delete(file);
   string log=Directory.GetFiles(Path.Combine(root,"native"),"*.jsonl").Single();var rows=File.ReadLines(log).Select(x=>JsonNode.Parse(x)!).ToArray();rows[^2]["Data"]!["Phase"]="Native";rows[^1]["Data"]!["NativeResult"]=new JsonObject{["Active"]=0,["Completed"]=1};File.WriteAllLines(log,rows.Select(x=>x.ToJsonString()));
  }
  try
  {
   string safe=Copy("safe");string before=Digest(safe);var scan=MarketLockToolCommands.Scan(safe,()=>false);var e=scan.Entries.Single();
   Check("scan_never_writes",before==Digest(safe));Check("actual_preflight_only_safe",e.CanRecoverSafely&&e.State=="ConfirmedNotDispatched");
   Check("busy_disables_actions",MarketLockToolCommands.Scan(safe,()=>true).Entries.All(x=>!x.CanForce&&!x.CanRecoverSafely));
   Check("stale_preview_rejected",Throws(()=>MarketLockToolCommands.Apply(new(safe,e.RequestId,"wrong","Safe",false),()=>false))&&before==Digest(safe));
   var result=MarketLockToolCommands.Apply(new(safe,e.RequestId,e.Fingerprint,"Safe",false),()=>false);
   Check("safe_exact_recovery",result.Success,result.Detail);Check("safe_pending_zero",MarketLockToolCommands.Scan(safe,()=>false).Entries.Length==0);
   Check("safe_backup_rollback",MarketLockToolCommands.Rollback(Path.Combine(result.BackupDirectory,"manifest.json"),()=>false).Success);
   Check("rollback_restores_known_pending",MarketLockToolCommands.Scan(safe,()=>false).Entries.Single().CanRecoverSafely);
   Check("rollback_idempotent",MarketLockToolCommands.Rollback(Path.Combine(result.BackupDirectory,"manifest.json"),()=>false).State=="AlreadyRolledBack");
   string force=Copy("unknown");Unknown(force);var fs=MarketLockToolCommands.Scan(force,()=>false);var f=fs.Entries.Single();var src=Read(force).s.Source!;
   Check("unknown_never_automatic",!f.CanRecoverSafely&&f.CanForce&&f.State=="OutcomeUnknown");string original=Digest(force);
   Check("force_requires_this_action_confirmation",Throws(()=>MarketLockToolCommands.Apply(new(force,f.RequestId,f.Fingerprint,"Force",false),()=>false))&&original==Digest(force));
   var originalLedger=Directory.GetFiles(Path.Combine(force,"scan-progress"),"*",SearchOption.AllDirectories).Concat(Directory.GetFiles(Path.Combine(force,"searched-owners"),"*",SearchOption.AllDirectories)).Concat(Directory.GetFiles(Path.Combine(force,"native"))).Where(x=>!x.EndsWith(".lock")).ToDictionary(x=>x,ManualLockReleaseStore.Hash);
   var applied=MarketLockToolCommands.Apply(new(force,f.RequestId,f.Fingerprint,"Force",true),()=>false);
   Check("manual_force_clears_app_lock",applied.Success,applied.Detail);var after=Read(force);
   Check("manual_force_replays_unblocked",after.s.Error==null&&after.o.Error==null&&!after.s.BlocksSource(src)&&!after.o.BlocksSource(src),after.s.Error+after.o.Error);
   Check("manual_force_does_not_forge_ledger_or_native",originalLedger.All(x=>ManualLockReleaseStore.Hash(x.Key)==x.Value));
   Check("manual_certificate_retains_unknown",ManualLockReleaseStore.Read(force).Single().Mode=="ManualRiskAccepted_OutcomeUnknown");
   Check("manual_force_not_reimported_as_uncertain",!ShopScanProgressStore.ReadHistoricalEvidence(Path.Combine(force,"scan-progress")).Any(x=>x.RequestId==f.RequestId));
   Check("force_pending_removed_without_resend",MarketLockToolCommands.Scan(force,()=>false).Entries.Length==0);
   File.WriteAllText(Path.Combine(force,"sessions","new-user-record.json"),"{}");
   Check("rollback_refuses_later_user_write",Throws(()=>MarketLockToolCommands.Rollback(Path.Combine(applied.BackupDirectory,"manifest.json"),()=>false)));
   foreach(string phase in new[]{"ManualCertificateSaved","ManualPendingRenamed"})
   {
    string root=Copy(phase);Unknown(root);var se=MarketLockToolCommands.Scan(root,()=>false).Entries.Single();
    var interrupted=MarketLockToolCommands.Apply(new(root,se.RequestId,se.Fingerprint,"Force",true),()=>false,at=>{if(at==phase)throw new IOException("simulated interruption");});
    Check(phase+"_reports_incomplete",!interrupted.Success&&interrupted.State=="IncompleteStopped");
    var st=Read(root);var recovered=ShopStartupRecovery.Run(root,st.s,st.o,()=>false);
    Check(phase+"_startup_finishes_authorized_once",Directory.GetFiles(Path.Combine(root,"sessions"),"*.pending.json").Length==0&&!Read(root).s.BlocksSource(src)&&!Read(root).o.BlocksSource(src),JsonSerializer.Serialize(recovered));
    Check(phase+"_second_startup_noop",ShopStartupRecovery.Run(root,st.s,st.o,()=>false).Length==0);
   }
   string corrupt=Copy("unsupported");Unknown(corrupt);string log=Directory.GetFiles(Path.Combine(corrupt,"native"),"*.jsonl").Single();File.WriteAllText(log,File.ReadAllText(log).Replace("\"NativeProtocolVersion\":13","\"NativeProtocolVersion\":99"));
   Check("unknown_protocol_not_forceable",MarketLockToolCommands.Scan(corrupt,()=>false).Entries.All(x=>!x.CanForce&&!x.CanRecoverSafely));
   string active=Copy("active");Unknown(active);log=Directory.GetFiles(Path.Combine(active,"native"),"*.jsonl").Single();File.WriteAllText(log,File.ReadAllText(log).Replace("\"Active\":0","\"Active\":1"));
   Check("active_native_not_forceable",MarketLockToolCommands.Scan(active,()=>false).Entries.All(x=>!x.CanForce&&!x.CanRecoverSafely));
   using(var lease=new FileStream(Path.Combine(active,"lock-tool-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
    Check("maintenance_ownership_prevents_concurrent_mutation",Throws(()=>MarketLockToolCommands.Apply(new(active,"x","x","Safe",false),()=>false)));
  }
  catch(Exception ex){checks.Add(new("suite_exception",false,ex.ToString()));}
  var report=new{At=DateTimeOffset.UtcNow,Passed=checks.Count>0&&checks.All(x=>x.Passed),Checks=checks.Count,Results=checks,Fixture=fixture,TestRoot=testRoot,GameReads=0,GameInputs=0,FormalDataModified=false};File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return report.Passed?0:1;
 }
}
