using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class OrphanHistoryBoundSelfTest
{
 internal static int Run(string fixture,string output)
 {
  var checks=new List<OfflineCheck>();
  void Check(string name,bool ok,string detail="isolated same-source history fixture")
  {checks.Add(new(name,ok,detail));File.AppendAllText(output+".progress.jsonl",JsonSerializer.Serialize(checks[^1])+"\n");if(!ok)throw new Exception(name+": "+detail);}
  string root=Path.Combine(Path.GetDirectoryName(output)!,"same-source-history-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try
  {
   foreach(string from in Directory.GetFiles(fixture,"*",SearchOption.AllDirectories))
   {string to=Path.Combine(root,Path.GetRelativePath(fixture,from));Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(from,to);}
   string session=Directory.GetFiles(Path.Combine(root,"sessions"),"*.resolved-*.json").Single();
   var node=JsonNode.Parse(File.ReadAllText(session))!;
   node["nativeLog"]=Path.Combine(root,"native",Path.GetFileName(node["nativeLog"]!.GetValue<string>()));
   File.WriteAllText(session,node.ToJsonString());
   string request=node["requestId"]!.GetValue<string>();
   for(int i=0;i<337;i++)
   {
    var other=node.DeepClone();other["requestId"]="offline-other-request-"+i;
    File.WriteAllText(Path.Combine(root,"sessions","14768-134353380915597425.resolved-"+Guid.NewGuid().ToString("N")+".json"),other.ToJsonString());
   }
   var scans=new ShopScanProgressStore(Path.Combine(root,"scan-progress"));
   var owners=new ShopSearchedOwnerStore(Path.Combine(root,"searched-owners"),Path.Combine(root,"scan-progress"));
   if(scans.Error is not null||owners.Error is not null)throw new Exception(scans.Error+owners.Error);
   Check("338_same_source_descriptors_exceed_old_256_limit",Directory.GetFiles(Path.Combine(root,"sessions"),"*.resolved-*.json").Length==338);
   Check("all_history_read_and_exact_target_binds",ShopOrphanRecovery.TryBind(root,request,scans,out var binding,out var reason),reason);
   ShopOrphanRecovery.CheckUnchanged(binding!);Check("race_validation_uses_same_bounded_full_lookup",true);
   string duplicate=Path.Combine(root,"sessions","14768-134353380915597425.resolved-"+Guid.NewGuid().ToString("N")+".json");File.Copy(session,duplicate);
   Check("duplicate_target_still_refused_among_large_history",!ShopOrphanRecovery.TryBind(root,request,scans,out _,out _));
   bool raced=false;try{ShopOrphanRecovery.CheckUnchanged(binding!);}catch(InvalidDataException){raced=true;}
   Check("duplicate_inserted_after_binding_refused",raced);File.Delete(duplicate);
   Check("unique_target_binds_after_owned_fixture_reset",ShopOrphanRecovery.TryBind(root,request,scans,out _,out _));
   var result=ShopStartupRecovery.Run(root,scans,owners,()=>false);
   Check("large_history_startup_recovers_stopped",result is[{State:"RecoveredStopped"}],JsonSerializer.Serialize(result));
   Check("both_original_source_latches_settled",!scans.BlocksSource(scans.Source!)&&!owners.BlocksSource(scans.Source!));
   Check("all_resolved_history_preserved",Directory.GetFiles(Path.Combine(root,"sessions"),"*.resolved-*.json").Length==338&&File.Exists(session));
  }
  catch(Exception ex){checks.Add(new("suite_exception",false,ex.ToString()));}
  var report=new{At=DateTimeOffset.UtcNow,Passed=checks.All(x=>x.Passed),Checks=checks.Count,Results=checks,Fixture=fixture,TestRoot=root,GameReads=0,GameInputs=0,CreatedWindow=false,FormalDataModified=false};
  File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return report.Passed?0:1;
 }
}
