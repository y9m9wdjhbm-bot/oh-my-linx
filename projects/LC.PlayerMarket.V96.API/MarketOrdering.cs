using MemoryHub;

namespace LcPlayerMarket;

public enum MarketSortField { None, Item, Price, Quantity, Owner, Direction, Position, Distance, CapturedAt, ItemKind }
public sealed record MarketSortTerm(MarketSortField Field, bool Descending);

/// <summary>Production three-level ordering; every nullable numeric key keeps unknown values last.</summary>
public static class MarketOrdering
{
    public static MarketSortTerm[] Normalize(IEnumerable<MarketSortTerm> terms) => terms
        .Where(t => t.Field != MarketSortField.None && Enum.IsDefined(t.Field)).DistinctBy(t => t.Field).Take(3).ToArray();

    /// <summary>A new column replaces only the primary key. An existing lower key swaps with it.</summary>
    public static MarketSortTerm[] PromotePrimary(IEnumerable<MarketSortTerm> terms, MarketSortField field)
    {
        var result = Normalize(terms).ToList();
        if (field == MarketSortField.None || !Enum.IsDefined(field)) return result.ToArray();
        if (result.Count == 0) return [new(field, false)];
        int index = result.FindIndex(t => t.Field == field);
        if (index == 0) result[0] = result[0] with { Descending = !result[0].Descending };
        else if (index > 0) (result[0], result[index]) = (result[index], result[0]);
        else result[0] = new(field, false);
        return result.ToArray();
    }

    /// <summary>Remove only a final numeric suffix independently matched to the confirmed quantity.</summary>
    public static string ItemNameKey(string displayName, ulong? quantity)
    {
        if (quantity is null || string.IsNullOrEmpty(displayName)) return displayName;
        string end = displayName.TrimEnd();
        if (end.Length < 4 || end[^1] is not (')' or '）')) return displayName;
        char opening = end[^1] == ')' ? '(' : '（';
        int at = end.LastIndexOf(opening);
        if (at <= 0) return displayName;
        string count = end[(at + 1)..^1].Trim();
        // Same bounded decimal/grouping rules as the service price parser; no culture-dependent thousands guessing.
        if (PersonalShopTextRules.ParsePrice(count) is not { } value || value != quantity.Value) return displayName;
        string name = end[..at].TrimEnd();
        return name.Length > 0 ? name : displayName;
    }

    internal static MarketRowView[] Apply(IEnumerable<MarketRowView> values, IEnumerable<MarketSortTerm> terms)
    {
        var selected = Normalize(terms);
        return values.OrderBy(row => row, Comparer<MarketRowView>.Create((a, b) => CompareRows(a, b, selected))).ToArray();
    }
    private static int CompareRows(MarketRowView a, MarketRowView b, IReadOnlyList<MarketSortTerm> terms)
    {
        foreach (var term in terms)
        {
            int compared = term.Field switch
            {
                MarketSortField.Item => Text(a.SortItemName, b.SortItemName, term.Descending),
                MarketSortField.Price => NullableValue(a.NumericPrice, b.NumericPrice, term.Descending),
                MarketSortField.Quantity => NullableValue(a.NumericQuantity, b.NumericQuantity, term.Descending),
                MarketSortField.ItemKind => MarketItemStatePresentation.CompareEffective(a.Row, b.Row, term.Descending),
                MarketSortField.Owner => Text(a.Owner, b.Owner, term.Descending),
                MarketSortField.Direction => Text(a.Direction, b.Direction, term.Descending),
                MarketSortField.Position => Position(a.SortPosition, b.SortPosition, term.Descending),
                MarketSortField.Distance => NullableValue(a.NumericDistance, b.NumericDistance, term.Descending),
                MarketSortField.CapturedAt => term.Descending ? b.CapturedAt.CompareTo(a.CapturedAt) : a.CapturedAt.CompareTo(b.CapturedAt),
                _ => 0
            };
            if (compared != 0) return compared;
        }
        int time = b.CapturedAt.CompareTo(a.CapturedAt);
        return time != 0 ? time : StringComparer.Ordinal.Compare(a.Key, b.Key);
    }
    private static int NullableValue<T>(T? a, T? b, bool descending) where T : struct, IComparable<T>
    {
        if (!a.HasValue) return b.HasValue ? 1 : 0;
        if (!b.HasValue) return -1;
        return descending ? b.Value.CompareTo(a.Value) : a.Value.CompareTo(b.Value);
    }
    private static int Position(ShopPoint? a, ShopPoint? b, bool descending)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        int x = descending ? b.X.CompareTo(a.X) : a.X.CompareTo(b.X);
        return x != 0 ? x : descending ? b.Y.CompareTo(a.Y) : a.Y.CompareTo(b.Y);
    }
    private static int Text(string a, string b, bool descending) => descending
        ? StringComparer.CurrentCultureIgnoreCase.Compare(b, a) : StringComparer.CurrentCultureIgnoreCase.Compare(a, b);

    public static IEnumerable<T> ByPrice<T>(IEnumerable<T> values, Func<T, ulong?> price, Func<T, DateTimeOffset> capturedAt, bool descending)
    {
        ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(price); ArgumentNullException.ThrowIfNull(capturedAt);
        return values.OrderBy(v => !price(v).HasValue)
            .ThenBy(v => descending ? ulong.MaxValue - (price(v) ?? 0) : price(v) ?? 0)
            .ThenByDescending(capturedAt);
    }
}
