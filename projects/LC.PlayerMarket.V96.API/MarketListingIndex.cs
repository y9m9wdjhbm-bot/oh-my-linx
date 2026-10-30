using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record ListingUpdate(bool Changed, MarketListing? Previous);

/// <summary>One selected whole page per storefront/direction; immutable revision IDs remain action identities.</summary>
internal sealed class MarketListingIndex
{
    private readonly Dictionary<string, MarketListing> pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketListing> revisions = new(StringComparer.Ordinal);
    public int Count => pages.Count;
    public IEnumerable<MarketListing> Values => pages.Values;
    public bool TryGetValue(string revisionKey, out MarketListing listing) => revisions.TryGetValue(revisionKey, out listing!);

    // Display grouping only. No ID is synthesized or carried to a different LC source.
    // Server identity is not available in MarketOwner; differing/missing locations stay separate.
    internal static string PageKey(MarketListing value)
    {
        var owner = value.Owner;
        return !string.IsNullOrWhiteSpace(owner.Name) && !string.IsNullOrWhiteSpace(owner.MapName)
            && owner.Position is { X: > 0, Y: > 0 } at
            ? JsonSerializer.Serialize(new object[] { "named-location", owner.Name, owner.MapName, at.X, at.Y, value.ViewerDirection })
            : JsonSerializer.Serialize(new object[] { "local-source", value.SourceSessionKey, owner.Id, owner.LocalIdentity, value.ViewerDirection });
    }

    public ListingUpdate Upsert(MarketListing value)
    {
        if (!LocalStore.Valid(value)) return new(false, null);
        string pageKey = PageKey(value);
        pages.TryGetValue(pageKey, out var previous);
        if (previous is not null)
        {
            if (LocalStore.Key(previous) == LocalStore.Key(value)) return new(false, previous);
            bool previousVerified = previous.OwnerAndResponseMatched && previous.Complete;
            bool nextVerified = value.OwnerAndResponseMatched && value.Complete;
            if (previousVerified && !nextVerified) return new(false, previous);
            if (previousVerified == nextVerified)
            {
                int order = value.CapturedAt.CompareTo(previous.CapturedAt);
                if (order < 0 || order == 0 && StringComparer.Ordinal.Compare(LocalStore.Key(value), LocalStore.Key(previous)) <= 0)
                    return new(false, previous);
            }
            revisions.Remove(LocalStore.Key(previous));
        }
        pages[pageKey] = value;
        revisions[LocalStore.Key(value)] = value;
        return new(true, previous);
    }

    public void Clear() { pages.Clear(); revisions.Clear(); }
    public void Trim(int maximum)
    {
        if (pages.Count <= maximum) return;
        foreach (var value in pages.Values.OrderByDescending(p => p.CapturedAt).Skip(maximum).ToArray())
        { pages.Remove(PageKey(value)); revisions.Remove(LocalStore.Key(value)); }
    }
}
