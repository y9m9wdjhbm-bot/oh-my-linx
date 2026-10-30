using System.Buffers.Binary;
using MemoryHub;

namespace MemoryHub.Reader;

/// <summary>Pure exact-code comparison. The caller owns source closure and cancellation.</summary>
public static class PersonalShopItemKindCodeVerifier
{
    public static bool Verify(string sha256, ulong imageBase, Func<ulong, int, byte[]> read)
    {
        var contract = PersonalShopItemCodeContracts.ForBuild(sha256);
        if (contract is null || imageBase < 0x10000 || imageBase > 0x7FFFFFFFFFFFUL) return false;
        bool Address(uint rva, int count, out ulong address)
        {
            address = 0;
            if (count <= 0 || imageBase > 0x7FFFFFFFFFFFUL - rva - (ulong)count) return false;
            address = imageBase + rva; return true;
        }
        foreach (var span in contract.CodeSpans)
        {
            byte[] expected = Convert.FromHexString(span.Hex);
            if (!Address(span.Rva, expected.Length, out ulong address)) return false;
            byte[] actual = read(address, expected.Length);
            if (actual is null || actual.Length != expected.Length || !actual.AsSpan().SequenceEqual(expected)) return false;
        }
        foreach (var descriptor in contract.PrefixDescriptors)
        {
            if (!Address(descriptor.DescriptorRva, 16, out ulong address)
                || !Address(descriptor.TextRva, checked((int)descriptor.Length), out ulong textAddress)) return false;
            byte[] pair = read(address, 16);
            if (pair is null || pair.Length != 16 || BinaryPrimitives.ReadUInt64LittleEndian(pair) != textAddress
                || BinaryPrimitives.ReadUInt64LittleEndian(pair.AsSpan(8)) != descriptor.Length) return false;
        }
        return true;
    }
}
