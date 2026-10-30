using System.Text.Json;

namespace MemoryHub.Service;

/// <summary>Keep complete identities and state; only optional summaries may shrink.</summary>
internal static class ShopResponseBudget
{
    internal const int RuntimeReservedBytes = 4096;
    internal const string FullDetailNotice = "完整原因見進度紀錄";
    internal static int MeasuredBytes(ShopCommandResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response with { RequestDurationMs = double.MaxValue }, HubProtocol.Json).Length;

    internal static ShopCommandResponse Fit(ShopCommandResponse response, int reservedBytes = RuntimeReservedBytes, MarketListing? requiredListing = null)
    {
        int limit = HubProtocol.MaxMessageBytes - reservedBytes;
        var result = response;
        if (requiredListing is not null && (requiredListing.RequestId != response.RequestId
            || response.Listings.Count(value => value.RequestId == requiredListing.RequestId) != 1
            || !response.Listings.Any(value => ReferenceEquals(value, requiredListing))))
            throw new InvalidDataException("PAGE_RECEIPT_LISTING_IDENTITY_MISMATCH");
        while (MeasuredBytes(result) > limit && result.Listings.Length > (requiredListing is null ? 0 : 1))
        {
            int remove = Array.FindLastIndex(result.Listings, value => requiredListing is null || !ReferenceEquals(value, requiredListing));
            if (remove < 0) break;
            result = result with { Listings = result.Listings.Where((_, index) => index != remove).ToArray(), ListingsTruncated = true };
        }
        if (MeasuredBytes(result) <= limit) return result;
        if (result.SavedScan is { } progress)
        {
            foreach (int keep in new[] { 128, 32, 0 })
            {
                result = result with { SavedScan = progress with { Owners = progress.Owners.Select(owner => owner with
                    { BuyDetail = Shorten(owner.BuyDetail, keep), SellDetail = Shorten(owner.SellDetail, keep) }).ToArray() } };
                if (MeasuredBytes(result) <= limit) return result;
            }
        }
        throw new ShopRequestFailure(ShopFailure.Global("TargetSnapshotTooLarge",
            "完整名單及搜尋進度超過既有回覆位元組界線；未截斷店主或授權新的名單／搜尋計畫。"));
    }

    private static string Shorten(string value, int keep) => value.Length <= keep ? value
        : (keep == 0 ? "" : value[..keep] + "…") + FullDetailNotice;

    // Conservative future public shape: all owners, longest current status, largest count fields.
    // This catches duplicated names in targets + progress before installing a snapshot or plan.
    internal static ShopSavedScanProgress FutureProgress(string sourceSessionKey, MarketOwner[] owners) => new()
    {
        PlanId = new string('f', 32), SourceSessionKey = sourceSessionKey,
        CreatedAt = DateTimeOffset.MaxValue, UpdatedAt = DateTimeOffset.MaxValue,
        TotalPages = int.MaxValue, SearchedPages = int.MaxValue, ConfirmedPages = int.MaxValue,
        RemainingPages = int.MaxValue, AvailablePages = int.MaxValue, DeferredPages = int.MaxValue,
        HistorySkippedPages = int.MaxValue, UncertainPages = int.MaxValue, CanContinue = false,
        ContinueBlockCode = "ProgressUnavailable", ContinueBlockReason = FullDetailNotice,
        Owners = owners.Select(owner => new ShopOwnerSearchProgress { OwnerId = owner.Id, OwnerName = owner.Name,
            BuyState = "HistorySkipped", SellState = "HistorySkipped", BuyDetail = FullDetailNotice,
            SellDetail = FullDetailNotice, UpdatedAt = DateTimeOffset.MaxValue }).ToArray()
    };
}
