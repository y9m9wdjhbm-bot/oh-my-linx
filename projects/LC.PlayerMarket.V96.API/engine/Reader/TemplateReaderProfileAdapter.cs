using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LcLocationTemplate;

namespace MemoryHub.Reader;

/// <summary>
/// Imports immutable template candidates into the consumer's existing reader.
/// This adapter does not create runtime Verified checks. CurrentLcSource must
/// still validate live PE/source, exact RTTI, ownership and final source closure.
/// Native function locations grant no invocation. Named hotbar offsets and fixed
/// shop witnesses still require the consumer's current typed read and closure.
/// </summary>
internal static class TemplateReaderProfileAdapter
{
    internal const string PointerFile = "template-active.json";
    internal static readonly IReadOnlyDictionary<string,uint> ShopOffsetWitnesses=new Dictionary<string,uint>(StringComparer.Ordinal)
    {
        ["offset:Actor:id"]=24,["offset:Actor:components"]=32,
        ["offset:ActorComponentBase:owner"]=8,["offset:ActionComponent:directAction"]=192
    };
    internal static IReadOnlyDictionary<string,uint> ShopOffsetWitnessesFor(LcBuildIdentity identity)
    {
        if(!CurrentShopFieldLayoutB47CV1.IsBuild(identity))return ShopOffsetWitnesses;
        return new Dictionary<string,uint>(ShopOffsetWitnesses,StringComparer.Ordinal)
        { ["offset:ActionComponent:directAction"]=CurrentShopFieldLayoutB47CV1.DirectActionOffset };
    }
    private sealed record Pointer(int SchemaVersion, string FileName, string Sha256);

