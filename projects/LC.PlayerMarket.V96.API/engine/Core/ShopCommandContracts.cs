using System.Text.Json;

namespace MemoryHub;

public static class ShopCommands
{
    public const int Schema = 1;
    public const string GetState = "GetState";
    public const string OpenShop = "OpenShop";
    public const string ScanVisible = "ScanVisible";
    public const string ContinueSavedScan = "ContinueSavedScan";
    public const string GetSearchedOwners = "GetSearchedOwners";
    public const string ClearSearchedOwners = "ClearSearchedOwners";
    public const string ResetSearchProgress = "ResetSearchProgress";
    public const string CancelScan = "CancelScan";
    public const string PauseScan = "PauseScan";
    public const string ResumeScan = "ResumeScan";
    public const string RefreshTargets = "RefreshTargets";
    public const string ReadNavigation = "ReadNavigation";
    public const string CheckNativeReadiness = "CheckNativeReadiness";
    public const string PrepareNativeReadiness = "PrepareNativeReadiness";
    public static string PipeName => HubProtocol.PipeName + ".shops";
}

public sealed record ShopPoint(int X, int Y)
{
    public long GridDistance(ShopPoint other) => Math.Max(Math.Abs((long)X-other.X),Math.Abs((long)Y-other.Y));
}

public sealed record ShopCommandRequest
{
    public int Schema { get; init; } = ShopCommands.Schema;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Command { get; init; } = ShopCommands.GetState;
    /// <summary>Submit using local source/transport prerequisites without waiting for game-state confirmation.</summary>
    public bool ForceDispatch { get; init; }
    /// <summary>Keep one owned readonly readiness probe, including scan preparation, until completion or explicit cancellation; does not grant dispatch.</summary>
    public bool WaitForNativeReadinessCompletion { get; init; }
    public string ExpectedSourceSessionKey { get; init; } = "";
    public ulong? OwnerId { get; init; }
    public string? ViewerDirection { get; init; }
    public string? ExpectedOwnerIdentity { get; init; }
    public string? ListingRevision { get; init; }
    public string? TargetSnapshotId { get; init; }
    public string? ExpectedScanJobId { get; init; }
    public string? ExpectedScanPlanId { get; init; }
    public string? ExpectedHistoryRevision { get; init; }
    public ulong? HistoryAfterOwnerId { get; init; }
    public int HistoryLimit { get; init; } = 100;
}

/// <summary>Explicit observation, retained until the next manual refresh. Not a live actor lease.</summary>
public sealed record ShopTargetSnapshot
{
    public string SnapshotId { get; init; } = "";
    public string SourceSessionKey { get; init; } = "";
    public DateTimeOffset RefreshedAt { get; init; }
    public ulong SelfId { get; init; }
    public ShopPoint? SelfPosition { get; init; }
    public MarketOwner[] Owners { get; init; } = [];
    public int ShopActionObservedCount { get; init; }
    public int KnownOtherActionObservedCount { get; init; }
    public int UnknownShopStateCount { get; init; }
    public bool ScreenCoverageVerified { get; init; }
    /// <summary>Only an explicit successful refresh in the current broker lifetime grants dispatch.</summary>
    public bool CanDispatch { get; init; }
    public string DispatchBlockReason { get; init; } = "尚未完成本次服務的周遭刷新；保存資料僅供查看。";
    public string Detail { get; init; } = "";
}

