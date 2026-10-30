using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static partial class NativePageRangeV2Verification
{
    private const string FrozenSha="B3BEC431A21638BBC20EBA897A1A1A04EF78B8E60D92B4D07CA677785D401F94";
    private static readonly SourceIdentity Source=new(6180,134367230890706269,@"C:\Program Files (x86)\NCSOFT\Lineage Classic\LC.exe","0x7FF79A400000",0x6ACEDB9A,0x3F5F000,ShopNativeHistoricalPackage.SupportedSha,"frozen-v83-page-ab-error-not-current-live");
    private const ulong Image=140701421535232,Target=87736701;
    private static byte[] Frozen()
    {
        var assembly=typeof(NativePageRangeV2Verification).Assembly;
        string name=assembly.GetManifestResourceNames().Single(n=>n.EndsWith("legacy-size-only.jsonl",StringComparison.Ordinal));
        using var stream=assembly.GetManifestResourceStream(name)!;using var memory=new MemoryStream();stream.CopyTo(memory);
        byte[] bytes=memory.ToArray();Need(Convert.ToHexString(SHA256.HashData(bytes))==FrozenSha,"Original twelve-row frozen fixture bytes changed");return bytes;
    }
    internal static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action action){try{action();checks.Add(new("page_range_v2_"+name,true,"Frozen receipt or explicitly synthetic mutation; no LC/helper/input."));}catch(Exception e){checks.Add(new("page_range_v2_"+name,false,e.ToString()));}}
        string original=Encoding.UTF8.GetString(Frozen());
        ShopNativeOutcome Parse(string text)=>ShopNativeOutcome.Parse(text,Source,Image,Target,1);
        Check("frozen_twelve_rows_known_not_entered",()=>{var p=Parse(original);Need(p.SafeToSettle&&p.NoEntryConfirmed&&p.SourceMatched&&!p.Entered&&!p.Returned&&p.Failure is {Code:"NativePreflightPageChanged",Scope:"Global",Phase:"Preflight"},p.Detail);});
        void Reject(string name,Action<List<JsonObject>> mutate,bool reseal=true)=>Check("reject_"+name,()=>{var rows=Rows(original);mutate(rows);var p=Parse(Text(rows,reseal));Need(!p.SafeToSettle&&!p.NoEntryConfirmed,"Unsafe mutated receipt settled: "+name);});
        foreach(string kind in new[]{"START","SOURCE","CURRENT_NATIVE_CODE_GUARDS","NONINVOKED_CODE_OBSERVATION","NETWORK_SHARED_COUNTS_SAMPLE","NETWORK_SHARED_COUNTS_AB","PREFLIGHT_PAGE_AB_ERROR","ERROR","END"})
        {string k=kind;Reject("missing_"+k,r=>r.Remove(r.First(x=>Kind(x)==k)));Reject("duplicate_"+k,r=>r.Insert(1,r.First(x=>Kind(x)==k).DeepClone().AsObject()));}
        Reject("source_creation",r=>Data(r,"SOURCE")["Creation"]=Source.CreationFileTime+1);
        Reject("source_pid",r=>Data(r,"SOURCE")["Pid"]=Source.ProcessId+1);
        Reject("source_target",r=>Data(r,"SOURCE")["TargetOwner"]=Target+1);
        Reject("source_path",r=>Data(r,"SOURCE")["Path"]=@"D:\unbound\LC.exe");
        Reject("cleanup_false",r=>Data(r,"END")["CleanupConfirmed"]=false);
        Reject("session_blocked",r=>Data(r,"END")["SessionBlocked"]=true);
        Reject("native_result",r=>Data(r,"END")["NativeResult"]=new JsonObject{["Entered"]=0,["Returned"]=0});
        Reject("entered_contradiction",r=>Data(r,"END")["NativeAlreadyReturned"]=true);
        Reject("event_result_contradiction",r=>Data(r,"END")["EventResultDurable"]=true);
        Reject("publication_intent",r=>r.Insert(r.Count-2,new JsonObject{["Kind"]="COMMAND_PUBLISH_INTENT",["Data"]=new JsonObject{["Sequence"]=1}}));
        Reject("third_stage_intent_even_unpublished_flag",r=>{Data(r,"ERROR")["Phase"]="BeforePublish";Data(r,"ERROR")["PreflightStage"]="BeforeCommandPublication";Data(r,"ERROR")["RequestNotPublished"]=true;r.Insert(r.Count-2,new JsonObject{["Kind"]="EVENT_INTENT_DURABLE",["Data"]=new JsonObject{["Calls"]=1}});});
        foreach(string field in new[]{"Base","AllocationBase","AllocationProtect","PartitionId","State","Protect","Type"})
        {string key=field;Reject("after_attribute_"+key,r=>{var p=Data(r,"PREFLIGHT_PAGE_AB_ERROR")["After"]!.AsObject();p[key]=p[key]!.GetValue<ulong>()+1;});}
        Reject("no_size_difference",r=>Data(r,"PREFLIGHT_PAGE_AB_ERROR")["After"]!["Size"]=4096);
        Reject("before_range_uncovered",r=>Data(r,"PREFLIGHT_PAGE_AB_ERROR")["Before"]!["Size"]=1);
        Reject("after_range_uncovered",r=>Data(r,"PREFLIGHT_PAGE_AB_ERROR")["After"]!["Size"]=1);
        Reject("address_hex_differs",r=>Data(r,"PREFLIGHT_PAGE_AB_ERROR")["AddressHex"]="0x7FF79A677CF1");
        Reject("length_differs",r=>Data(r,"PREFLIGHT_PAGE_AB_ERROR")["Length"]=550);
        Reject("new_producer_missing_marker",r=>Data(r,"START")["ManagedProgramVersion"]="17.0.1");
        Reject("legacy_claims_new_marker",r=>Data(r,"START")["PageRangeProofContract"]=2);
        Reject("sequence_out_of_order",r=>r[3]["Sequence"]=77,false);
        Reject("timestamp_out_of_order",r=>r[4]["At"]="2026-10-16T00:00:00Z",false);
        Reject("durability_count",r=>Data(r,"END")["LogDiskFlushesBeforeEnd"]=1,false);
        Reject("page_error_not_durable",r=>r.First(x=>Kind(x)=="PREFLIGHT_PAGE_AB_ERROR")["DiskFlush"]=false,false);
        ModernChecks(Check);
        checks.AddRange(PatrolAutoResumeSelfTest.RunPageRangeRecoveryAsync().GetAwaiter().GetResult());
        Current79D1Checks(Check);CurrentB47CChecks(Check);return checks.ToArray();
    }
    private static List<JsonObject> Rows(string text)=>text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonNode.Parse(s)!.AsObject()).ToList();
    private static string Kind(JsonObject row)=>row["Kind"]!.GetValue<string>();
    private static JsonObject Data(List<JsonObject> rows,string kind)=>rows.First(r=>Kind(r)==kind)["Data"]!.AsObject();
    private static string Text(List<JsonObject> rows,bool seal=true)
    {
        if(seal){for(int i=0;i<rows.Count;i++){rows[i]["Sequence"]=i+1;rows[i]["DiskFlush"]=Kind(rows[i]) is "ERROR" or "END" or "PREFLIGHT_PAGE_AB_ERROR";}
        if(rows.Any(r=>Kind(r)=="END")){var end=Data(rows,"END");end["LogRecordsBeforeEnd"]=rows.Count-1;end["LogDiskFlushesBeforeEnd"]=rows.Take(rows.Count-1).Count(r=>r["DiskFlush"]!.GetValue<bool>());end["LogBufferedRecordsBeforeEnd"]=rows.Take(rows.Count-1).Count(r=>!r["DiskFlush"]!.GetValue<bool>());}}
        return string.Join("\n",rows.Select(r=>r.ToJsonString()))+"\n";
    }
    private static void Need(bool ok,string detail){if(!ok)throw new InvalidOperationException(detail);}
    internal static int RunReport(string output)
    {
        bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);Need(admin,"ADMIN_REQUIRED");
        var checks=Run();var report=new{Success=checks.All(c=>c.Passed),IsAdministrator=admin,Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),Checks=checks,FrozenFixtureSha256=FrozenSha,LCRead=false,GameInputs=0,GameWrites=0};
        using var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(stream,report,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);return report.Success?0:1;
    }
}
