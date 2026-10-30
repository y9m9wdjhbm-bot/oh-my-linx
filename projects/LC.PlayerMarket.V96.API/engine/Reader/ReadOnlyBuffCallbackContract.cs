using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MemoryHub.Reader;

// This establishes a data-location contract, never callability of a function.
// All delegates must use an already bound, read-only current LC session. The
// caller must retain its exact source/profile/root/scene and ownership fences.
internal static class ReadOnlyBuffCallbackContract
{
    internal const string Name = ".?AV?$_Func_impl_no_alloc@V<lambda_1>@?1??Init@BuffSlot@ui@lineage@@AEAAXXZ@XPEAVRef@cocos2d@@_NPEAVEventMouse@7@@std@@";
    internal const string BaseName = ".?AV?$_Func_base@XPEAVRef@cocos2d@@_NPEAVEventMouse@2@@std@@";
    internal const int FunctionSlotCount = 6;

    // Header identity is compared with the already bound source, never used to
    // replace PID/create-time/path/disk identity verification.
    internal sealed record Image(ulong Address, uint Size, uint Timestamp);
    internal sealed record Anchors(uint Vtable, uint Descriptor, uint Col);
    internal sealed record Region(ulong Start, ulong Size, ulong AllocationBase,
        uint State, uint Protect, uint Type);
    internal sealed record Section(uint Start, uint Size, uint Characteristics);
    internal sealed record Metadata(ulong Address, byte[] Bytes);
    internal sealed record Slot(uint Rva, uint ObservedProtection);

    internal static Candidate Inspect(ReadSession memory,Anchors exact,Func<ulong,int,byte[]> readAndTrack,Action check)
        =>Inspect(new(memory.Image,(uint)memory.ImageSize,memory.Profile.Identity.PeTimestamp),exact,readAndTrack,
            at=>Query(memory,at),check);
    internal static Region? Query(ReadSession memory,ulong at)
    {
        if(at<memory.Image||at-memory.Image>=(ulong)memory.ImageSize||!memory.Query((uint)(at-memory.Image),out var region))return null;
        return new(region.BaseAddress,region.RegionSize,region.AllocationBase,region.State,region.Protect,region.Type);
    }

    // A candidate must not be published until CloseLocal AND all existing
    // dynamic graph/source closures succeed. There is intentionally no
    // Executable/Callable property and no function invocation facility.
    internal sealed class Candidate(Image image, Anchors anchors, Metadata[] metadata,
        Section[] sections, Slot[] slots)
    {
        internal Image BoundImage { get; } = image;
        internal Anchors Type { get; } = anchors;
        internal IReadOnlyList<Metadata> Proofs { get; } = metadata;
        internal IReadOnlyList<Slot> FunctionSlots { get; } = slots;

        // readFresh must bypass metadata reuse. Budget and cancellation apply
        // to every read and query through the caller's normal session methods.
        internal void CloseLocal(Func<ulong, int, byte[]> readFresh,
            Func<ulong, Region?> query, Action check)
        {
            foreach (var proof in metadata)
            {
                check();
                var now = readFresh(proof.Address, proof.Bytes.Length);
                Require(now.AsSpan().SequenceEqual(proof.Bytes), "METADATA_CHANGED");
            }
            // Protection may change between NOACCESS and RX, but same-image,
            // committed, non-writable code-section membership must still hold.
            foreach (var slot in slots)
            {
                check();
                CheckCodeRegion(image, sections, slot.Rva, query);
            }
        }
    }

