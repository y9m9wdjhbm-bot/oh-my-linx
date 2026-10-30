namespace MemoryHub;

/// <summary>Directions are always from the local viewer's perspective.</summary>
public static class ShopPageModes
{
    public const string ViewerBuy = "ViewerBuy";
    public const string ViewerSell = "ViewerSell";
}

public sealed record PersonalShopRow(
    int RowIndex, string State, bool IncludedInListing,
    string? Name, string? PriceRaw, ulong? Price, string? UnitRaw,
    ulong? Quantity, string QuantityState, string Detail)
{
    public int? ItemKindRaw { get; init; }
    public byte? ItemKindDetailFlag { get; init; }
    public string? ItemKind { get; init; }
    public string ItemKindState { get; init; } = "Unknown";
    public PersonalShopItemStateEvidence? ItemStateEvidence { get; init; }
    public string ItemIdentificationState { get; init; } = "Unknown";
    public string ItemIdentificationEvidence { get; init; } = "Unverified";
}

/// <summary>Rules backed by the retained V30 Buy/Sell text samples.</summary>
public static class PersonalShopTextRules
{
    public static bool IsTemplate(string name, string price) => name == "아이템 이름" && price == "2555";
    public static ulong? ParsePrice(string text)
    {
        if (text.Length == 0) return null;
        string[] groups = text.Split(',');
        if (groups.Any(group => group.Length == 0 || group.Any(c => c is < '0' or > '9')) ||
            groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))) return null;
        return ulong.TryParse(string.Concat(groups), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out ulong value) ? value : null;
    }
    public static PersonalShopRow Decode(int index, string name, string price, string unit)
    {
        bool empty = string.IsNullOrWhiteSpace(name) || IsTemplate(name, price);
        bool completeText = !string.IsNullOrWhiteSpace(price) && !string.IsNullOrWhiteSpace(unit);
        return new(index, empty ? "EmptyOrTemplateCandidate" : completeText ? "NamedListingCandidate" : "Partial",
            !empty && completeText, name, price, ParsePrice(price), unit, null, "Unverified",
            empty ? "空白或已知模板，排除商品；不推定整店為空" : "同列文字；括號數字及計價單位不當成庫存");
    }
}

/// <summary>
/// One closed UI text sample. OwnerIdRaw is the selected subsystem field, not
/// proof that the cached layout belongs to that owner or that a new reply arrived.
/// The broker must separately correlate request, owner and page generations.
/// </summary>
public sealed record PersonalShopListing
{
    public string SourceSessionKey { get; init; } = "";
    public DateTimeOffset SampledAt { get; init; }
    public ulong? OwnerIdRaw { get; init; }
    public string OwnerBindingState { get; init; } = "ContextOnly_UnboundToRows";
    public string? PageMode { get; init; }
    public string PageModeState { get; init; } = "Unknown";
    public string? BankFingerprint { get; init; }
    public string BankState { get; init; } = "Unknown";
    public PersonalShopBankSummary[] RawBankSummary { get; init; } = [];
    public PersonalShopSlotRow[] SlotRows { get; init; } = [];
    public string LayoutState { get; init; } = "Unknown";
    public bool Complete { get; init; }
    public bool PageEmpty { get; init; }
    public bool WholeStoreEmptyVerified { get; init; }
    public bool VisibilityVerified { get; init; }
    public bool DataMayBeCachedOrHidden { get; init; } = true;
    public int? ContainerRowCount { get; init; }
    public int InspectedRows { get; init; }
    public int ExcludedEmptyOrTemplateRows { get; init; }
    public PersonalShopRow[] Rows { get; init; } = [];
    public string[] IncompleteReasons { get; init; } = [];
    public string Evidence { get; init; } = "TypedUiTextScopeOnly";
}

public sealed record PersonalShopBankSummary(int BankIndex, string PageRole, int NativeMode,
    int OccupiedSlots, int TypedPayloadSlots, string RawSha256);

/// <summary>Data values only. Remote addresses are kept inside Reader, not exposed to clients.</summary>
public sealed record PersonalShopSlotRow
{
    public int BankIndex { get; init; }
    public int RowIndex { get; init; }
    public string State { get; init; } = "Unknown";
    public bool PairPresent { get; init; }
    public bool ControlVerified { get; init; }
    public bool PayloadVerified { get; init; }
    public ulong RawField10 { get; init; }
    public byte RawField18 { get; init; }
    public long RawPriceField20 { get; init; }
    public ulong RawQuantityField28 { get; init; }
    public ulong? PriceCandidate { get; init; }
    public ulong? QuantityCandidate { get; init; }
    public ulong? PayloadQuantityCandidate { get; init; }
    public bool QuantityMatchesPayload { get; init; }
    public string? RawDisplayTextCandidate { get; init; }
    public uint? EntityIdCandidate { get; init; }
    public uint? DefinitionIdCandidate { get; init; }
    public ulong? UidCandidate { get; init; }
    public int? ItemKindRaw { get; init; }
    public byte? ItemKindDetailFlag { get; init; }
    public string? ItemKind { get; init; }
    public string ItemKindState { get; init; } = "Unknown";
    public PersonalShopItemStateEvidence? ItemStateEvidence { get; init; }
    public string Detail { get; init; } = "";
}
