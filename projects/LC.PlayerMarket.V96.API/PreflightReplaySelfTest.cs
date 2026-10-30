using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class PreflightReplaySelfTest
{
    internal static int Run(string fixturePath,string output)
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Func<bool> action){try{checks.Add(new(name,action(),"offline replay"));}catch(Exception ex){checks.Add(new(name,false,ex.ToString()));}}
        string fixture=File.ReadAllText(fixturePath);
        JsonObject[] Read(string text)=>text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonNode.Parse(x)!.AsObject()).ToArray();
        string Write(JsonObject[] rows)=>string.Join('\n',rows.Select(x=>x.ToJsonString()));
        string Mutate(string text,Action<JsonObject[]> action){var rows=Read(text);action(rows);return Write(rows);}
        var original=Read(fixture);var id=original[1]["Data"]!;
        ulong image=id["Image"]!.GetValue<ulong>(),owner=id["TargetOwner"]!.GetValue<ulong>();
        var source=new SourceIdentity(id["Pid"]!.GetValue<int>(),id["Creation"]!.GetValue<long>(),id["Path"]!.GetValue<string>(),$"0x{image:X}",
            Convert.ToUInt32(id["Timestamp"]!.GetValue<string>()[2..],16),Convert.ToUInt32(id["ImageSize"]!.GetValue<string>()[2..],16),id["Sha256"]!.GetValue<string>(),"offline-recorded-source");
        ShopNativeOutcome Parse(string text,int exit=1)=>ShopNativeOutcome.Parse(text,source,image,owner,exit);
        Check("original_v42_replays_actual_132732_session_fault",()=>{
            var assembly=Assembly.LoadFrom(@"D:\lineage\LC_PlayerMarket_v42\release\LC.PlayerMarket.V42.dll");
            var type=assembly.GetType("MemoryHub.Service.ShopNativeOutcome",true)!;
            var value=type.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[fixture,source,image,owner,1])!;
            return !(bool)type.GetProperty("SafeToSettle")!.GetValue(value)!;
        });
        Check("exact_recorded_legacy_preflight_has_no_dispatch",()=>{var x=Parse(fixture);return x.SafeToSettle&&x.NoEntryConfirmed&&!x.Entered&&!x.Returned&&x.Failure is{Code:"NativePreflightReadUnavailable",Scope:"Global"};});
        Check("legacy_unknown_read_location_still_stops_batch",()=>{
            var x=Parse(fixture);return ShopScanPolicy.Decide(x.Failure!.Attach(new(){RequestId="offline",SessionBlocked=false}),true)==ShopScanDisposition.StopBatch;
        });
        string modern=Mutate(fixture,r=>{
            r[0]["Data"]!["ManagedVersion"]="13.0.1";r[0]["Data"]!["PreflightDiagnosticContract"]=1;
            r[^2]["Data"]!["Type"]="SourceReadException";r[^2]["Data"]!["Code"]="SourceReadIncomplete";
            r[^2]["Data"]!["Message"]="ReadProcessMemory incomplete: 299; category=NetworkConnectionTreeHead";
            r[^2]["Data"]!["ReadFailure"]=JsonSerializer.SerializeToNode(new{Address=0x123456780UL,RequestedBytes=8,ActualBytes=0,Win32Error=299,ApiSucceeded=false,Category="NetworkConnectionTreeHead",ReadCall=227});
            r[^1]["Data"]!["PreflightNoDispatchProof"]=JsonSerializer.SerializeToNode(new{Contract=1,Stage="InitialVerify",SourceEstablished=true,TransportConstructionAttempted=false,DurableIntentWritten=false,RequestPublicationPossible=false});
        });
        Check("explicit_new_no_transport_proof_settles_page_without_success_claim",()=>{var x=Parse(modern);return x.SafeToSettle&&x.NoEntryConfirmed&&!x.Entered&&!x.Returned&&x.Failure is{Code:"NativePreflightReadUnavailable",Scope:"Page",Phase:"Preflight"};});
        Check("known_context_read_failure_allows_next_different_page",()=>{
            var x=Parse(modern);return ShopScanPolicy.Decide(x.Failure!.Attach(new(){RequestId="offline",SessionBlocked=false}),true)==ShopScanDisposition.Continue;
        });
        Check("code_read_is_global_and_not_auto_retried",()=>{var x=Parse(Mutate(modern,r=>r[^2]["Data"]!["ReadFailure"]!["Category"]="NativeCode"));return x.SafeToSettle&&x.NoEntryConfirmed&&x.Failure!.Scope=="Global";});
        Check("AB_context_read_failure_retains_same_scope",()=>Parse(Mutate(modern,r=>r[^2]["Data"]!["ReadFailure"]!["Category"]="NetworkConnectionTreeHead/AB")).Failure?.Scope=="Page");
        void Reject(string name,string basis,Action<JsonObject[]> mutation)=>Check(name,()=>!Parse(Mutate(basis,mutation)).NoEntryConfirmed);
        foreach(var basis in new[]{("legacy",fixture),("modern",modern)})
        {
            Reject(basis.Item1+"_wrong_source",basis.Item2,r=>r[1]["Data"]!["Creation"]=1);
            Reject(basis.Item1+"_wrong_target",basis.Item2,r=>r[1]["Data"]!["TargetOwner"]=1);
            Reject(basis.Item1+"_wrong_path",basis.Item2,r=>r[1]["Data"]!["Path"]="other.exe");
            Reject(basis.Item1+"_wrong_PE",basis.Item2,r=>r[1]["Data"]!["ImageSize"]="0x1000");
            Reject(basis.Item1+"_missing_source",basis.Item2,r=>r[1]["Kind"]="OTHER");
            Reject(basis.Item1+"_duplicate_sequence",basis.Item2,r=>r[3]["Sequence"]=3);
            Reject(basis.Item1+"_hook_install_seen",basis.Item2,r=>r[3]["Kind"]="HOOK_INSTALL_INTENT");
            Reject(basis.Item1+"_command_publish_seen",basis.Item2,r=>r[3]["Kind"]="COMMAND_PUBLISH_INTENT");
            Reject(basis.Item1+"_later_phase",basis.Item2,r=>r[^2]["Data"]!["Phase"]="BeforePublish");
            Reject(basis.Item1+"_end_not_durable",basis.Item2,r=>r[^1]["DiskFlush"]=false);
            Reject(basis.Item1+"_incomplete_log",basis.Item2,r=>r[^1]["Data"]!["LogRecordsBeforeEnd"]=6);
            Reject(basis.Item1+"_cleanup_incomplete",basis.Item2,r=>r[^1]["Data"]!["CleanupConfirmed"]=false);
            Reject(basis.Item1+"_helper_still_blocked",basis.Item2,r=>r[^1]["Data"]!["SessionBlocked"]=true);
            Check(basis.Item1+"_truncation_remains_uncertain",()=>!Parse(string.Join('\n',basis.Item2.Split('\n').Where(x=>!x.Contains("\"Kind\":\"END\"")))).SafeToSettle);
            Check(basis.Item1+"_exit_mismatch_remains_uncertain",()=>!Parse(basis.Item2,0).SafeToSettle);
        }
        foreach(string flag in new[]{"TransportConstructionAttempted","DurableIntentWritten","RequestPublicationPossible"})
            Reject("positive_"+flag+"_forbids_unpublished",modern,r=>r[^1]["Data"]!["PreflightNoDispatchProof"]![flag]=true);
        Reject("unestablished_source",modern,r=>r[^1]["Data"]!["PreflightNoDispatchProof"]!["SourceEstablished"]=false);
        Reject("unknown_proof_contract",modern,r=>r[^1]["Data"]!["PreflightNoDispatchProof"]!["Contract"]=2);
        Reject("missing_proof",modern,r=>r[^1]["Data"]!.AsObject().Remove("PreflightNoDispatchProof"));
        Reject("full_success_not_read_failure",modern,r=>{r[^2]["Data"]!["ReadFailure"]!["ActualBytes"]=8;r[^2]["Data"]!["ReadFailure"]!["ApiSucceeded"]=true;});
        Reject("unknown_legacy_error_not_recognized",fixture,r=>r[^2]["Data"]!["Message"]="other error");
        Reject("legacy_network_phase_B_cannot_use_A_proof",fixture,r=>r[5]["Data"]!["Phase"]="B");
        Check("same_page_failure_cannot_continue_changed_source",()=>{var x=Parse(modern);return ShopScanPolicy.Decide(x.Failure!.Attach(new(){RequestId="offline"}),false)==ShopScanDisposition.StopBatch;});
        Check("native_entry_evidence_still_stops_page",()=>{var x=Parse(modern);return ShopScanPolicy.Decide(x.Failure!.Attach(new(){RequestId="offline",NativeEntered=true}),true)==ShopScanDisposition.StopBatch;});
        checks.AddRange(NativeProtocolV13SelfTest.Run());
        var report=new{At=DateTimeOffset.UtcNow,Passed=checks.All(x=>x.Passed),Checks=checks.Count,Failures=checks.Where(x=>!x.Passed).ToArray(),Results=checks,
            Fixture=fixturePath,RealLegacyReplay=true,SyntheticNewProducerCases=true,GameReads=0,GameInputs=0,CreatedWindow=false,ActiveMarketChanged=false};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        return report.Passed?0:1;
    }
}
