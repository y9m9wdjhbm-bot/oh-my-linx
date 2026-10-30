using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Service;

internal static partial class ShopResidentB47CContract
{
    internal sealed record RangeSpec(ulong Rva,int Length,string Sha256,ulong DeferredBit=0);
    // Exact slices of the already pinned V17 GuardCode/PacketGuards. No runtime
    // values or previously protected bytes are substituted into a new proof.
    internal static readonly RangeSpec[] RequestedRanges=[
        new(0x3FDC0,209,"3BAEF08E44FF63FE17E4E9A151DBE9ACECD3C9FBBDFE675160EE54687CF81FCE"),
        new(0x28C630,2512,"23DCB33DCC4009E7F82B457CEFB3AF8BBC0EAE5A53D686C9ECB145CA83C9F972"),
        new(0x28D000,834,"9006E0C7D5BCCBEFC99B5327669BB3D2F8A3E68CD2D787C2B146DBF277664747"),
        new(0x27CA10,549,"9F966C0313F4B67ABD5C64E180D44CA0467868289B53C3B415A3198E76E33F53"),
        new(0x73A0C0,91,"6565910A6445E793C5775FF7F2280C74961B83DB5EC881671859599E3B195CDF"),
        new(0x779760,67,"097C1F8EA99D7615358198174CD58AF40E1AAB93CB864594E3758C352CAECDA1"),
        new(0x73A0C0,1,"44BD7AE60F478FAE1061E11A7739F4B94D1DAF917982D33B6FC8A01A63F89C21"),
        new(0x779760,1,"3F39D5C348E5B79D06E842C114E6CC571583BBF44E4B0EBFDA1A01EC05745D43")];

    internal static bool IsManagedProducer18(JsonElement value)
    {
        string version=T(value,"ManagedProgramVersion");
        return version=="18.0.0" && N(value,"PageRangeProofContract")==2;
    }
    internal static bool NewRangeProducer(JsonElement start)=>T(start,"ManagedProgramVersion")=="18.0.0" && N(start,"PageRangeProofContract")==2;
    internal static bool TerminalMatches(JsonElement start,JsonElement end)=>Certificate(end)
        && (NewRangeProducer(start) ? T(end,"ManagedProgramVersion")=="18.0.0" && N(end,"PageRangeProofContract")==2
            : !end.TryGetProperty("PageRangeProofContract",out _) && (!end.TryGetProperty("ManagedProgramVersion",out var version)
                || version.ValueKind==JsonValueKind.String && version.GetString()=="17.0.0"));
    internal static bool SameManagedProducer(JsonElement left,JsonElement right)=>IsManagedProducer18(left)
        && IsManagedProducer18(right) && T(left,"ManagedProgramVersion")==T(right,"ManagedProgramVersion");

    private static readonly string[] PageFields=["Base","AllocationBase","AllocationProtect","PartitionId","Size","State","Protect","Type"];
    internal static void PageShape(JsonElement page,ulong image)
    {
        Need(page.ValueKind==JsonValueKind.Object && page.EnumerateObject().Count()==8
            && PageFields.All(k=>page.TryGetProperty(k,out _)),"Exact eight-field MBI required");
        foreach(string field in PageFields)_=N(page,field);
        ulong b=N(page,"Base"),size=N(page,"Size");
        Need(b>=image && b-image<ShopNativePackage.SupportedImageSize && size>0
            && size<=ShopNativePackage.SupportedImageSize-(b-image)
            && N(page,"AllocationProtect")<=uint.MaxValue && N(page,"PartitionId")<=ushort.MaxValue
            && N(page,"Protect")<=uint.MaxValue && N(page,"State")<=uint.MaxValue && N(page,"Type")<=uint.MaxValue,
            "MBI bounds or numeric type differs");
    }
    internal static bool RangeCovered(JsonElement page,ulong address,ulong length)=>length>0
        && address<=ulong.MaxValue-length && N(page,"Base")<=address && N(page,"Size")>=length
        && address-N(page,"Base")<=N(page,"Size")-length;
    internal static bool StablePageAttributes(JsonElement a,JsonElement b)=>PageFields.Where(k=>k!="Size").All(k=>N(a,k)==N(b,k));
    internal static void RequestedRange(JsonElement d,ulong image,RangeSpec spec,ulong mask,string stage,bool closed,bool equivalent)
    {
        ulong address=checked(image+spec.Rva);var a=F(d,"Before");var b=F(d,"After");PageShape(a,image);PageShape(b,image);
        bool ca=RangeCovered(a,address,(ulong)spec.Length),cb=RangeCovered(b,address,(ulong)spec.Length);
        bool same=N(a,"Base")==N(b,"Base"),stable=PageFields.Where(k=>k is not ("Base" or "Size")).All(k=>N(a,k)==N(b,k));
        bool eq=ca && cb && same && stable;
        Need(T(d,"Category")=="NativeCodePage" && N(d,"PageRangeProofContract")==2
            && T(d,"PreflightStage")==stage && B(d,"RequestNotPublished") && B(d,"FinalSourceClosureCompleted")==closed
            && N(d,"Address")==address && Hex(T(d,"AddressHex"))==address && Hex(T(d,"Rva"))==spec.Rva
            && N(d,"Length")== (ulong)spec.Length && B(d,"RequestedRangeCoveredA")==ca && B(d,"RequestedRangeCoveredB")==cb
            && B(d,"SameBase")==same && B(d,"StableAttributes")==stable && B(d,"RegionSizeChanged")== (N(a,"Size")!=N(b,"Size"))
            && B(d,"PageRangeEquivalent")==eq && eq==equivalent,"Requested code range fields disagree with actual MBI");
        bool cold=spec.DeferredBit!=0 && (mask&spec.DeferredBit)!=0;
        if(cold)
            Need(!B(d,"ByteProofRecorded") && !B(d,"ReadAComplete") && !B(d,"ReadBComplete")
                && F(d,"BytesAbEqual").ValueKind==JsonValueKind.Null && F(d,"BeforeSha256").ValueKind==JsonValueKind.Null
                && F(d,"AfterSha256").ValueKind==JsonValueKind.Null,"Cold/metadata-only range fabricated byte reads");
        else
            Need(B(d,"ByteProofRecorded") && B(d,"ReadAComplete") && B(d,"ReadBComplete") && B(d,"BytesAbEqual")
                && T(d,"BeforeSha256")==spec.Sha256 && T(d,"AfterSha256")==spec.Sha256,
                "Recorded range does not match exact current A/B bytes");
        Need(N(a,"AllocationBase")==image && N(a,"State")==0x1000 && N(a,"Type")==0x1000000
            && (cold ? N(a,"Protect")==1 : N(a,"Protect") is 0x20 or 0x40 or 0x80),"Initial current code page state differs");
        if(equivalent)Need(N(b,"AllocationBase")==image && N(b,"State")==0x1000 && N(b,"Type")==0x1000000,
            "Final current code page state differs");
    }
    internal static void ClosedRequestedRanges(JsonElement closed,ulong image,ulong mask,string stage)
    {
        Need(N(closed,"PageRangeProofContract")==2 && T(closed,"PreflightStage")==stage && B(closed,"RequestNotPublished"),"New closure stage differs");
        var ranges=F(closed,"RequestedCodeRanges");Need(ranges.ValueKind==JsonValueKind.Array && ranges.GetArrayLength()==RequestedRanges.Length,"Exact B47C current requested ranges required");
        for(int i=0;i<RequestedRanges.Length;i++)RequestedRange(ranges[i],image,RequestedRanges[i],mask,stage,true,true);
    }
}
