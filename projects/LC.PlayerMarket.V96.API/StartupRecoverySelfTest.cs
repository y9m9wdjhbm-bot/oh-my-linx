using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class StartupRecoverySelfTest
{
    internal static int Run(string fixture,string output)
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,bool ok,string detail="offline fixture"){checks.Add(new(name,ok,detail));if(!ok)throw new Exception(name+": "+detail);}
        string testRoot=Path.Combine(Path.GetDirectoryName(output)!,"recovery-cases-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testRoot);
        string Copy(string name)
        {
            string dest=Path.Combine(testRoot,name);Directory.CreateDirectory(dest);
            foreach(string f in Directory.GetFiles(fixture,"*",SearchOption.AllDirectories))
            {string to=Path.Combine(dest,Path.GetRelativePath(fixture,f));Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(f,to);}
            foreach(string pending in Directory.GetFiles(Path.Combine(dest,"sessions"),"*.pending.json"))
            {var node=JsonNode.Parse(File.ReadAllText(pending))!;node["nativeLog"]=Path.Combine(dest,"native",Path.GetFileName(node["nativeLog"]!.GetValue<string>()));File.WriteAllText(pending,node.ToJsonString());}
            return dest;
        }
        (ShopScanProgressStore,ShopSearchedOwnerStore,SourceIdentity) Stores(string root)
        {
            var scans=new ShopScanProgressStore(Path.Combine(root,"scan-progress"));
            var owners=new ShopSearchedOwnerStore(Path.Combine(root,"searched-owners"),Path.Combine(root,"scan-progress"));
            if(scans.Error is not null||owners.Error is not null)throw new Exception(scans.Error+owners.Error);
            return(scans,owners,scans.Source!);
        }
        string Hash(string f)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        try
        {
            string good=Copy("normal");var (scans,owners,source)=Stores(good);
            Check("actual_fixture_replays_both_persistent_locks",scans.BlocksSource(source)&&owners.BlocksSource(source));
            var beforeHistory=owners.Summary(false,"");
            var originalFiles=Directory.GetFiles(good,"*",SearchOption.AllDirectories).ToDictionary(x=>x,Hash);
            var result=ShopStartupRecovery.Run(good,scans,owners,()=>false);
            Check("actual_pending_recovered_stopped",result is[{State:"RecoveredStopped"}],JsonSerializer.Serialize(result));
            Check("all_three_proven_latches_settled",!scans.BlocksSource(source)&&!owners.BlocksSource(source)&&Directory.GetFiles(Path.Combine(good,"sessions"),"*.pending.json").Length==0);
            Check("original_native_log_unchanged",originalFiles.Where(p=>p.Key.Contains("\\native\\")).All(p=>Hash(p.Key)==p.Value));
            Check("original_failed_result_preserved",Directory.GetFiles(Path.Combine(good,"recovery"),"original-terminal-result.json",SearchOption.AllDirectories).All(f=>Hash(f)==originalFiles[Path.Combine(good,"scan-progress","9e3ce0e247d349ff9d5af82d60d9fc0a.events","00000023.json")]));
            var reloaded=Stores(good);
            Check("restart_remains_unblocked",!reloaded.Item1.BlocksSource(source)&&!reloaded.Item2.BlocksSource(source));
            Check("second_startup_does_no_extra_recovery",ShopStartupRecovery.Run(good,reloaded.Item1,reloaded.Item2,()=>false).Length==0);
            var afterHistory=reloaded.Item2.Summary(false,"");
            Check("historical_owner_count_preserved",beforeHistory.OwnerCount==afterHistory.OwnerCount);
            foreach(bool correctDirectory in new[]{true,false})
            {
                string root=Copy(correctDirectory?"permanent-native-log":"foreign-native-log");
                var stores=Stores(root);
                string designated=Path.Combine(testRoot,"diagnostics-"+Guid.NewGuid().ToString("N"),"native");
                string actualDirectory=correctDirectory?designated:Path.Combine(testRoot,"foreign-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(actualDirectory);
                foreach(string pending in Directory.GetFiles(Path.Combine(root,"sessions"),"*.pending.json"))
                {
                    var node=JsonNode.Parse(File.ReadAllText(pending))!;
                    string original=node["nativeLog"]!.GetValue<string>();
                    string relocated=Path.Combine(actualDirectory,Path.GetFileName(original));
                    File.Copy(original,relocated);
                    node["nativeLog"]=relocated;File.WriteAllText(pending,node.ToJsonString());
                }
                var before=Directory.GetFiles(actualDirectory,"*.jsonl").ToDictionary(path=>path,Hash);
                var recovered=ShopStartupRecovery.Run(root,stores.Item1,stores.Item2,()=>false,nativeLogDirectory:designated);
                Check(correctDirectory?"exact_current_diagnostic_directory_is_accepted":"other_diagnostic_directory_never_settles_pending",
                    correctDirectory?recovered is[{State:"RecoveredStopped"}]&&!stores.Item1.BlocksSource(source)&&!stores.Item2.BlocksSource(source)
                        :recovered is[{State:"Deferred"}]&&stores.Item1.BlocksSource(source)&&stores.Item2.BlocksSource(source)
                            &&Directory.GetFiles(Path.Combine(root,"sessions"),"*.pending.json").Length==1,
                    JsonSerializer.Serialize(recovered));
                Check((correctDirectory?"exact":"other")+"_permanent_native_log_is_unchanged",before.All(pair=>Hash(pair.Key)==pair.Value));
            }
            // Replay the repaired standard ledger with the actually published V42
            // types, proving rollback readability without launching its GUI.
            var asm=Assembly.LoadFrom(@"D:\lineage\LC_PlayerMarket_v42\release\LC.PlayerMarket.V42.dll");
            foreach(var item in new[]{("ShopScanProgressStore",new object?[]{Path.Combine(good,"scan-progress")}),
                ("ShopSearchedOwnerStore",new object?[]{Path.Combine(good,"searched-owners"),Path.Combine(good,"scan-progress"),null,CancellationToken.None})})
            {
                var type=asm.GetType("MemoryHub.Service."+item.Item1,true)!;
                object old=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,item.Item2,null)!;
                Check("published_v42_replays_"+item.Item1,type.GetProperty("Error",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(old)is null
                    &&!(bool)type.GetMethod("BlocksSource",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(old,[source])!);
            }
            foreach(string stage in new[]{"TerminalSettled","ResultRestored","OwnerSettled"})
            {
                string root=Copy("interruption-"+stage);var store=Stores(root);
                var first=ShopStartupRecovery.Run(root,store.Item1,store.Item2,()=>false,at=>{if(at==stage)throw new IOException("simulated interruption");});
                Check(stage+"_interruption_keeps_pending",first.Any(x=>x.State=="Deferred")&&Directory.GetFiles(Path.Combine(root,"sessions"),"*.pending.json").Length==1);
                var fresh=Stores(root);var retried=ShopStartupRecovery.Run(root,fresh.Item1,fresh.Item2,()=>false);
                Check(stage+"_restart_finishes_once",retried is[{State:"RecoveredStopped"}]&&!fresh.Item1.BlocksSource(source)&&!fresh.Item2.BlocksSource(source),JsonSerializer.Serialize(retried));
            }
            string busy=Copy("busy");var bs=Stores(busy);
            Check("active_other_market_defers_without_clear",ShopStartupRecovery.Run(busy,bs.Item1,bs.Item2,()=>true) is[{State:"Deferred"}]&&bs.Item1.BlocksSource(source));
            string unknown=Copy("unknown");var us=Stores(unknown);
            string log=Directory.GetFiles(Path.Combine(unknown,"native"),"*.jsonl").Single();
            File.WriteAllText(log,File.ReadAllText(log).Replace("\"Phase\":\"Preflight\"","\"Phase\":\"Native\""));
            Check("possible_dispatch_never_cleared",ShopStartupRecovery.Run(unknown,us.Item1,us.Item2,()=>false) is[{State:"Deferred"}]
                &&us.Item1.BlocksSource(source)&&us.Item2.BlocksSource(source)&&Directory.GetFiles(Path.Combine(unknown,"sessions"),"*.pending.json").Length==1);
        }
        catch(Exception ex){checks.Add(new("recovery_suite_exception",false,ex.ToString()));}
        var report=new{At=DateTimeOffset.UtcNow,Passed=checks.All(x=>x.Passed),Checks=checks.Count,Results=checks,Fixture=fixture,TestRoot=testRoot,
            GameReads=0,GameInputs=0,CreatedWindow=false,FormalDataModified=false};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        return report.Passed?0:1;
    }
}
