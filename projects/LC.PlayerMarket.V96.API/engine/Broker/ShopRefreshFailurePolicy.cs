namespace MemoryHub.Service;

/// <summary>Only classifies a read-only target refresh. Never grants dispatch or restores a source.</summary>
internal static class ShopRefreshFailurePolicy
{
    internal const string ReadTimeout = "RefreshReadTimeout";
    internal const string SourceSampleGap = "RefreshSourceSampleGap";
    private static readonly string[] RequiredFields = ["selfId", "position", "nearbyPlayers", "playerShopStates"];

    internal static void RequireReadSource(ReadResult result, SourceIdentity expected, SourceIdentity? current, bool forceDispatch)
    {
        if (ShopSourceAvailability.Changed(current, expected) || ShopSourceAvailability.Changed(result.Source, expected))
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "手動刷新期間已確認 LC 來源不同；未替換既有名單。"));
        if (Classify(result, expected, current) is { } readFailure)
            throw new ShopRequestFailure(readFailure);
        if (!forceDispatch && current is null || result.Source is null)
            throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "手動刷新期間 LC 來源採樣暫缺；未替換既有名單。"));
    }

    internal static ShopFailure? Classify(ReadResult result, SourceIdentity expected, SourceIdentity? current)
    {
        // A known different source always wins over a time-limit or null observation.
        if (result.NoSourceRead || current is not null && current != expected
            || result.Source is not null && result.Source != expected) return null;
        if (result.Source is null && result.State == "Unknown" && result.ReadFailureCode == "TIME_LIMIT"
            && result.ReadFailureNativeError == 0 && RequiredFields.All(key =>
                result.Fields.TryGetValue(key, out var field) && field.State == "Unknown"
                && field.Value is null && field.SampledAt is null))
            return ShopFailure.Global(ReadTimeout,
                "本次唯讀周遭採樣已達讀取期限；舊名單維持撤銷送出資格。須重新核對完整原來源與站定位置後，才可依設定額度重新刷新。");
        if (current is null && result.Source == expected && result.State is "Reading" or "Unknown"
            && result.ReadFailureCode is null && result.ReadFailureNativeError is null)
            return ShopFailure.Global(SourceSampleGap,
                "本次唯讀採樣仍屬原完整來源，但背景來源樣本暫缺；舊名單維持撤銷送出資格。須重新核對原來源後刷新。");
        return null;
    }
}
