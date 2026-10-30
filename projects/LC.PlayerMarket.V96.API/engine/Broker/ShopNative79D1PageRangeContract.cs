using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Service;

internal static partial class ShopNative79D1Contract
{
    internal sealed record RangeSpec(ulong Rva,int Length,string Sha256,ulong DeferredBit=0);
    // Exact slices of the already pinned V17 GuardCode/PacketGuards. No runtime
    // values or previously protected bytes are substituted into a new proof.
    internal static readonly RangeSpec[] RequestedRanges=[
        new(0x3FD40,209,"A27A1438FC58D0BF4FF809C4C87F0187DB25076456DBCE3234250CF365BC1E43"),
        new(0x287BB0,1104,"BA2BE8D123D2DF8F7E8EA7E7929ED220283D221357EE57AA2ADBEA75BF9E881B"),
        new(0x288000,2242,"C96035E19462C9984E0C254F4ACBEA6C6E8E3D44EF9CCB61432E3BFAC4094AA2"),
        new(0x277F90,112,"5E4F63BEA0D1AD73F54F082F09385556AE4E7AC592B7A2770CFEABC185F27E0F"),
        new(0x278000,437,"D7CBCD2B10FCE11E17407213653087BB952D2F7EDDC270A454977A12EE602543"),
        new(0x731AA0,91,"0B5262EFE80D968E1041568C6C0EC6CC641FCE8DAED2377192F7B9744CD80817"),
        new(0x7710F0,67,"E8970D042A2CEE49CA238F302E102D5B00298E2CD31805D20EC4DB0BC41ECE4B"),
        new(0x731AA0,1,"44BD7AE60F478FAE1061E11A7739F4B94D1DAF917982D33B6FC8A01A63F89C21"),
        new(0x7710F0,1,"3F39D5C348E5B79D06E842C114E6CC571583BBF44E4B0EBFDA1A01EC05745D43")];

    internal static bool IsManagedProducer17(JsonElement value)
    {
        string version=T(value,"ManagedProgramVersion");
        return version=="17.0.1" && N(value,"PageRangeProofContract")==2;
    }
    internal static bool NewRangeProducer(JsonElement start)=>T(start,"ManagedProgramVersion")=="17.0.1" && N(start,"PageRangeProofContract")==2;
    internal static bool TerminalMatches(JsonElement start,JsonElement end)=>Certificate(end)
        && (NewRangeProducer(start) ? T(end,"ManagedProgramVersion")=="17.0.1" && N(end,"PageRangeProofContract")==2
            : !end.TryGetProperty("PageRangeProofContract",out _) && (!end.TryGetProperty("ManagedProgramVersion",out var version)
                || version.ValueKind==JsonValueKind.String && version.GetString()=="17.0.0"));
    internal static bool SameManagedProducer(JsonElement left,JsonElement right)=>IsManagedProducer17(left)
        && IsManagedProducer17(right) && T(left,"ManagedProgramVersion")==T(right,"ManagedProgramVersion");

    private static readonly string[] PageFields=["Base","AllocationBase","AllocationProtect","PartitionId","Size","State","Protect","Type"];
    internal static void PageShape(JsonElement page,ulong image)
    {
        Need(page.ValueKind==JsonValueKind.Object && page.EnumerateObject().Count()==8
            && PageFields.All(k=>page.TryGetProperty(k,out _)),"Exact eight-field MBI required");
        foreach(string field in PageFields)_=N(page,field);
        ulong b=N(page,"Base"),size=N(page,"Size");
        Need(b>=image && b-image<ShopNativeLegacy79D1Package.SupportedImageSize && size>0
            && size<=ShopNativeLegacy79D1Package.SupportedImageSize-(b-image)
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
        var ranges=F(closed,"RequestedCodeRanges");Need(ranges.ValueKind==JsonValueKind.Array && ranges.GetArrayLength()==RequestedRanges.Length,"Nine exact current requested ranges required");
        for(int i=0;i<RequestedRanges.Length;i++)RequestedRange(ranges[i],image,RequestedRanges[i],mask,stage,true,true);
    }
}
