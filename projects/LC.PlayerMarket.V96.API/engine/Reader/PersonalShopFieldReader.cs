using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MemoryHub.Reader;

internal static class PersonalShopFieldReader
{
    internal static ShopActionSourceCandidate? Capture(ReadSession memory, Journal journal, Budget budget, ItemStateContradictionStore contradictions, bool bindingOnly = false)
    {
        if (!journal.Enabled("personalShopListing")) return null;
        var sampler = new PersonalShopSampler(memory, journal, budget, contradictions);
        var result = sampler.Run(bindingOnly);
        journal.Set("personalShopListing", result, result.SampledAt, result.Evidence);
        return sampler.ActionSource;
    }
}

/// <summary>Selected typed catalog and row path only. Never invokes LC functions.</summary>
internal sealed partial class PersonalShopSampler(ReadSession memory, Journal journal, Budget budget, ItemStateContradictionStore contradictions)
{
    private sealed record Proof(ulong Address, byte[] Bytes, string Purpose);
    private sealed record TypeInfo(string Name, ulong Vtable, ulong Col, uint Descriptor, uint Hierarchy,
        uint ObjectOffset, uint ConstructionOffset, bool FirstSlotExecutable, bool SecondSlotExecutable);
    private sealed record CatalogEntry(string Key, ulong Node, ulong Object, ulong Control, TypeInfo? Type);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ProtectedTypes = new(StringComparer.Ordinal)
    { ".?AVLayout@ui@cocos2d@@", ".?AVListView@ui@cocos2d@@", ".?AVText@ui@cocos2d@@" };
    private static readonly HashSet<string> NodeTypes = new(ProtectedTypes, StringComparer.Ordinal)
    { ".?AVNode@cocos2d@@", ".?AVLabel@cocos2d@@", ".?AVScene@cocos2d@@" };
    private readonly List<Proof> proofs = [];
    private readonly Dictionary<ulong, TypeInfo> types = [];
    private readonly HashSet<ulong> verifiedNodeBases = [];
    private readonly Dictionary<ulong, ReadOnlyUiSubsystemContract.Candidate> uiDataTypes = [];
    private readonly Dictionary<ulong, ulong> subsystemControls = [];
    private readonly HashSet<ulong> selectedSubsystemControls = [];
    private readonly List<string> partial = [];
    private ulong director, scene;
    private bool currentItemKindCodeVerified, currentItemIdentificationVerified;
    internal ShopActionSourceCandidate? ActionSource { get; private set; }

