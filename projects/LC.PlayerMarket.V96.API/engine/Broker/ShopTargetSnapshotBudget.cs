using System.Text.Json;

namespace MemoryHub.Service;

/// <summary>Precommit full-envelope admission. Rejected captures never grant dispatch or truncate owners.</summary>
internal static class ShopTargetSnapshotBudget
{
    internal const int ReservedBytes = 64 * 1024;
    internal const string RefreshBlockedReason = "本次周遭刷新尚未成功完成；保存名單僅供查看，請成功刷新後再查詢。";
    internal const string ReloadBlockedReason = "本機讀取器已重新啟動；保存名單僅供查看，請手動刷新後再查詢。";

    internal static void RequireFits(SourceIdentity source, ShopTargetSnapshot snapshot, ShopCommandResponse response)
    {
        foreach (var candidate in new[] { snapshot,
            snapshot with { CanDispatch = false, DispatchBlockReason = RefreshBlockedReason },
            snapshot with { CanDispatch = false, DispatchBlockReason = ReloadBlockedReason } })
        {
            RequireSavedFits(source, candidate);
            ShopResponseBudget.Fit(response with { TargetSnapshot = candidate }, ReservedBytes);
            ShopResponseBudget.Fit(response with { TargetSnapshot = candidate,
                SavedScan = ShopResponseBudget.FutureProgress(source.SessionKey, snapshot.Owners) }, ReservedBytes);
        }
    }

    internal static void RequirePlanFits(ShopTargetSnapshot targets, SavedScanPlan plan, ShopCommandResponse response) =>
        ShopResponseBudget.Fit(response with { TargetSnapshot = targets,
            SavedScan = ShopResponseBudget.FutureProgress(plan.Source.SessionKey, plan.Owners) }, ReservedBytes);

    internal static void RequireSavedFits(SourceIdentity source, ShopTargetSnapshot snapshot) =>
        RequireBytes(JsonSerializer.SerializeToUtf8Bytes(new { Source = source, Snapshot = snapshot }, HubProtocol.Json).Length, "SavedTargets");

    private static void RequireBytes(int bytes, string envelope)
    {
        if (bytes <= 0 || bytes > HubProtocol.MaxMessageBytes - ReservedBytes)
            throw new ShopRequestFailure(ShopFailure.Global("TargetSnapshotTooLarge",
                $"完整周遭名單{envelope}封套為 {bytes} bytes，保留 {ReservedBytes} bytes 後超過既有 {HubProtocol.MaxMessageBytes} bytes 界線；未截斷、未保存或授權本次名單。"));
    }
}
