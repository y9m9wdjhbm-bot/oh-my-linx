using System.Buffers.Binary;

namespace MemoryHub.Reader;

public sealed record ItemStatePage(ulong BaseAddress, ulong AllocationBase, ulong RegionSize,
    uint State, uint Protect, uint Type, uint AllocationProtect = 0, ushort PartitionId = 0)
{
    public bool Covers(ulong address, int length, ulong image) => length > 0 && State == 0x1000 && Type == 0x1000000
        && AllocationBase == image && BaseAddress <= address && RegionSize <= ulong.MaxValue - BaseAddress
        && address <= ulong.MaxValue - (uint)length && address + (uint)length <= BaseAddress + RegionSize;
    public bool Readable => Protect is 2 or 4 or 8 or 0x20 or 0x40 or 0x80;
}
public sealed record ItemStatePageProof(ulong Address, int Length, ItemStatePage Page);
public sealed record ItemStateSchemaProof(bool RuntimeVerified, bool MetadataVerified, bool StableContradiction,
    string Reason, ItemStatePageProof[] Pages);

/// <summary>Pure bounded byte/page verifier. Caller owns source closure, fresh disk SHA and contradiction persistence.</summary>
public static class PersonalShopSchemaVerifier
{
    public static ItemStateSchemaProof Capture(string build, ulong image, Func<ulong,int,byte[]> read,
        Func<ulong,ItemStatePage?> query)
    {
        var contract = PersonalShopItemCodeContracts.ForBuild(build);
        var pages = new List<ItemStatePageProof>();
        ItemStateSchemaProof Fail(string why, bool contradiction = false) => new(false,false,contradiction,why,pages.ToArray());
        if (contract is null || image < 0x10000 || image > 0x7FFFFFFFFFFFUL - 0x3F5F000) return Fail("UnsupportedBuildOrBase");
        ulong codeAddress = image + contract.FormatterRva; int codeLength = contract.CodeSpans[0].Hex.Length / 2;
        var codePage = query(codeAddress);
        if (codePage is null || !codePage.Covers(codeAddress, codeLength, image)) return Fail("FormatterPageIdentityUnavailable");
        bool metadataOnly = codePage.Protect == 1;
        if (metadataOnly && !PersonalShopBuildCertificate.Supports(build)) return Fail("BuildCertificateUnavailable");
        if (!metadataOnly && !codePage.Readable) return Fail("FormatterPageUnsupported");
        pages.Add(new(codeAddress,codeLength,codePage));
        var spans = contract.CodeSpans.Skip(metadataOnly ? 1 : 0)
            .Select(s => (Address:image+s.Rva, Expected:Convert.FromHexString(s.Hex))).ToList();
        foreach(var descriptor in contract.PrefixDescriptors)
        {
            byte[] expected = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(expected,image+descriptor.TextRva);
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(8),descriptor.Length);
            spans.Add((image+descriptor.DescriptorRva,expected));
        }
        foreach(var span in spans)
        {
            var before=query(span.Address);
            if(before is null || !before.Covers(span.Address,span.Expected.Length,image) || !before.Readable)
                return Fail("MetadataOrCodePageUnavailable");
            byte[] a=read(span.Address,span.Expected.Length);
            if(a is null || a.Length!=span.Expected.Length) return Fail("IncompleteRead");
            byte[] b=read(span.Address,span.Expected.Length);
            var after=query(span.Address);
            if(b is null || b.Length!=span.Expected.Length) return Fail("IncompleteRead");
            if(before!=after || !a.AsSpan().SequenceEqual(b))return Fail("TransientByteOrPageChange");
            pages.Add(new(span.Address,span.Expected.Length,before));
            if(!a.AsSpan().SequenceEqual(span.Expected))return Fail("StableReadableContradiction",true);
        }
        if(query(codeAddress)!=codePage)return Fail("FormatterPageChanged");
        return new(!metadataOnly,true,false,metadataOnly?"BuildCertifiedMetadata":"CurrentRuntimeCodeVerified",pages.ToArray());
    }
    public static bool ClosePages(ItemStateSchemaProof proof, Func<ulong,ItemStatePage?> query) =>
        proof.Pages.All(p=>query(p.Address)==p.Page);
}