    // readAndTrack must return fresh metadata and register it with the caller's
    // closure graph. No executable-byte reads are requested by this helper.
    internal static Candidate Inspect(Image image, Anchors exact,
        Func<ulong, int, byte[]> readAndTrack, Func<ulong, Region?> query, Action check)
    {
        Require(image.Size >= 4096 && image.Address >= 0x10000 &&
            image.Address <= 0x7FFFFFFFFFFFUL - image.Size, "IMAGE_RANGE");
        var proofs = new Dictionary<(ulong, int), Metadata>();
        byte[] Read(uint rva, int count)
        {
            check();
            Require(count is > 0 and <= 4096 && (uint)count <= image.Size &&
                rva <= image.Size - (uint)count, "METADATA_RANGE");
            ulong at = image.Address + rva;
            var bytes = readAndTrack(at, count);
            Require(bytes.Length == count, "METADATA_PARTIAL");
            var key = (at, count);
            if (proofs.TryGetValue(key, out var prior))
                Require(prior.Bytes.AsSpan().SequenceEqual(bytes), "METADATA_CHANGED");
            else proofs.Add(key, new(at, bytes.ToArray()));
            return bytes;
        }

        var sections = ReadSections(image, Read);
        Require((exact.Vtable & 7) == 0 && exact.Vtable >= 8 &&
            (exact.Descriptor & 7) == 0 && (exact.Col & 3) == 0, "ANCHOR_ALIGNMENT");
        byte[] prefix = Read(exact.Vtable - 8, 8 + FunctionSlotCount * 8);
        Require(U64(prefix) == image.Address + exact.Col, "VTABLE_COL");
        byte[] col = Read(exact.Col, 24);
        Require(U32(col) == 1 && U32(col, 4) == 0 && U32(col, 8) == 0 &&
            U32(col, 12) == exact.Descriptor && U32(col, 20) == exact.Col, "PRIMARY_COL");
        CheckDescriptor(image, exact.Descriptor, Name, Read);

        uint hierarchy = U32(col, 16);
        Require((hierarchy & 3) == 0, "HIERARCHY_ALIGNMENT");
        byte[] chd = Read(hierarchy, 16);
        Require(U32(chd) == 0 && U32(chd, 4) == 0 && U32(chd, 8) == 2,
            "HIERARCHY_SHAPE");
        uint baseArray = U32(chd, 12);
        Require((baseArray & 3) == 0, "BASE_ARRAY_ALIGNMENT");
        byte[] bases = Read(baseArray, 8);
        uint selfBcd = U32(bases), baseBcd = U32(bases, 4);
        Require(selfBcd != baseBcd && (selfBcd & 3) == 0 && (baseBcd & 3) == 0,
            "BASE_DESCRIPTORS");
        byte[] self = Read(selfBcd, 28), parent = Read(baseBcd, 28);
        CheckBase(self, exact.Descriptor, 1);
        Require(U32(self, 24) == hierarchy, "SELF_HIERARCHY_LINK");
        uint parentTd = U32(parent);
        Require(parentTd != exact.Descriptor && (parentTd & 7) == 0, "BASE_TYPE_DISTINCT");
        CheckBase(parent, parentTd, 0);
        CheckDescriptor(image, parentTd, BaseName, Read);
        // The parent's own CHD is not traversed by this bounded primary-type
        // contract. Its link must be aligned/in-image; no claim is made that its
        // bytes are part of this proof. The complete two-base CHD above is read.
        uint parentHierarchy = U32(parent, 24);
        Require(parentHierarchy != hierarchy && (parentHierarchy & 3) == 0 &&
            parentHierarchy <= image.Size - 16, "PARENT_HIERARCHY_RANGE");

        var slots = new Slot[FunctionSlotCount];
        for (int index = 0; index < slots.Length; index++)
        {
            check();
            ulong pointer = U64(prefix, 8 + index * 8);
            Require(pointer >= image.Address && pointer - image.Address < image.Size,
                "SLOT_OUTSIDE_CURRENT_IMAGE");
            uint rva = (uint)(pointer - image.Address);
            uint protection = CheckCodeRegion(image, sections, rva, query);
            slots[index] = new(rva, protection);
        }
        return new(image, exact, proofs.Values.ToArray(), sections, slots);
    }

    private static void CheckDescriptor(Image image, uint rva, string expected,
        Func<uint, int, byte[]> read)
    {
        byte[] bytes = read(rva, 16 + expected.Length + 1);
        ulong typeInfo = U64(bytes);
        Require(typeInfo >= image.Address && typeInfo - image.Address <= image.Size - 8 &&
            (typeInfo & 7) == 0, "TYPEINFO_RANGE");
        Require(bytes.AsSpan(16).SequenceEqual(Encoding.ASCII.GetBytes(expected + '\0')),
            "EXACT_TYPE_NAME");
        // TD+8 is reserved storage: do not invent a must-be-zero rule.
    }

