namespace MemoryHub;

/// <summary>Local numeric owner-ID/page history; this is not server-wide identity or dispatch permission.</summary>
public sealed record ShopSearchHistorySummary
{
    public string Revision { get; init; } = "";
    public string Epoch { get; init; } = "";
    public int OwnerCount { get; init; }
    public int SearchedPages { get; init; }
    public int ConfirmedPages { get; init; }
    public int UncertainPages { get; init; }
    public string? ReadError { get; init; }
    public bool CanClear { get; init; }
    public string ClearBlockReason { get; init; } = "尚未取得後端清除資格。";
    public DateTimeOffset? ClearedAt { get; init; }
}

public sealed record ShopSearchedOwnerRecord
{
    public ulong OwnerId { get; init; }
    public string OwnerName { get; init; } = "";
    public ShopPoint? Position { get; init; }
    public string? MapName { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset LastSearchedAt { get; init; }
    public string SourceSessionKey { get; init; } = "";
    public string BuyState { get; init; } = "NotSent";
    public string SellState { get; init; } = "NotSent";
}