public sealed record MarketOwner
{
    public ulong Id { get; init; }
    public string LocalIdentity { get; init; } = "";
    public string Name { get; init; } = "";
    public ShopPoint? Position { get; init; }
    public string? MapName { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool? InViewport { get; init; }
    public string VisibilityEvidence { get; init; } = "Unknown";
    public string? ActionType { get; init; }
    public string ShopState { get; init; } = "Unknown";
    public DateTimeOffset? ShopStateSampledAt { get; init; }
    public string ShopStateEvidence { get; init; } = "";
    public string ShopStateDetail { get; init; } = "尚未讀取擺攤狀態。";
}

/// <summary>
/// A new self/selected-ID rectangle observation joined to an explicitly saved
/// target. SavedOwnerPosition is never relabelled as the owner's live position.
/// Rectangle lookup cannot establish a current actor identity or visibility.
/// </summary>
public sealed record ShopNavigationSnapshot
{
    public string SourceSessionKey { get; init; } = "";
    public string TargetSnapshotId { get; init; } = "";
    public ulong OwnerId { get; init; }
    public string OwnerIdentity { get; init; } = "";
    public ulong SelfId { get; init; }
    public DateTimeOffset SampledAt { get; init; }
    public DateTimeOffset SelfPositionSampledAt { get; init; }
    public DateTimeOffset SavedTargetAt { get; init; }
    public ShopPoint? SelfPosition { get; init; }
    public ShopPoint? SavedOwnerPosition { get; init; }
    public string? MapName { get; init; }
    public string State { get; init; } = "ProjectionUnavailable";
    public string Detail { get; init; } = "";
    public JsonElement? RawViewport { get; init; }
    public bool CurrentActorIdentityVerified { get; init; }
    public bool PhysicalPixelMappingVerified { get; init; }
}

public sealed record MarketListing
{
    public string Revision { get; init; } = "";
    public string SourceSessionKey { get; init; } = "";
    public string RequestId { get; init; } = "";
    public MarketOwner Owner { get; init; } = new();
    public string ViewerDirection { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? RequestedAt { get; init; }
    public double? PageDurationMs { get; init; }
    public double? SourceReadDurationMs { get; init; }
    public double? NativeDurationMs { get; init; }
    public double? ResponseReadDurationMs { get; init; }
    public int ResponseReadAttempts { get; init; }
    public ShopPoint? SelfPositionAtRequest { get; init; }
    public long? DistanceAtRequest { get; init; }
    public string State { get; init; } = "Unknown";
    public string Detail { get; init; } = "";
    public bool EmptyPageObserved { get; init; }
    public ShopEmptyPageObservation? EmptyPageObservation { get; init; }
    public bool OwnerAndResponseMatched { get; init; }
    public bool PageEmpty { get; init; }
    public bool Complete { get; init; }
    public PersonalShopRow[] Rows { get; init; } = [];
}

public sealed record MarketScanState
{
    public bool PreflightNotEnteredConfirmed { get; init; }
    public SourceIdentity? PreflightNoEntrySource { get; init; }
    public int SkippedHistoryPages { get; init; }
    public string? JobId { get; init; }
    public string? SourceSessionKey { get; init; }
    public string? TargetSnapshotId { get; init; }
    public bool Running { get; init; }
    public bool PauseRequested { get; init; }
    public bool Paused { get; init; }
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int EmptyObservedPages { get; init; }
    /// <summary>Verified no-entry receipts; processed pages that were not queried and were not retried.</summary>
    public int NotEnteredPages { get; init; }
    public int TotalShops { get; init; }
    public int ShopActionObservedCount { get; init; }
    public int KnownOtherActionObservedCount { get; init; }
    public int UnknownShopStateCount { get; init; }
    public int CompletedShops { get; init; }
    public int SkippedPages { get; init; }
    public string? StopReason { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureScope { get; init; }
    public string? FailurePhase { get; init; }
    public string? FailureRva { get; init; }
    public string? FailureDetail { get; init; }
    public double? LastPageDurationMs { get; init; }
    public double? AveragePageDurationMs { get; init; }
    public double? AverageShopDurationMs { get; init; }
    public ulong? CurrentOwnerId { get; init; }
    public bool ScreenCoverageVerified { get; init; }
    public string Detail { get; init; } = "尚未搜尋商店";
}

public sealed record ShopCommandResponse
{
    public bool PreflightNotEnteredConfirmed { get; init; }
    public SourceIdentity? PreflightNoEntrySource { get; init; }
    public int Schema { get; init; } = ShopCommands.Schema;
    public string RequestId { get; init; } = "";
    public string State { get; init; } = "Unknown";
    public string Detail { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public double? RequestDurationMs { get; init; }
    public string? CurrentSourceSessionKey { get; init; }
    public ShopNativeReadiness? NativeReadiness { get; init; }
    public bool NativeEntered { get; init; }
    public bool NativeReturned { get; init; }
    public bool ResponseVerified { get; init; }
    public bool EmptyPageObserved { get; init; }
    public ShopEmptyPageObservation? EmptyPageObservation { get; init; }
    public bool SessionBlocked { get; init; }
    /// <summary>No scan worker or native gate owner remains. SessionBlocked must also be checked before moving.</summary>
    public bool WorkSettled { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureScope { get; init; }
    public string? FailurePhase { get; init; }
    public string? FailureRva { get; init; }
    public ShopTargetSnapshot? TargetSnapshot { get; init; }
    public ShopNavigationSnapshot? Navigation { get; init; }
    public MarketScanState Scan { get; init; } = new();
    public ShopSavedScanProgress? SavedScan { get; init; }
    public ShopSearchHistorySummary? SearchHistory { get; init; }
    public ShopSearchedOwnerRecord[] SearchedOwners { get; init; } = [];
    public ulong? NextHistoryOwnerId { get; init; }
    public bool HistoryTruncated { get; init; }
    public MarketListing[] Listings { get; init; } = [];
    public bool ListingsTruncated { get; init; }
}