    private static ulong U64(byte[] b, int o = 0) => Probe.U64(b, o);
    private static uint U32(byte[] b, int o = 0) => Probe.U32(b, o);
    private static int I32(byte[] b, int o = 0) => unchecked((int)U32(b, o));
    private static string Hex(ulong value) => Probe.Hex(value);
    private static bool Pointer(ulong value) => value >= 0x10000 && value <= 0x7FFFFFFFFFFFUL && (value & 7) == 0;
    private static void Need(bool condition, string code, string detail)
    { if (!condition) throw new ProbeFailure(code, detail); }
    private static void Ptr(ulong value) => Need(Pointer(value), "POINTER_RANGE", "商店指標未對齊或超界。");
    private bool InImage(ulong at, int count) => at >= memory.Image && at - memory.Image <= uint.MaxValue && memory.InImage((uint)(at - memory.Image), count);
    private byte[] Raw(ulong at, int count, string purpose)
    {
        budget.Check();
        if (InImage(at, count)) return memory.Required((uint)(at - memory.Image), count);
        if (!memory.Pointed(at, count, out var bytes)) throw new ProbeFailure("SHOP_READ_FAILED", purpose + " 無法完整唯讀。", memory.LastNativeError);
        return bytes;
    }
    private byte[] Read(ulong at, int count, string purpose)
    {
        Need(proofs.Count < 16000, "SHOP_PROOF_LIMIT", "商店 A/B 證據筆數超界。");
        byte[] bytes = Raw(at, count, purpose); proofs.Add(new(at, bytes, purpose)); return bytes;
    }
    private byte[] Meta(ulong at, int count, string purpose)
    { Need(InImage(at, count), "METADATA_RANGE", "商店型別資料不在主映像。"); return Read(at, count, purpose); }
    private void Partial(string reason) { if (!partial.Contains(reason)) partial.Add(reason); }
    private string Name(uint descriptor)
    {
        var bytes = new List<byte>();
        for (int offset = 0; offset < 256; offset += 16)
        {
            foreach (byte value in Meta(memory.Image + descriptor + 16 + (uint)offset, 16, "完整 RTTI 名稱"))
            {
                if (value == 0)
                {
                    string name = Encoding.ASCII.GetString(bytes.ToArray());
                    Need(name.StartsWith(".?AV", StringComparison.Ordinal) || name.StartsWith(".?AU", StringComparison.Ordinal), "RTTI_NAME", "完整型別前綴不符。");
                    return name;
                }
                Need(value is >= 33 and <= 126, "RTTI_NAME", "RTTI 名稱不是 ASCII。"); bytes.Add(value);
            }
        }
        throw new ProbeFailure("RTTI_NAME", "256 bytes 內沒有完整 RTTI 名稱。");
    }
    private TypeInfo Type(ulong obj)
    {
        Ptr(obj); ulong vt = U64(Read(obj, 8, "物件 vptr"));
        if (types.TryGetValue(vt, out var cached)) return cached;
        Need((vt & 7) == 0 && vt >= 8 && InImage(vt - 8, 24), "RTTI_VTABLE", "vtable 不在主映像。");
        byte[] prefix = Meta(vt - 8, 24, "vtable/COL/兩函式槽"); ulong col = U64(prefix);
        Need((col & 3) == 0, "RTTI_COL", "COL 未對齊。"); byte[] body = Meta(col, 24, "primary COL");
        Need(U32(body) == 1 && U32(body, 20) == col - memory.Image, "RTTI_COL", "COL signature/self 不符。");
        uint td = U32(body, 12), hierarchy = U32(body, 16);
        Meta(memory.Image + td, 16, "TD prefix"); Meta(memory.Image + hierarchy, 16, "CHD header");
        var type = new TypeInfo(Name(td), vt, col, td, hierarchy, U32(body, 4), U32(body, 8),
            memory.Executable(U64(prefix, 8)), memory.Executable(U64(prefix, 16)));
        types.Add(vt, type); return type;
    }
    private TypeInfo Exact(ulong obj, string name)
    {
        var type = Type(obj);
        bool delegateRoot = name is ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@" or ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@";
        Need(type.Name == name && type.ObjectOffset == 0 && type.ConstructionOffset == 0 &&
            (type.FirstSlotExecutable || type.SecondSlotExecutable || delegateRoot || NodeTypes.Contains(name) || name == ReadOnlyUiSubsystemContract.Name), "EXACT_TYPE", "商店精確型別不符：" + name);
        if (name == ReadOnlyUiSubsystemContract.Name && !uiDataTypes.ContainsKey(type.Vtable))
        {
            // This proof only identifies read-only UI data. It does not grant
            // callability to any function slot or change native action guards.
            try
            {
                uiDataTypes.Add(type.Vtable, ReadOnlyUiSubsystemContract.Inspect(memory,
                    new(checked((uint)(type.Vtable - memory.Image)), type.Descriptor, checked((uint)(type.Col - memory.Image))),
                    (at, count) => Meta(at, count, "UI subsystem data contract"), budget.Check));
            }
            catch (InvalidDataException error) { throw new ProbeFailure("SHOP_UI_DATA_TYPE", error.Message); }
        }
        if (NodeTypes.Contains(name) && name != ".?AVDirector@cocos2d@@") NodeBase(type);
        return type;
    }
    private void NodeBase(TypeInfo type)
    {
        if (verifiedNodeBases.Contains(type.Vtable)) return;
        byte[] chd = Meta(memory.Image + type.Hierarchy, 16, "完整 Node 階層");
        uint count = U32(chd, 8), array = U32(chd, 12);
        Need(U32(chd) == 0 && count is > 0 and <= 64, "NODE_HIERARCHY", "Node 基底數超界。");
        byte[] entries = Meta(memory.Image + array, checked((int)count * 4), "Node BCD array"); int matches = 0;
        for (int i = 0; i < count; i++)
        {
            byte[] bcd = Meta(memory.Image + U32(entries, i * 4), 28, "Node BCD");
            if (Name(U32(bcd)) != ".?AVNode@cocos2d@@") continue;
            Need(I32(bcd, 8) == 0 && I32(bcd, 12) == -1 && I32(bcd, 16) == 0, "NODE_BASE_OFFSET", "Node 基底不是 offset 0。"); matches++;
        }
        Need(matches == 1, "NODE_BASE", "Node 基底不存在或不唯一。"); verifiedNodeBases.Add(type.Vtable);
    }
    private ulong[] Vector(byte[] header, string purpose, int stride = 8, int maxCount = 128)
    {
        ulong begin = U64(header), end = U64(header, 8), cap = U64(header, 16);
        if (begin == 0 || end == 0 || cap == 0) { Need(begin == 0 && end == 0 && cap == 0, "VECTOR", purpose + " 空集合非全零。"); return []; }
        Ptr(begin); Ptr(end); Ptr(cap);
        Need(end >= begin && cap >= end && (end - begin) % (uint)stride == 0 && (cap - begin) % (uint)stride == 0 &&
            (end - begin) / (uint)stride <= (uint)maxCount && (cap - begin) / (uint)stride <= 4096, "VECTOR_LIMIT", purpose + " 容器界限不符。");
        if (begin == end) return [];
        byte[] bytes = Read(begin, checked((int)(end - begin)), purpose + " 完整元素");
        var values = new ulong[(int)((end - begin) / (uint)stride)]; var unique = new HashSet<ulong>();
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = U64(bytes, i * stride); Ptr(values[i]); Need(unique.Add(values[i]), "VECTOR_DUPLICATE", "重複元素。");
            if (stride == 16) { ulong control = U64(bytes, i * stride + 8); Ptr(control); subsystemControls.Add(values[i], control); }
        }
        return values;
    }
    private string String(byte[] header, int offset, int maximumBytes, string purpose)
    {
        ulong length = U64(header, offset + 16), cap = U64(header, offset + 24);
        Need(length <= (uint)maximumBytes && cap >= length && (cap == 15 && length <= 15 || cap is >= 16 and <= 65536), "STRING_LIMIT", purpose + " std::string 超界。");
        int count = checked((int)length + 1); byte[] bytes;
        if (cap == 15) bytes = header.AsSpan(offset, count).ToArray();
        else { ulong at = U64(header, offset); Ptr(at); bytes = Read(at, count, purpose + " 完整字串"); }
        Need(bytes[^1] == 0 && bytes.AsSpan(0, bytes.Length - 1).IndexOf((byte)0) < 0, "STRING_NUL", purpose + " NUL 不完整。");
        return StrictUtf8.GetString(bytes, 0, bytes.Length - 1);
    }
    private List<CatalogEntry> Catalog(ulong ui)
    {
        Exact(ui, ".?AVUISubsystem@ui@lineage@@");
        byte[] h = Read(ui + 0x350, 56, "UI catalog header");
        ulong head = U64(h), count = U64(h, 8), buckets = U64(h, 16), end = U64(h, 24), cap = U64(h, 32), mask = U64(h, 40), bucketCount = U64(h, 48);
        Ptr(head); Ptr(buckets); Ptr(end); Ptr(cap);
        Need(count <= 128 && bucketCount is > 0 and <= 1048576 && (bucketCount & (bucketCount - 1)) == 0 && mask == bucketCount - 1 &&
            end >= buckets && cap >= end && end - buckets == bucketCount * 16 && (cap - buckets) % 16 == 0 && cap - buckets <= 32 * 1024 * 1024,
            "CATALOG_LAYOUT", "UI catalog 布局不符。");
        byte[] sentinel = Read(head, 16, "catalog sentinel"); ulong cursor = U64(sentinel), last = U64(sentinel, 8), previous = head;
        var seen = new HashSet<ulong>(); var result = new List<CatalogEntry>();
        while (cursor != head)
        {
            Ptr(cursor); Need(seen.Count < 128 && (ulong)seen.Count < count && seen.Add(cursor), "CATALOG_CYCLE", "catalog 數量或循環不符。");
            byte[] n = Read(cursor, 64, "catalog links/key/shared pointer"); ulong next = U64(n), prev = U64(n, 8); Ptr(next); Ptr(prev);
            Need(prev == previous && next != cursor && U64(Read(next + 8, 8, "next reciprocal")) == cursor && U64(Read(prev, 8, "previous reciprocal")) == cursor, "CATALOG_LINK", "catalog 回指失配。");
            string key = String(n, 16, 127, "catalog key");
            Need(key.All(c => c is >= '!' and <= '~'), "CATALOG_KEY", "catalog key 不符 ASCII。");
            if (key == "PersonalShopLayout")
            { ulong obj = U64(n, 48), control = U64(n, 56); Ptr(obj); Ptr(control); result.Add(new(key, cursor, obj, control, Type(obj))); }
            previous = cursor; cursor = next;
        }
        Need((ulong)seen.Count == count && previous == last, "CATALOG_COUNT", "catalog 數量或末節點不符。"); return result;
    }
    private void CloseProofs(string stage)
    {
        foreach (var proof in uiDataTypes.Values)
        {
            try { proof.CloseLocal((at, count) => Raw(at, count, stage), at => ReadOnlyUiSubsystemContract.Query(memory, at), budget.Check); }
            catch (InvalidDataException error) { throw new ProbeFailure("SHOP_UI_DATA_TYPE_CHANGED", error.Message); }
        }
        foreach (ulong control in selectedSubsystemControls) VerifySubsystemAlive(control);
        int end = proofs.Count;
        for (int i = 0; i < end; i++)
        { var p = proofs[i]; Need(Raw(p.Address, p.Bytes.Length, stage).AsSpan().SequenceEqual(p.Bytes), "GROUP_SOURCE_CHANGED", "商店來源／所選資料在 A/B 核對中變更：" + p.Purpose); }
    }
    private void SelectSubsystemOwner(ulong obj)
    {
        Need(subsystemControls.TryGetValue(obj, out ulong control), "SHOP_SUBSYSTEM_OWNER", "商店子系統不在完整所屬集合。");
        ulong vtable = U64(Read(control, 8, "selected subsystem control vptr"));
        Need(InImage(vtable, 8), "SHOP_SUBSYSTEM_OWNER", "商店子系統控制型別不在目前 LC 映像。");
        VerifySubsystemAlive(control); selectedSubsystemControls.Add(control);
    }
    private void VerifySubsystemAlive(ulong control)
    {
        byte[] counts = Raw(control + 8, 8, "selected subsystem shared ownership");
        Need(I32(counts) > 0 && I32(counts, 4) > 0,
            "SHOP_SUBSYSTEM_OWNER", "商店子系統所屬生命週期無效。");
    }
    internal PersonalShopListing Run(bool bindingOnly = false)
    {
        // A newly relocated build may be sampled only through its own exact
        // TD/COL/vtable bindings. Every selected object, owner, bank and row is
        // subsequently closed by this sampler; an absent layout remains partial.
        foreach(string name in new[]{".?AVUISubsystem@ui@lineage@@",".?AVPlayerShopSubsystem@lineage@@",
            ".?AV?$_Ref_count_obj2@VItemEntity@lineage@@@std@@"})
            Need(memory.Profile.TypeBindings.ContainsKey(name),"SHOP_TYPE_UNVERIFIED","此 LC 版本缺少玩家商店必要型別的完整定位："+name);
        var sampledAt = DateTimeOffset.UtcNow;
        director = U64(Read(memory.Address(0x17E00D8), 8, "profile Director global")); Exact(director, ".?AVDirector@cocos2d@@");
        scene = U64(Read(director + 0x150, 8, "Director Scene")); Exact(scene, ".?AVScene@cocos2d@@");
        byte[] links = Read(scene + 0x190, 16, "Scene parent/Director");
        Need(U64(links) == 0 && U64(links, 8) == director, "GROUP_SOURCE_CHANGED", "商店場景來源不符。"); journal.RequireScene(director, scene);
        ulong root = U64(Read(memory.Address(0x1642B60), 8, "profile service global")); Exact(root, ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@");
        Need(Read(root + 0xD8, 1, "service ready")[0] != 0, "SHOP_SERVICE_NOT_READY", "角色服務尚未就緒。");
        ulong holder = U64(Read(root + 0xD0, 8, "service holder")); Exact(holder, ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@");
        ulong[] subsystems = Vector(Read(holder + 0x180, 24, "subsystem vector"), "subsystem", 16);
        var ui = new List<ulong>(); var shops = new List<ulong>();
        foreach (ulong obj in subsystems)
        {
            var type = Type(obj);
            if (type.Name == ".?AVUISubsystem@ui@lineage@@") { Exact(obj, type.Name); ui.Add(obj); }
            if (type.Name == ".?AVPlayerShopSubsystem@lineage@@") { Exact(obj, type.Name); shops.Add(obj); }
        }
        Need(ui.Count == 1 && shops.Count == 1, "SHOP_ROOT_NOT_UNIQUE", "UI／玩家商店 subsystem 不唯一。");
        SelectSubsystemOwner(ui[0]); SelectSubsystemOwner(shops[0]);
        ulong ownerId = U64(Read(shops[0] + 0x2C0, 8, "same-build code-derived selected shop ID"));
        if (bindingOnly) return FinishBindingOnly(sampledAt, root, ui[0], shops[0], ownerId);
        currentItemKindCodeVerified = CurrentBuildItemKindCodeVerified();
        currentItemIdentificationVerified = currentItemKindCodeVerified && CurrentBuildItemIdentificationVerified();
        ShopBanks banks = ReadShopBanks(shops[0], ownerId);
        var catalog = Catalog(ui[0]);
        Need(catalog.Count <= 1, "SHOP_LAYOUT_AMBIGUOUS", "商店 layout 不唯一。");
        if (catalog.Count == 1) InspectPersonalShopRows(catalog[0]);
        VerifyShopItemControlsAlive();
        CloseProofs("商店完整最終 B"); CloseItemStateProof(); AttachItemStateContradictionCommit();
        var previous = journal.SourceClosure;
        journal.SourceClosure = () => { previous?.Invoke(); VerifyShopItemControlsAlive(); CloseProofs("跨資料組商店來源 B"); CloseItemStateProof(); journal.RequireScene(director, scene); };
        string own = journal.SelfId?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        string session = $"{memory.ProcessId}:{memory.CreationFileTime}:{memory.Image:X}:{director:X}:{scene:X}:{own}";
        // This is still a local candidate. CurrentLcSource promotes it only
        // after the outer PE, process, file, source and selected-data closure.
        ActionSource = new(session, sampledAt, shops[0], ownerId, root, ui[0], director, scene);
        if (catalog.Count == 0) return new() { SourceSessionKey = session, SampledAt = sampledAt, OwnerIdRaw = ownerId,
            LayoutState = "Absent", Complete = false, BankFingerprint = banks.Fingerprint, BankState = "Closed",
            RawBankSummary = banks.Summary, SlotRows = banks.Rows, Evidence = "ClosedCatalog_NoPersonalShopLayout;ClosedBanks_NoPageJoin;NotNewAck" };
        Need(pendingPersonalShopRows.Count == 1, "SHOP_ROW_RESULT", "商店列結果不唯一。");
        var raw = JsonSerializer.SerializeToElement(pendingPersonalShopRows[0]);
        var rows = new List<PersonalShopRow>();
        foreach (var row in raw.GetProperty("rows").EnumerateArray())
        {
            string? Text(string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            ulong? price = row.TryGetProperty("numericPriceCandidate", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number) ? number : null;
            rows.Add(new(row.GetProperty("rowIndex").GetInt32(), Text("state") ?? "Unknown", row.GetProperty("includedInCandidateList").GetBoolean(),
                Text("nameRaw"), Text("priceRaw"), price, Text("unitRaw"), null, "Unverified", Text("detail") ?? "同列文字；庫存與頁面方向未確認"));
        }
        bool complete = raw.GetProperty("completeForSelectedRowTextScope").GetBoolean();
        var listing = new PersonalShopListing { SourceSessionKey = session, SampledAt = sampledAt, OwnerIdRaw = ownerId, LayoutState = "Present",
            Complete = complete, PageEmpty = complete && rows.All(row => !row.IncludedInListing),
            BankFingerprint = banks.Fingerprint, BankState = "Closed", RawBankSummary = banks.Summary, SlotRows = banks.Rows,
            ContainerRowCount = raw.GetProperty("declaredRowContainerCount").ValueKind == JsonValueKind.Number ? raw.GetProperty("declaredRowContainerCount").GetInt32() : null,
            InspectedRows = raw.GetProperty("inspectedRows").GetInt32(), ExcludedEmptyOrTemplateRows = raw.GetProperty("emptyOrTemplateCandidates").GetInt32(),
            Rows = rows.ToArray(), IncompleteReasons = raw.GetProperty("incompleteReasons").EnumerateArray().Select(v => v.GetString() ?? "Unknown").ToArray(),
            Evidence = "TypedCatalog_RowTextAndBothBanksAB;OwnerContextOnly;VisibilityUnverified;BankContentNotAckGeneration;NotWholeStoreCoverage" };
        var matched = PersonalShopBankRules.Match(listing, memory.Profile.Sha256, currentItemKindCodeVerified, currentItemIdentificationVerified, itemStateEvidence);
        journal.Write("PERSONAL_SHOP_BANK_PAGE_SAMPLE", new { matched.PageMode, matched.PageModeState, matched.BankFingerprint,
            activeFlagRaw = banks.ActiveFlagRaw, summary = banks.Summary,
            typedPayloadRows = banks.Rows.Count(row => row.PayloadVerified), matchedQuantities = matched.Rows.Count(row => row.Quantity is not null),
            responseGenerationVerified = false, nativeFunctionsCalled = false });
        AttachItemStatePublication(matched);
        return matched;
    }
}
