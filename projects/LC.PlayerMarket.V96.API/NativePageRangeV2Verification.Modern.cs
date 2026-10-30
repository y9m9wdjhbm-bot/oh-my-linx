using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static partial class NativePageRangeV2Verification
{
    private static readonly Type Fixture=typeof(NativeCertifiedColdV17Verification);
    private static object Field(string name)=>Fixture.GetField(name,BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    private static List<JsonObject> BaseFixture(string name,params object[] args)=>(List<JsonObject>)Fixture.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args)!;
    private static string SyntheticText(List<JsonObject> rows)=>(string)Fixture.GetMethod("Text",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[rows])!;
    private static JsonObject Range(ShopNativeV17Contract.RangeSpec spec,ulong image,ulong mask,string stage,bool closed=true)
    {
        ulong at=image+spec.Rva;bool cold=spec.DeferredBit!=0 && (mask&spec.DeferredBit)!=0;
        var page=new JsonObject{["Base"]=at&~4095UL,["AllocationBase"]=image,["AllocationProtect"]=128,["PartitionId"]=0,["Size"]=4096,["State"]=4096,["Protect"]=cold?1:32,["Type"]=16777216};
        return new(){["Category"]="NativeCodePage",["PageRangeProofContract"]=2,["PreflightStage"]=stage,["RequestNotPublished"]=true,["FinalSourceClosureCompleted"]=closed,
            ["Address"]=at,["AddressHex"]=$"0x{at:X}",["Rva"]=$"0x{spec.Rva:X}",["Length"]=spec.Length,["Before"]=page,["After"]=page.DeepClone(),
            ["RequestedRangeCoveredA"]=true,["RequestedRangeCoveredB"]=true,["SameBase"]=true,["StableAttributes"]=true,["RegionSizeChanged"]=false,["PageRangeEquivalent"]=true,
            ["ByteProofRecorded"]=!cold,["ReadAComplete"]=!cold,["ReadBComplete"]=!cold,["BytesAbEqual"]=cold?null:JsonValue.Create(true),
            ["BeforeSha256"]=cold?null:JsonValue.Create(spec.Sha256),["AfterSha256"]=cold?null:JsonValue.Create(spec.Sha256)};
    }
    private static List<JsonObject> Modern(List<JsonObject> rows,ulong image)
    {
        Data(rows,"START")["ManagedProgramVersion"]="17.0.1";Data(rows,"START")["PageRangeProofContract"]=2;
        Data(rows,"END")["ManagedProgramVersion"]="17.0.1";Data(rows,"END")["PageRangeProofContract"]=2;
        int index=0;
        foreach(var row in rows.Where(r=>Kind(r)=="PREFLIGHT_AB_CLOSED"))
        {
            var d=row["Data"]!.AsObject();string stage=new[]{"BeforeTransport","InstalledBeforeIntent","BeforeCommandPublication"}[index++];
            d["PageRangeProofContract"]=2;d["PreflightStage"]=stage;d["RequestNotPublished"]=true;
            ulong mask=ulong.Parse(d["DeferredMask"]!.ToJsonString());var ranges=new JsonArray();foreach(var spec in ShopNativeV17Contract.RequestedRanges)ranges.Add(Range(spec,image,mask,stage));d["RequestedCodeRanges"]=ranges;
        }
        return rows;
    }
    private static JsonObject FailingRange(ulong image,string stage)
    {
        var range=Range(ShopNativeV17Contract.RequestedRanges[3],image,0,stage,false);
        range["After"]!["Protect"]=1;range["StableAttributes"]=false;range["PageRangeEquivalent"]=false;return range;
    }
    private static JsonObject TypedPageError(ulong image,string stage)=>new(){["Type"]="NativePreflightException",["Code"]="NativePreflightPageChanged",["Phase"]=stage=="BeforeTransport"?"Preflight":"BeforePublish",["Rva"]="0x277CF0",["PageRangeProofContract"]=2,["PreflightStage"]=stage,["RequestNotPublished"]=true,["Message"]=$"Current requested code range page proof changed: address=0x{image+0x277CF0:X} length=549."};
    private static List<JsonObject> ModernFailure()
    {
        var rows=Rows(System.Text.Encoding.UTF8.GetString(Frozen()));Modern(rows,Image);
        rows.First(r=>Kind(r)=="PREFLIGHT_PAGE_AB_ERROR")["Data"]=FailingRange(Image,"BeforeTransport");
        rows.First(r=>Kind(r)=="ERROR")["Data"]=TypedPageError(Image,"BeforeTransport");return rows;
    }
    private static List<JsonObject> InstalledModernFailure()
    {
        ulong image=(ulong)Field("Image");var rows=Modern(BaseFixture("InstalledRefusal"),image);
        int at=rows.FindIndex(r=>Kind(r)=="NATIVE_CODE_PAGE_UNAVAILABLE");rows.RemoveAt(at);
        var full=Modern(BaseFixture("Native",3u,3u,false),image);int begin=full.FindIndex(r=>Kind(r)=="SOURCE")+1,end=full.FindIndex(r=>Kind(r)=="PREFLIGHT_AB_CLOSED");
        var group=full.Skip(begin).Take(end-begin).Select(r=>r.DeepClone().AsObject()).ToList();
        rows.InsertRange(at,group);at+=group.Count;
        rows.Insert(at,new(){["Kind"]="PREFLIGHT_PAGE_AB_ERROR",["Data"]=FailingRange(image,"InstalledBeforeIntent")});
        Data(rows,"ERROR").Clear();foreach(var pair in TypedPageError(image,"InstalledBeforeIntent"))Data(rows,"ERROR")[pair.Key]=pair.Value?.DeepClone();return rows;
    }
    private static void ModernChecks(Action<string,Action> check)
    {
        var source=(SourceIdentity)Field("Source");ulong image=(ulong)Field("Image"),target=(ulong)Field("Target"),hwnd=(ulong)Field("Hwnd"),shop=(ulong)Field("Shop"),owner=(ulong)Field("Owner");var epoch=(DateTimeOffset)Field("Epoch");
        ShopNativeOutcome Parse(List<JsonObject> rows)=>ShopNativeOutcome.Parse(SyntheticText(rows),source,image,target,Data(rows,"END")["ExitCode"]!.GetValue<int>());
        ShopNativeReadiness Ready(List<JsonObject> rows)=>ShopNativeReadinessProof.Parse(SyntheticText(rows),source,hwnd,shop,owner,0,epoch,epoch.AddMilliseconds(950),"synthetic-marker2-not-live");
        for(uint mask=0;mask<=3;mask++)
        {
            uint m=mask;
            check("modern_returned_mask_"+m,()=>{var p=Parse(Modern(BaseFixture("Native",m,m,false),image));Need(p.SafeToSettle&&p.Returned,p.Detail);});
            check("modern_readiness_mask_"+m,()=>{var p=Ready(Modern(BaseFixture("Readiness",m,false),image));Need(p.Ready,p.Detail);});
        }
        check("modern_positive_region_size_only_range_stays_valid",()=>{var rows=Modern(BaseFixture("Readiness",0u,false),image);var range=Data(rows,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]![3]!;range["After"]!["Size"]=12288;range["RegionSizeChanged"]=true;Need(Ready(rows).Ready,"Covered stable range rejected because adjacent region size grew");});
        check("modern_first_preflight_noentry",()=>{var p=ShopNativeOutcome.Parse(SyntheticText(ModernFailure()),Source,Image,Target,1);Need(p.SafeToSettle&&p.NoEntryConfirmed&&p.Failure?.Code=="NativePreflightPageChanged",p.Detail);});
        check("modern_installed_before_intent_noentry",()=>{var p=Parse(InstalledModernFailure());Need(p.SafeToSettle&&p.NoEntryConfirmed&&p.Failure is {Code:"NativePreflightPageChanged",Phase:"Preflight"},p.Detail);});
        void Reject(string name,Action<List<JsonObject>> mutate,uint mask=0)=>check("modern_reject_"+name,()=>{var rows=Modern(BaseFixture("Readiness",mask,false),image);mutate(rows);Need(!Ready(rows).Ready,"Tampered new readonly proof granted readiness: "+name);});
        Reject("missing_marker",r=>Data(r,"START").Remove("PageRangeProofContract"));
        Reject("wrong_marker",r=>Data(r,"START")["PageRangeProofContract"]=1);
        Reject("missing_end_marker",r=>Data(r,"END").Remove("PageRangeProofContract"));
        Reject("wrong_end_version",r=>Data(r,"END")["ManagedProgramVersion"]="17.0.0");
        Reject("missing_ranges",r=>Data(r,"PREFLIGHT_AB_CLOSED").Remove("RequestedCodeRanges"));
        Reject("range_missing",r=>Data(r,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]!.AsArray().RemoveAt(0));
        Reject("range_duplicated",r=>Data(r,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]!.AsArray().Add(Data(r,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]![0]!.DeepClone()));
        foreach(string field in new[]{"Address","Length","RequestedRangeCoveredA","RequestedRangeCoveredB","SameBase","StableAttributes","PageRangeEquivalent","FinalSourceClosureCompleted","RequestNotPublished","ByteProofRecorded","ReadAComplete","ReadBComplete","BytesAbEqual","BeforeSha256","AfterSha256","PreflightStage"})
        {string key=field;Reject("range_field_"+key,r=>{var d=Data(r,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]![3]!.AsObject();d[key]=key switch{"Address"=>JsonValue.Create(image+0x277CF1),"Length"=>JsonValue.Create(548),"BeforeSha256" or "AfterSha256"=>JsonValue.Create(new string('0',64)),"PreflightStage"=>JsonValue.Create("BeforeCommandPublication"),_=>JsonValue.Create(false)};});}
        foreach(string field in new[]{"Base","AllocationBase","AllocationProtect","PartitionId","Size","State","Protect","Type"})
        {string key=field;Reject("mbi_"+key,r=>{var d=Data(r,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]![3]!["After"]!.AsObject();d[key]=ulong.Parse(d[key]!.ToJsonString())+(key=="Base"?4096UL:1UL);});}
        Reject("cold_fabricated_match",r=>{var d=Data(r,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]![4]!;d["ByteProofRecorded"]=true;d["ReadAComplete"]=true;d["ReadBComplete"]=true;d["BytesAbEqual"]=true;d["BeforeSha256"]=ShopNativeV17Contract.RequestedRanges[4].Sha256;d["AfterSha256"]=ShopNativeV17Contract.RequestedRanges[4].Sha256;},3);
        check("modern_third_stage_failure_remains_uncertain",()=>{var rows=InstalledModernFailure();Data(rows,"PREFLIGHT_PAGE_AB_ERROR")["PreflightStage"]="BeforeCommandPublication";Data(rows,"ERROR")["PreflightStage"]="BeforeCommandPublication";rows.Insert(rows.FindIndex(r=>Kind(r)=="PREFLIGHT_PAGE_AB_ERROR"),new(){["Kind"]="EVENT_INTENT_DURABLE",["Data"]=new JsonObject{["Calls"]=1,["NoAutomaticRetransmission"]=true}});var p=Parse(rows);Need(!p.SafeToSettle&&!p.NoEntryConfirmed,"Synthetic third-stage intent settled without approved evidence");});
        check("modern_new_failure_flag_without_typed_error_rejected",()=>{var rows=ModernFailure();Data(rows,"ERROR")["Type"]="InvalidOperationException";Need(!ShopNativeOutcome.Parse(SyntheticText(rows),Source,Image,Target,1).SafeToSettle,"Flag-only proof settled");});
    }
}
