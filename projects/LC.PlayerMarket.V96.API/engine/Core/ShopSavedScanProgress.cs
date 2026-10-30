namespace MemoryHub;

/// <summary>Historical per-page progress. This does not grant live dispatch permission.</summary>
public sealed record ShopSavedScanProgress
{
    public string PlanId { get; init; } = "";
    public string SourceSessionKey { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int TotalPages { get; init; }
    public int SearchedPages { get; init; }
    public int ConfirmedPages { get; init; }
    public int RemainingPages { get; init; }
    public int AvailablePages { get; init; }
    public int DeferredPages { get; init; }
    public int HistorySkippedPages { get; init; }
    public int UncertainPages { get; init; }
    public bool CanContinue { get; init; }
    public string ContinueBlockCode { get; init; } = "Unknown";
    public string ContinueBlockReason { get; init; } = "尚未確認本次手動刷新及來源。";
    public ShopOwnerSearchProgress[] Owners { get; init; } = [];
}

public sealed record ShopOwnerSearchProgress
{
    public ulong OwnerId { get; init; }
    public string OwnerName { get; init; } = "";
    public string BuyState { get; init; } = "NotSent";
    public string SellState { get; init; } = "NotSent";
    public string BuyDetail { get; init; } = "尚未送出";
    public string SellDetail { get; init; } = "尚未送出";
    public DateTimeOffset UpdatedAt { get; init; }
}