    private static void CheckBase(byte[] bytes, uint descriptor, uint contained)
    {
        Require(U32(bytes) == descriptor && U32(bytes, 4) == contained &&
            U32(bytes, 8) == 0 && U32(bytes, 12) == uint.MaxValue && U32(bytes, 16) == 0 &&
            U32(bytes, 20) == 0x40, "PRIMARY_NONVIRTUAL_BASE");
    }

    private static Section[] ReadSections(Image image, Func<uint, int, byte[]> read)
    {
        byte[] dos = read(0, 64);
        Require(U16(dos) == 0x5A4D, "DOS_SIGNATURE");
        uint ntRva = U32(dos, 60);
        Require(ntRva >= 64 && ntRva <= Math.Min(image.Size - 24, 1024U * 1024), "PE_OFFSET");
        byte[] coff = read(ntRva, 24);
        Require(U32(coff) == 0x4550 && U16(coff, 4) == 0x8664 &&
            U32(coff, 8) == image.Timestamp, "PE_IDENTITY");
        ushort count = U16(coff, 6), optionalBytes = U16(coff, 20);
        Require(count is >= 1 and <= 96 && optionalBytes is >= 112 and <= 1024,
            "PE_HEADER_BOUNDS");
        byte[] optional = read(checked(ntRva + 24), optionalBytes);
        Require(U16(optional) == 0x20B && U32(optional, 56) == image.Size, "PE_OPTIONAL_IDENTITY");
        uint headersSize = U32(optional, 60);
        uint sectionRva = checked(ntRva + 24U + optionalBytes);
        uint sectionBytes = (uint)count * 40;
        Require(headersSize >= sectionRva && sectionBytes <= headersSize - sectionRva &&
            headersSize <= image.Size, "PE_SECTION_HEADER_RANGE");
        byte[] table = read(sectionRva, (int)sectionBytes);
        var sections = new Section[count];
        for (int index = 0; index < count; index++)
        {
            int at = index * 40;
            uint virtualSize = U32(table, at + 8), start = U32(table, at + 12);
            uint rawSize = U32(table, at + 16), flags = U32(table, at + 36);
            // Use PE loaded extent. Require it wholly inside the bound image;
            // no page-rounded gap or overlapping section counts as code.
            uint size = Math.Max(virtualSize, rawSize);
            Require(size != 0 && start >= headersSize && size <= image.Size &&
                start <= image.Size - size, "PE_SECTION_RANGE");
            sections[index] = new(start, size, flags);
        }
        var ordered = sections.OrderBy(s => s.Start).ToArray();
        for (int index = 1; index < ordered.Length; index++)
            Require((ulong)ordered[index - 1].Start + ordered[index - 1].Size <= ordered[index].Start,
                "PE_SECTION_OVERLAP");
        return sections;
    }

    private static uint CheckCodeRegion(Image image, Section[] sections, uint rva,
        Func<ulong, Region?> query)
    {
        var matches = sections.Where(s => rva >= s.Start && rva - s.Start < s.Size).ToArray();
        Require(matches.Length == 1, "SLOT_SECTION_MISSING_OR_AMBIGUOUS");
        uint flags = matches[0].Characteristics;
        Require((flags & 0x60000020) == 0x60000020 && (flags & 0x80000000) == 0,
            "SLOT_SECTION_NOT_READONLY_CODE");
        ulong address = image.Address + rva;
        Region? region = query(address);
        Require(region is not null, "SLOT_QUERY_FAILED");
        var found = region!;
        Require(found.State == 0x1000 && found.Type == 0x1000000 &&
            found.AllocationBase == image.Address && found.Size != 0 &&
            found.Start <= address && found.Start <= ulong.MaxValue - found.Size &&
            address - found.Start < found.Size, "SLOT_NOT_CURRENT_MEM_IMAGE");
        // Exact values reject GUARD, NOCACHE, WRITECOMBINE, RW, RWX, and
        // write-copy pages. NOACCESS is accepted only for data identification.
        Require(found.Protect is 0x01 or 0x20, "SLOT_PROTECTION_NOT_ALLOWED");
        return found.Protect;
    }

    private static ushort U16(byte[] b, int at = 0) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static uint U32(byte[] b, int at = 0) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static ulong U64(byte[] b, int at = 0) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidDataException("BUFF_CALLBACK_DATA_" + code);
    }
}
