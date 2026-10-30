using MemoryHub;

namespace LcPlayerMarket;

/// <summary>An explicitly refreshed service sample. Age and actor arrivals/departures never replace it.</summary>
internal sealed class ManualNearbyContext
{
    private readonly Dictionary<ulong, MarketOwner> owners;
    public string SnapshotId { get; }
    public SourceIdentity Source { get; }
    public DateTimeOffset RefreshedAt { get; }
    public ShopPoint? SelfPosition { get; }
    public int Count => owners.Count;
    public ShopEligibilityCounts ShopCounts { get; }
    public bool Invalidated { get; private set; }
    public string InvalidReason { get; private set; } = "";
    public bool CanDispatch { get; private set; }
    public string DispatchBlockReason { get; private set; } = "請先在本視窗成功手動刷新周遭，再開始搜尋或開啟商店。";

    public ManualNearbyContext(string snapshotId, SourceIdentity source, DateTimeOffset refreshedAt,
        ShopPoint? selfPosition, IEnumerable<MarketOwner> candidates, bool canDispatch = false)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Length > 256 || string.IsNullOrWhiteSpace(source.SessionKey)
            || refreshedAt == default) throw new InvalidDataException("手動周遭快照缺少識別、來源或刷新時間。");
        var values = candidates.Take(ActorCollectionContract.MaximumActors + 1).ToArray();
        if (values.Length > ActorCollectionContract.MaximumActors || values.Any(p => p is null || p.Id is 0 or > int.MaxValue
            || string.IsNullOrWhiteSpace(p.LocalIdentity) || p.LocalIdentity.Length > 256)
            || values.Select(p => p.Id).Distinct().Count() != values.Length)
            throw new InvalidDataException("手動周遭快照超過範圍或含重複／無效店主。");
        SnapshotId = snapshotId; Source = source; RefreshedAt = refreshedAt; SelfPosition = selfPosition;
        owners = values.ToDictionary(p => p.Id, p => p with { });
        ShopCounts = ShopEligibilityPolicy.Count(owners.Values);
        CanDispatch = canDispatch;
        if (canDispatch) DispatchBlockReason = "";
    }

    // Restore the service's last explicit refresh after the market window was
    // reopened. This never renews the sample or replaces an adopted context.
    public static ManualNearbyContext? RestoreSaved(ManualNearbyContext? existing,
        SourceIdentity? currentSource, ShopCommandResponse response)
    {
        if (existing is not null || currentSource is null || response.TargetSnapshot is not { } saved
            || response.CurrentSourceSessionKey != currentSource.SessionKey
            || saved.SourceSessionKey != currentSource.SessionKey || saved.Owners is null
            || saved.SelfId is 0 or > int.MaxValue) return null;
        try { return new(saved.SnapshotId, currentSource, saved.RefreshedAt, saved.SelfPosition, saved.Owners); }
        catch (InvalidDataException) { return null; }
    }

    // Only the explicitly requested refresh can grant a new dispatch context.
    // Restored history and background polling can never grant this permission.
    public static bool TryAdoptRefresh(ManualNearbyContext? existing, SourceIdentity requestedSource,
        SourceIdentity? currentSource, string requestId, ShopCommandResponse response,
        out ManualNearbyContext? adopted, out string reason)
    {
        adopted = null;
        reason = "周遭刷新未完成；保存資料與導航保留，搜尋及開啟商店已停用，請重新手動刷新。";
        var target = response.TargetSnapshot;
        if (string.IsNullOrWhiteSpace(requestId) || response.RequestId != requestId
            || response.State != "TargetsRefreshed" || !string.IsNullOrWhiteSpace(response.FailureCode)
            || target is null || !target.CanDispatch || target.SnapshotId == existing?.SnapshotId
            || target.SourceSessionKey != requestedSource.SessionKey
            || response.CurrentSourceSessionKey != requestedSource.SessionKey || currentSource != requestedSource
            || target.Owners is null || target.SelfId is 0 or > int.MaxValue)
            return false;
        try { adopted = new(target.SnapshotId, requestedSource, target.RefreshedAt, target.SelfPosition, target.Owners, true); }
        catch (InvalidDataException) { return false; }
        reason = "";
        return true;
    }

    public void BlockDispatch(string reason)
    {
        CanDispatch = false;
        DispatchBlockReason = string.IsNullOrWhiteSpace(reason)
            ? "本次周遭名單已停止送出查詢；請重新手動刷新。" : reason;
    }

    public void ObserveBroker(ShopCommandResponse response)
    {
        ObserveSession(response.CurrentSourceSessionKey);
        if (Invalidated) return;
        if (response.TargetSnapshot is not { } target || target.SnapshotId != SnapshotId
            || target.SourceSessionKey != Source.SessionKey)
            BlockDispatch("服務尚未確認這份周遭名單可供查詢；保存資料與導航保留，請重新手動刷新。");
        else if (!target.CanDispatch)
            BlockDispatch(string.IsNullOrWhiteSpace(target.DispatchBlockReason)
                ? "服務已停止此名單的搜尋與開店；請重新手動刷新。" : target.DispatchBlockReason);
        // A true flag on GetState is not a successful refresh for this window.
    }

    public void ObserveSource(SourceIdentity? current)
    {
        // A transient missing background sample neither deletes nor renews this
        // manual snapshot. A confirmed new source permanently invalidates it.
        if (current is null || current == Source || Invalidated) return;
        Invalidate("LC 來源或場景已變更；保留舊清單，請手動刷新周遭後再操作。");
    }
    public void ObserveSession(string? session)
    {
        if (!string.IsNullOrEmpty(session) && session != Source.SessionKey)
            Invalidate("LC 來源已變更；請手動刷新周遭，不沿用舊 ID。");
    }
    public void Invalidate(string reason) { Invalidated = true; InvalidReason = reason; BlockDispatch(reason); }
    public LivePlayer? Match(MarketListing listing)
    {
        if (Invalidated || listing.SourceSessionKey != Source.SessionKey || listing.Owner is null
            || !owners.TryGetValue(listing.Owner.Id, out var owner) || owner.LocalIdentity != listing.Owner.LocalIdentity) return null;
        return new(owner.Id, owner.LocalIdentity, owner.Name, owner.Position, owner.ObservedAt);
    }
    public bool CanUse(MarketListing listing) => LocalStore.Valid(listing) && listing.OwnerAndResponseMatched && Match(listing) is not null;

    public bool CanOpenShop(MarketListing listing, out string reason)
    {
        if (!CanDispatch) { reason = DispatchBlockReason; return false; }
        return CanScanSavedOwner(listing, out reason);
    }

    // Forced submission bypasses pending-operation confirmation only. The
    // saved owner must still contain complete evidence of a shop action.
    public bool CanScanSavedOwner(MarketListing listing, out string reason)
    {
        if (!CanUse(listing))
        { reason = "這筆歷史商品的店主未對應已保存的周遭快照；請手動刷新後再比對。"; return false; }
        var owner = owners[listing.Owner.Id];
        if (!ShopEligibilityPolicy.CanScan(owner))
        { reason = ShopEligibilityPolicy.RejectionDetail(owner); return false; }
        reason = ""; return true;
    }
}

