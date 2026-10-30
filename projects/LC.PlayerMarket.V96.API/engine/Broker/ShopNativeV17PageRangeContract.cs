using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Service;

internal static partial class ShopNativeV17Contract
{
    internal sealed record RangeSpec(ulong Rva,int Length,string Sha256,ulong DeferredBit=0);
    // Exact slices of the already pinned V17 GuardCode/PacketGuards. No runtime
    // values or previously protected bytes are substituted into a new proof.
    internal static readonly RangeSpec[] RequestedRanges=[
        new(0x3FC90,209,"29B9045210C0F164763ECD33599EF71E78B5F6B0B13A504C07A40058BA7C35EC"),
        new(0x287910,1776,"8C5F309811586C2574C54EF0FD6ED4FA4DD68DBD192DF7A066543BA390DDB287"),
        new(0x288000,1570,"28B300DCA9AEF6B483E29FDB6541BFA3DF810E31F9C9E2CF4C1917C0E707243B"),
        new(0x277CF0,549,"BB7CC6DA729C2263E31BACBF876E86E5648D3ECF41A5191FCE68E913379973C9"),
        new(0x72D300,91,"BA49FB37E824FC2A33864D390BCEBB5EB52872025FBACCA457CF9C8FF7AB6EFD",1),
        new(0x76BEB0,67,"7E16D7DD5472FC8C48134A98B3ABC910F48B91B50F998758EBF38D51A153C80F",2),
        new(0x72D300,1,Convert.ToHexString(SHA256.HashData(new byte[]{0x48})),1),
        new(0x76BEB0,1,Convert.ToHexString(SHA256.HashData(new byte[]{0x44})),2)];

    internal static bool IsManagedProducer17(JsonElement value)
    {
        string version=T(value,"ManagedProgramVersion");
        return version=="17.0.0" ? !value.TryGetProperty("PageRangeProofContract",out _)
            : version=="17.0.1" && N(value,"PageRangeProofContract")==2;
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
        Need(b>=image && b-image<ShopNativeHistoricalPackage.SupportedImageSize && size>0
            && size<=ShopNativeHistoricalPackage.SupportedImageSize-(b-image)
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
        var ranges=F(closed,"RequestedCodeRanges");Need(ranges.ValueKind==JsonValueKind.Array && ranges.GetArrayLength()==8,"Eight exact requested ranges required");
        for(int i=0;i<8;i++)RequestedRange(ranges[i],image,RequestedRanges[i],mask,stage,true,true);
    }
}
