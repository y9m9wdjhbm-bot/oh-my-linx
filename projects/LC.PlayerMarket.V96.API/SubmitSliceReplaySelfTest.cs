using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class SubmitSliceReplaySelfTest
{
 internal static int Run(string fixturePath,string output)
 {
  var checks=new List<OfflineCheck>();
  void Check(string name,Func<bool> test){try{checks.Add(new(name,test(),"recorded 2026-10-02 01:43:57 helper replay"));}catch(Exception ex){checks.Add(new(name,false,ex.ToString()));}}
  string fixture=File.ReadAllText(fixturePath);
  JsonObject[] Read(string s)=>s.Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonNode.Parse(x)!.AsObject()).ToArray();
  string Write(JsonObject[] rows)=>string.Join('\n',rows.Select(x=>x.ToJsonString()));
  string Mutate(Action<JsonObject[]> action){var r=Read(fixture);action(r);return Write(r);}
  var original=Read(fixture);var id=original[1]["Data"]!;
  ulong image=id["Image"]!.GetValue<ulong>(),owner=id["TargetOwner"]!.GetValue<ulong>();
  var source=new SourceIdentity(id["Pid"]!.GetValue<int>(),id["Creation"]!.GetValue<long>(),id["Path"]!.GetValue<string>(),$"0x{image:X}",Convert.ToUInt32(id["Timestamp"]!.GetValue<string>()[2..],16),Convert.ToUInt32(id["ImageSize"]!.GetValue<string>()[2..],16),id["Sha256"]!.GetValue<string>(),"offline-exact-014357");
  ShopNativeOutcome Parse(string text)=>ShopNativeOutcome.Parse(text,source,image,owner,1);
  Check("formal_v44_reproduces_safe_cleanup_but_missing_no_entry_proof",()=>{
   var a=Assembly.LoadFrom(@"D:\lineage\LC_PlayerMarket_v44\release\LC.PlayerMarket.V44.dll");var t=a.GetType("MemoryHub.Service.ShopNativeOutcome",true)!;
   var x=t.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[fixture,source,image,owner,1])!;
   return (bool)t.GetProperty("SafeToSettle")!.GetValue(x)!&&!(bool)t.GetProperty("NoEntryConfirmed",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(x)!;
  });
  Check("recorded_submit_tail_preflight_is_confirmed_not_dispatched",()=>{var x=Parse(fixture);return x.SafeToSettle&&x.NoEntryConfirmed&&!x.Entered&&!x.Returned&&x.Failure is{Code:"NativeCodeUnavailable",Scope:"Global",Phase:"Preflight",Rva:"0x27E000"};});
  Check("unavailable_code_still_stops_batch_without_automatic_dispatch",()=>{var x=Parse(fixture);return ShopScanPolicy.Decide(x.Failure!.Attach(new(){RequestId="offline",SessionBlocked=false}),true)==ShopScanDisposition.StopBatch;});
  string first=Mutate(r=>{
   r[2]["Data"]!["Rva"]="0x27D620";r[2]["Data"]!["Length"]=2528;r[3]["Data"]!["Rva"]="0x27D620";r[4]["Data"]!["Rva"]="0x27D620";
   foreach(var p in new[]{r[2]["Data"]!["Before"]!,r[2]["Data"]!["After"]!,r[3]["Data"]!["Page"]!}){p["Base"]=image+0x27D000;p["Size"]=4096;}
  });
  Check("registered_first_slice_remains_confirmed_not_dispatched",()=>Parse(first).NoEntryConfirmed);
  void Reject(string name,Action<JsonObject[]> mutation)=>Check(name,()=>!Parse(Mutate(mutation)).NoEntryConfirmed);
  Reject("arbitrary_subslice_rejected",r=>{r[2]["Data"]!["Rva"]="0x27E001";r[2]["Data"]!["Length"]=817;});
  Reject("wrong_slice_length_rejected",r=>r[2]["Data"]!["Length"]=819);
  Reject("different_function_rejected",r=>r[2]["Data"]!["FunctionRva"]="0x27E000");
  Reject("unavailable_event_rva_mismatch",r=>r[3]["Data"]!["Rva"]="0x27D620");
  Reject("error_rva_mismatch",r=>r[4]["Data"]!["Rva"]="0x27D620");
  Reject("wrong_target_owner",r=>r[1]["Data"]!["TargetOwner"]=owner+1);
  Reject("changed_pid",r=>r[1]["Data"]!["Pid"]=source.ProcessId+1);
  Reject("missing_modern_proof",r=>r[^1]["Data"]!.AsObject().Remove("PreflightNoDispatchProof"));
  foreach(string flag in new[]{"TransportConstructionAttempted","DurableIntentWritten","RequestPublicationPossible"})Reject("positive_"+flag+"_remains_unknown",r=>r[^1]["Data"]!["PreflightNoDispatchProof"]![flag]=true);
  Reject("wrong_proof_stage",r=>r[^1]["Data"]!["PreflightNoDispatchProof"]!["Stage"]="BeforePublish");
  Reject("unknown_proof_contract",r=>r[^1]["Data"]!["PreflightNoDispatchProof"]!["Contract"]=2);
  Reject("source_not_established",r=>r[^1]["Data"]!["PreflightNoDispatchProof"]!["SourceEstablished"]=false);
  Reject("tail_requires_modern_producer",r=>r[0]["Data"]!["ManagedVersion"]="13.0.0");
  Reject("cleanup_not_confirmed",r=>r[^1]["Data"]!["CleanupConfirmed"]=false);
  Reject("native_session_still_blocked",r=>r[^1]["Data"]!["SessionBlocked"]=true);
  Reject("proof_sequence_gap",r=>r[2]["Sequence"]=4);
  Reject("wrong_flush_accounting",r=>r[^1]["Data"]!["LogStreamFlushesBeforeEnd"]=3);
  Reject("changed_page_owner",r=>r[2]["Data"]!["Before"]!["AllocationBase"]=image+4096);
  Reject("private_page_not_lc_image",r=>r[2]["Data"]!["Before"]!["Type"]=0x20000);
  Reject("guard_page_not_noaccess",r=>r[2]["Data"]!["Before"]!["Protect"]=0x101);
  Reject("slice_outside_region",r=>r[2]["Data"]!["Before"]!["Size"]=817);
  Reject("before_after_final_page_disagree",r=>r[2]["Data"]!["After"]!["Size"]=4096);
  Reject("invalid_elapsed_not_evidence",r=>r[2]["Data"]!["ElapsedMilliseconds"]=-1);
  Check("additional_record_cannot_be_accepted",()=>!Parse(fixture+"\n"+original[4].ToJsonString()).NoEntryConfirmed);
  var report=new{At=DateTimeOffset.UtcNow,Passed=checks.Count(x=>x.Passed),Failed=checks.Count(x=>!x.Passed),Success=checks.All(x=>x.Passed),Checks=checks,Fixture=fixturePath,GameInputs=0,LiveTest=false};
  Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,JsonSerializer.Serialize(report,HubProtocol.Json));return report.Success?0:1;
 }
}
