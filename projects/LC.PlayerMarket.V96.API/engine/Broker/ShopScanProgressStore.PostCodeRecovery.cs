namespace MemoryHub.Service;

internal sealed partial class ShopScanProgressStore
{
    internal bool RecoverPostCode(NativePostCodeRecoveryEvidence proof, string detail, bool requireScanAttempt)
    {
        lock (sync)
        {
            RequireHealthy();
            if (!requests.TryGetValue(proof.RequestId, out var page))
            {
                if (requireScanAttempt || knownIntents.ContainsKey(proof.RequestId)) throw Failure("原生恢復找不到目前計畫的原請求。");
                return false; // An accepted direct OpenShop has no scan-plan attempt.
            }
            if (plan is null || plan.Source != proof.Source || page.OwnerId != proof.OwnerId
                || page.OwnerIdentity != proof.OwnerIdentity || page.Direction != proof.Direction)
                throw Failure("原生恢復與原搜尋頁的來源／店主／方向不同。");
            ShopPostCodeRecoveryProof.ValidateEvidence(proof, plan.Source, proof.RequestId, page.OwnerId, page.OwnerIdentity, page.Direction);
            var state = pages[page];
            if (state.Settled)
            {
                var recorded = Directory.EnumerateFiles(EventsDirectory(plan.PlanId), "*.json")
                    .Select(file => Read<ScanProgressEvent>(file, 64 * 1024))
                    .Where(item => item.RequestId == proof.RequestId && item.Kind == "NativePostCodeRecovered").ToArray();
                if (recorded.Length != 1 || recorded[0].PostCodeRecovery != proof || !state.SettledReturned || state.State != "Sent")
                    throw Failure("原生恢復已結清狀態缺少完全相同的原恢復事件。");
                return true;
            }
            if (sequence >= MaximumEvents) throw Failure("搜尋進度事件已達上限；恢復證據未寫入。");
            var item = new ScanProgressEvent { Sequence = sequence + 1, PlanId = plan.PlanId, Kind = "NativePostCodeRecovered",
                RequestId = proof.RequestId, OwnerId = page.OwnerId, OwnerIdentity = page.OwnerIdentity, Direction = page.Direction,
                At = NextTimestamp(updatedAt), NativeEntered = true, NativeReturned = true, ResponseVerified = false,
                SessionBlocked = false, Detail = detail.Length > 16000 ? detail[..16000] : detail, PostCodeRecovery = proof };
            ValidateEvent(item);
            try
            {
                WriteNew(Path.Combine(EventsDirectory(plan.PlanId), EventFileName(item.Sequence)), item);
                Apply(item);
                if (unresolved.TryGetValue(proof.RequestId, out var intent)) SettleIndex(intent, true);
            }
            catch (Exception failure) { error = "原生恢復事件保存失敗；保留原請求與 pending：" + failure.Message; throw; }
            return true;
        }
    }
}

internal sealed partial class ShopSearchedOwnerStore
{
    internal void RecoverPostCode(NativePostCodeRecoveryEvidence proof) => Mutate(() =>
    {
        if (!attempts.TryGetValue(LiveKey(proof.RequestId), out var item) || item.Source != proof.Source
            || item.Owner?.Id != proof.OwnerId || item.Owner.LocalIdentity != proof.OwnerIdentity || item.Direction != proof.Direction)
            throw Failure("原生恢復與原擺攤搜尋意圖不同。");
        ShopPostCodeRecoveryProof.ValidateEvidence(proof, proof.Source, proof.RequestId, proof.OwnerId, proof.OwnerIdentity, proof.Direction);
        if (item.State == "Sent") return; // Interrupted recovery may already have appended the exact settlement.
        if (item.State != "Uncertain") throw Failure("原生恢復不可改寫已確認或未派送的其他結果。");
        Append(item with { Kind = "Settled", State = "Sent" });
    });
}
