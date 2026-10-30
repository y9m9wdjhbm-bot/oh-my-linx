namespace MemoryHub.Service;

internal static class ShopSingleReadPagePolicy
{
    internal static bool ProductsMatch(ShopCommandRequest request, SourceIdentity expected,
        PersonalShopListing? after, DateTimeOffset requestedAt, DateTimeOffset now) =>
        after is not null && request.OwnerId is > 0 and <= int.MaxValue
        && request.ExpectedSourceSessionKey == expected.SessionKey && after.SourceSessionKey == expected.SessionKey
        && ShopRuntimeContext.Fresh(after.SampledAt, now) && after.SampledAt >= requestedAt
        && after.OwnerIdRaw == request.OwnerId && after.PageMode == request.ViewerDirection
        && after.PageModeState == "MatchedExclusiveBankAndRows" && after.Complete
        && after.IncompleteReasons is { Length: 0 } && after.Rows is not null && after.Rows.Any(r => r is not null && r.IncludedInListing);

    // One successful post-call read with zero displayed item rows. The request's
    // owner/direction remain metadata; raw observed owner/direction are retained
    // separately and are not asserted to match an empty page.
    internal static ShopEmptyPageObservation? TryObserveEmpty(ShopCommandRequest request, SourceIdentity expected,
        PersonalShopListing baseline, PersonalShopListing? after, ShopNativeOutcome native,
        DateTimeOffset requestedAt, DateTimeOffset now)
    {
        if (after is null || !native.Entered || !native.Returned || !native.SafeToSettle || !native.SourceMatched
            || native.NoEntryConfirmed || native.Failure is not null || request.OwnerId is null
            || request.ExpectedSourceSessionKey != expected.SessionKey || baseline.SourceSessionKey != expected.SessionKey
            || after.SourceSessionKey != expected.SessionKey || !ShopRuntimeContext.Fresh(after.SampledAt, now)
            || after.SampledAt < requestedAt || after.SampledAt <= baseline.SampledAt
            || after.LayoutState is not ("Present" or "Absent") || after.IncompleteReasons is not { Length: 0 }
            || after.Rows is null || after.Rows.Any(row => row is null || row.IncludedInListing)
            || after.LayoutState == "Present" && !after.Complete) return null;
        var evidence = new ShopEmptyPageObservation { Schema = 2, ObservationMode = "SingleReadNoItems", ReadSucceeded = true,
            IncludedItems = 0, ObservedOwnerIdRaw = after.OwnerIdRaw, ObservedPageDirection = after.PageMode,
            RequestId = request.RequestId, SourceSessionKey = expected.SessionKey, OwnerId = request.OwnerId.Value,
            RequestedDirection = request.ViewerDirection ?? "", RequestedAt = requestedAt, BaselineSampledAt = baseline.SampledAt,
            SampledAt = after.SampledAt, ValidatedAt = now, LayoutState = after.LayoutState, BankState = after.BankState,
            BankFingerprint = after.BankFingerprint ?? "", Complete = after.Complete, NativeLifecycleSettled = true };
        return ShopEmptyPageRules.IsEvidenceValid(evidence) ? evidence : null;
    }
}
