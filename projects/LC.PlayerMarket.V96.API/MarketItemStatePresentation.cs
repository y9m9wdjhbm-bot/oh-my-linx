using System.Globalization;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Uses only the version-scoped corroboration marker or the explicit verified-identification user rule.</summary>
internal static class MarketItemStatePresentation
{
    internal const string CorroboratedState = "CurrentItemEntityUserCorroborated";
    internal const string CurrentAccessorState = "CurrentBuildItemKindAccessorVerified";
    internal const string UnidentifiedRule = "UserRuleUnidentifiedAsNormal";
    internal const string ComparisonNote = "依有效類型比價；已驗證未鑑定依設定視為一般，原始值保留供核對。相同有效類型不保證隱藏屬性相同。";

    internal static string EffectiveKind(PersonalShopRow row) => Resolve(row).Kind;
    internal static string EffectiveKindReason(PersonalShopRow row) => Resolve(row).Reason;

    private static (string Kind, string Reason) Resolve(PersonalShopRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.ItemKindDetailFlag.HasValue || !row.ItemKindRaw.HasValue) return ("Unknown", "RawFieldsMissing");
        if (row.ItemKindDetailFlag is not (0 or 1) || row.ItemKindRaw is not (0 or 1 or 2)) return ("Unknown", "RawFieldsUnsupported");
        if (ClaimsBuildCertification(row))
        {
            if (!HasCompleteBuildCertification(row)) return ("Unknown", "BuildCertificateEvidenceUnverified");
            if (row.ItemKindDetailFlag == 0) return ("Normal", UnidentifiedRule);
            return (row.ItemKindRaw switch { 0 => "Blessed", 1 => "Normal", 2 => "Cursed", _ => "Unknown" }, "BuildCertifiedItemKind");
        }
        if (row.ItemIdentificationState == "Unidentified" && row.ItemIdentificationEvidence == "Verified")
            return ("Normal", UnidentifiedRule);
        if (row.ItemKindDetailFlag != 1) return ("Unknown", "DetailFlagSemanticUnverified");
        if (row.ItemKindState is not (CorroboratedState or CurrentAccessorState)) return ("Unknown", "ItemKindEvidenceUnverified");
        return (row.ItemKindRaw switch { 0 => "Blessed", 1 => "Normal", 2 => "Cursed", _ => "Unknown" }, row.ItemKindState);
    }

    private static bool ClaimsBuildCertification(PersonalShopRow row) => row.ItemStateEvidence?.Mode == "BuildCertified"
        || row.ItemKindState == "BuildCertifiedItemKind" || row.ItemIdentificationEvidence == "BuildCertified";
    private static bool HasCompleteBuildCertification(PersonalShopRow row)
    {
        if (!PersonalShopItemEvidenceRules.IsBuildCertified(row) || row.ItemIdentificationEvidence != "BuildCertified") return false;
        if (row.ItemKindDetailFlag == 0) return row.ItemIdentificationState == "Unidentified" && row.ItemKindState == "Unknown" && row.ItemKind is null;
        return row.ItemKindDetailFlag == 1 && row.ItemIdentificationState == "Identified" && row.ItemKindState == "BuildCertifiedItemKind"
            && row.ItemKind == (row.ItemKindRaw switch { 0 => "Blessed", 1 => "Normal", 2 => "Cursed", _ => null });
    }

    internal static string RawFilterKey(PersonalShopRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"flag={Number(row.ItemKindDetailFlag)};raw={Number(row.ItemKindRaw)}";
    }
    internal static string RawFilterLabel(PersonalShopRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"旗標={Number(row.ItemKindDetailFlag, "缺值")}／原始類型={Number(row.ItemKindRaw, "缺值")}";
    }
    internal static string FilterKey(PersonalShopRow row)
    {
        string kind = EffectiveKind(row);
        return kind == "Unknown" ? "kind=Unknown;" + RawFilterKey(row) : "kind=" + kind;
    }
    internal static string FilterLabel(PersonalShopRow row) => EffectiveKind(row) switch
    {
        "Blessed" => "受祝福", "Normal" => "一般", "Cursed" => "受詛咒",
        _ => "未知（" + RawFilterLabel(row) + "）"
    };
    internal static string Display(PersonalShopRow row)
    {
        var effective = Resolve(row);
        if (effective.Kind == "Unknown") return "未知（未鑑定未核實）";
        if (HasCompleteBuildCertification(row)) return effective.Reason == UnidentifiedRule
            ? "未知（未鑑定；版本認證）" : FilterLabel(row) + "（已鑑定；版本認證）";
        if (effective.Reason == UnidentifiedRule) return "未知（未鑑定）";
        return FilterLabel(row) + (row.ItemIdentificationState == "Identified" && row.ItemIdentificationEvidence == "Verified"
            ? "（已鑑定）" : "");
    }
    internal static string EvidenceTooltip(PersonalShopRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return "物品顯示=" + Display(row) + Environment.NewLine
            + "比價分類=" + EffectiveKind(row) + "；分類依據=" + EffectiveKindReason(row) + Environment.NewLine
            + RawFilterLabel(row) + "；祝咒證據=" + row.ItemKindState + Environment.NewLine
            + "鑑定狀態=" + row.ItemIdentificationState + "；鑑定證據=" + row.ItemIdentificationEvidence + Environment.NewLine
            + "證據來源=" + (row.ItemStateEvidence?.Mode ?? "未記錄") + "；來源版本=" + (row.ItemStateEvidence?.SourceBuildSha256 ?? "未記錄") + Environment.NewLine
            + "當前程式碼驗證=" + EvidenceBoolean(row.ItemStateEvidence?.CurrentRuntimeCodeVerified)
            + "；當前中繼資料驗證=" + EvidenceBoolean(row.ItemStateEvidence?.CurrentMetadataVerified) + Environment.NewLine
            + "認證 SHA256=" + (row.ItemStateEvidence?.CertificateSha256 ?? "未記錄")
            + (HasCompleteBuildCertification(row) ? Environment.NewLine + "版本認證依同版本靜態語意與本次中繼資料，不表示當前格式化程式碼可讀。" : "");
    }
    internal static string EvidenceBoolean(bool? value) => value is null ? "未知" : value.Value ? "true" : "false";
    internal static bool MatchesFilter(PersonalShopRow row, string? selection)
    {
        ArgumentNullException.ThrowIfNull(row);
        string value = selection?.Trim() ?? "";
        return value.Length == 0 || StringComparer.Ordinal.Equals(value, FilterKey(row))
            || StringComparer.Ordinal.Equals(value, FilterLabel(row));
    }
    internal static MarketRawStateFilter[] FilterOptions(IEnumerable<PersonalShopRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.DistinctBy(FilterKey).OrderBy(r => r, Comparer<PersonalShopRow>.Create((a, b) => CompareEffective(a, b, false)))
            .Select(r => new MarketRawStateFilter(FilterKey(r), FilterLabel(r))).ToArray();
    }
    internal static int CompareEffective(PersonalShopRow a, PersonalShopRow b, bool descending)
    {
        int? Rank(PersonalShopRow row) => EffectiveKind(row) switch { "Blessed" => 0, "Normal" => 1, "Cursed" => 2, _ => null };
        int? x = Rank(a), y = Rank(b);
        int kind = NullableNumber(x, y, descending);
        return kind != 0 ? kind : x.HasValue ? 0 : CompareRaw(a, b, descending);
    }
    internal static int CompareRaw(PersonalShopRow a, PersonalShopRow b, bool descending)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b);
        bool aComplete = a.ItemKindDetailFlag.HasValue && a.ItemKindRaw.HasValue;
        bool bComplete = b.ItemKindDetailFlag.HasValue && b.ItemKindRaw.HasValue;
        if (aComplete != bComplete) return aComplete ? -1 : 1;
        int flag = NullableNumber(a.ItemKindDetailFlag, b.ItemKindDetailFlag, descending);
        return flag != 0 ? flag : NullableNumber(a.ItemKindRaw, b.ItemKindRaw, descending);
    }
    /// <summary>Do not persist: missing or unknown observations are isolated by view reference, never a shared Unknown key.</summary>
    internal static MarketRawComparisonKey ComparisonKey(MarketRowView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var row = view.Row;
        string effectiveKind = EffectiveKind(row), direction = view.Listing.ViewerDirection;
        bool complete = direction is ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell
            && !string.IsNullOrWhiteSpace(row.Name) && !string.IsNullOrWhiteSpace(view.SortItemName)
            && !string.IsNullOrWhiteSpace(row.UnitRaw) && effectiveKind != "Unknown";
        return new(direction, view.SortItemName, row.UnitRaw, effectiveKind, complete ? null : view);
    }
    internal static MarketRawComparisonGroup[] GroupForComparison(IEnumerable<MarketRowView> rows, bool descendingPrice)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.GroupBy(ComparisonKey).Select(group => new MarketRawComparisonGroup(group.Key,
            MarketOrdering.ByPrice(group, r => r.NumericPrice, r => r.CapturedAt, descendingPrice).ToArray(),
            group.Key.IsIsolated ? "類型未知或資料缺值，獨立列；" + ComparisonNote : ComparisonNote)).ToArray();
    }
    private static string Number<T>(T? value, string missing = "missing") where T : struct, IFormattable
        => value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : missing;
    private static int NullableNumber<T>(T? a, T? b, bool descending) where T : struct, IComparable<T>
    {
        if (!a.HasValue) return b.HasValue ? 1 : 0;
        if (!b.HasValue) return -1;
        return descending ? b.Value.CompareTo(a.Value) : a.Value.CompareTo(b.Value);
    }
}

internal sealed record MarketRawStateFilter(string Key, string Label);
internal sealed record MarketRawComparisonKey(string Direction, string Name, string? Currency,
    string EffectiveKind, MarketRowView? IsolatedView)
{
    internal bool IsIsolated => IsolatedView is not null;
    // This groups price comparisons under the user rule; it does not certify hidden-property equivalence.
    internal bool SemanticsVerified => false;
}
internal sealed record MarketRawComparisonGroup(MarketRawComparisonKey Key, MarketRowView[] Rows, string Note);