    internal static ReaderProfile Load(string directory)
    {
        var pointerBytes = ReadBounded(Path.Combine(directory, PointerFile), 4096);
        if(pointerBytes.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF}))pointerBytes=pointerBytes[3..];
        RejectDuplicateProperties(pointerBytes);
        var pointer = JsonSerializer.Deserialize<Pointer>(pointerBytes, HubProtocol.Json)
            ?? throw new InvalidDataException("模板作用中索引為空。");
        if (pointer.SchemaVersion != 1 || Path.GetFileName(pointer.FileName) != pointer.FileName
            || !pointer.FileName.StartsWith("template-profile-", StringComparison.Ordinal)
            || !pointer.FileName.EndsWith(".json", StringComparison.Ordinal)
            || pointer.Sha256 is not { Length: 64 } || !pointer.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("模板作用中索引格式或路徑無效。");
        return LoadCandidate(Path.Combine(directory, pointer.FileName), pointer.Sha256);
    }

    internal static ReaderProfile LoadCandidate(string path, string expectedSha256)
    {
        LocationProfile template = ProfileLoader.LoadVerifiedHash(path, expectedSha256);
        byte[] bytes = ReadBounded(path, ProfileLoader.MaximumJsonBytes);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), template.Sha256,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模板在讀取期間變更，拒絕使用。");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("nativeCallsEnabled", out var native) || native.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("模板不得授予原生呼叫。");
        var legacy = root.GetProperty("legacyReader");
        if (legacy.GetProperty("layout").GetString() != "LC-20260908-structure-v1")
            throw new InvalidDataException("模板版型與此讀取器不相容。");
        var fields = template.Fields.Values.Where(f => f.Status == "located").ToArray();
        var requiredSymbols = fields.SelectMany(f => f.RequiredSymbols).ToHashSet(StringComparer.Ordinal);
        var rvas = new Dictionary<uint, uint>();
        foreach (var item in legacy.GetProperty("rvaKeys").EnumerateObject())
        {
            if (!uint.TryParse(item.Name, NumberStyles.None, CultureInfo.InvariantCulture, out uint baseline))
                throw new InvalidDataException("模板基準符號須為十進位 UInt32。");
            uint value = ModuleCandidate(template, item.Value.GetString()!);
            if (!rvas.TryAdd(baseline, value)) throw new InvalidDataException("模板基準符號重複。");
        }
        var types = new Dictionary<string, RelocationTypeBinding>(StringComparer.Ordinal);
        foreach (var item in legacy.GetProperty("typeKeys").EnumerateObject())
        {
            var keys = item.Value;
            string[] references = [keys.GetProperty("descriptor").GetString()!, keys.GetProperty("col").GetString()!, keys.GetProperty("vtable").GetString()!];
            if (OptionalTypeHasUnknown(template, references, requiredSymbols)) continue;
            var binding = new RelocationTypeBinding(item.Name,
                ModuleCandidate(template, keys.GetProperty("descriptor").GetString()!),
                ModuleCandidate(template, keys.GetProperty("col").GetString()!),
                ModuleCandidate(template, keys.GetProperty("vtable").GetString()!));
            // Auxiliary consumers may add RTTI anchors to the shared profile.
            // Validate all three locations first, then retain this reader's names.
            if (!ProfileSymbols.Types.Any(t => t.Name == item.Name)) continue;
            if (!types.TryAdd(item.Name, binding)) throw new InvalidDataException("模板型別重複。");
        }
        var offsetIdentity=template.Identity;
        var shopWitnesses=ShopOffsetWitnessesFor(new(offsetIdentity.Sha256.ToUpperInvariant(),offsetIdentity.FileLength,checked((uint)offsetIdentity.PeOffset),offsetIdentity.PeTimestamp,offsetIdentity.ImageSize,offsetIdentity.Machine));
        var offsets=new Dictionary<string,uint>(StringComparer.Ordinal);
        foreach(var symbol in template.Symbols.Values.Where(s=>s.Kind==LocationKind.FieldOffset&&s.Status=="located"))
        {
            if(symbol.Name is not("offset:HudSlotDataSubsystem:slotsHead" or "offset:HudSlotDataSubsystem:slotsCount" or "offset:HudSlotDataSubsystem:page" or "offset:HudSlotDataSubsystem:focus")&&!ShopOffsetWitnesses.ContainsKey(symbol.Name))continue;
            if(symbol.Value is null||symbol.Value.Value>4096||symbol.Value.Value%4!=0||symbol.RequiredGates.Any(g=>!OffsetGateImplemented(symbol.Name,g)))
                throw new InvalidDataException("模板偏移或驗證契約不符："+symbol.Name);
            if(shopWitnesses.TryGetValue(symbol.Name,out uint witness)&&symbol.Value.Value!=witness)
                throw new InvalidDataException("商店 offset 僅為固定布局 witness，值不同需更新讀取器："+symbol.Name);
            offsets.Add(symbol.Name,checked((uint)symbol.Value.Value));
        }
        if (fields.Length == 0) throw new InvalidDataException("模板尚無可供此讀取器驗證的欄位。");
        foreach (var field in fields)
        {
            if (!ReaderProfile.SupportedKeys.Contains(field.Name) || field.RequiredSymbols.Count == 0
                || field.RequiredGates.Any(g => !FieldGateImplemented(field.Name,g)))
                throw new InvalidDataException("模板欄位或驗證需求超出此讀取器的實作：" + field.Name);
            foreach (var symbol in field.RequiredSymbols)
                if(!offsets.ContainsKey(symbol))_ = ModuleCandidate(template, symbol);
        }
        var identity = template.Identity;
        var candidate = new ReaderProfile("template-" + template.Id,
            new(identity.Sha256.ToUpperInvariant(), identity.FileLength, checked((uint)identity.PeOffset),
                identity.PeTimestamp, identity.ImageSize, identity.Machine),
            rvas, fields.Select(f => f.Name).ToArray(), typeBindings: types,fieldOffsets:offsets);
        _ = candidate.Resolve(0x17E00D8); _ = candidate.Resolve(0x1642B60);
        return candidate;
    }

    private static bool OffsetGateImplemented(string name,string gate)
        =>gate is "source" or "range" or "rtti" or "ownership" or "closure" or "value"
        ||gate=="hotbar_layout"&&name.StartsWith("offset:HudSlotDataSubsystem:",StringComparison.Ordinal)
        ||gate=="shop_layout"&&ShopOffsetWitnesses.ContainsKey(name)
        ||gate=="collection"&&name is "offset:HudSlotDataSubsystem:slotsHead" or "offset:HudSlotDataSubsystem:slotsCount" or "offset:HudSlotDataSubsystem:page";
    private static bool FieldGateImplemented(string field,string gate)
    {
        if(gate is "source" or "rtti" or "ownership" or "closure" or "value")return true;
        if(gate=="hotbar_layout")return field is "hotbarPage" or "hotbarFocus" or "hotbarSlots" or "atsEnabled";
        if(gate is "unique_typed_marker" or "ats_off_state_only")return field=="atsEnabled";
        if(gate is "actor_identity" or "collection_complete" or "component_unique" or "component_owner" or "direct_action" or "action_hierarchy" or "per_item_ab" or "publication_source" or "shop_layout")
            return field=="playerShopStates";
        // Natural-operation semantic_change is never inferred from structural reads.
        return false;
    }


    // A shared profile may retain optional Unknown RTTI for another capability.
    // Validate every reference and gate, but never manufacture an address or
    // block independent located fields that do not require that optional type.
    private static bool OptionalTypeHasUnknown(LocationProfile template,string[] references,IReadOnlySet<string> requiredSymbols)
    {
        bool unknown=false;
        foreach(string name in references)
        {
            if(!template.Symbols.TryGetValue(name,out var symbol)||symbol.Kind!=LocationKind.ModuleRva)
                throw new InvalidDataException("模板型別引用缺漏或不是模組 RVA："+name);
            if(symbol.Status=="located"){_=ModuleCandidate(template,name);continue;}
            if(symbol.Status!="unknown"||symbol.Value is not null||symbol.RequiredGates.Any(g=>!ModuleGateImplemented(name,g))||requiredSymbols.Contains(name))
                throw new InvalidDataException("模板必要型別未知或驗證契約不符："+name);
            unknown=true;
        }
        return unknown;
    }
    private static bool ModuleGateImplemented(string name,string gate)
        =>gate is "source" or "range" or "anchor" or "rtti" or "closure"
        ||gate=="primary_col"&&name.StartsWith("rtti:",StringComparison.Ordinal)&&
            (name.EndsWith(":descriptor",StringComparison.Ordinal)||name.EndsWith(":col",StringComparison.Ordinal)||name.EndsWith(":vtable",StringComparison.Ordinal));

    private static uint ModuleCandidate(LocationProfile template, string name)
    {
        if (!template.Symbols.TryGetValue(name, out var symbol) || symbol.Status != "located"
            || symbol.Kind != LocationKind.ModuleRva || symbol.Value is null
            || symbol.Value.Value<4096||symbol.Value.Value>=template.Identity.ImageSize)
            throw new InvalidDataException("模板必要符號尚未定位，或不是本版支援的模組 RVA：" + name);
        if (symbol.RequiredGates.Any(g => !ModuleGateImplemented(name,g)))
            throw new InvalidDataException("模板符號要求尚未實作的驗證：" + name);
        return checked((uint)symbol.Value.Value);
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 || file.Length > limit) throw new InvalidDataException("模板檔案為空或超界。");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 });
        var stack = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.StartArray) stack.Push(null);
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) stack.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && (stack.Count == 0 || stack.Peek() is null
                || !stack.Peek()!.Add(reader.GetString()!))) throw new InvalidDataException("模板索引包含重複 key。");
        }
    }
}
