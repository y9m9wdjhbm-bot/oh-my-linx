using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class PatrolController
{
    // The caller must first validate the owned job, source, plan and saved counts.
    // A settled failed batch is not permission to automatically submit it again.
    internal static bool IsSettledGlobalNativeCodeFailure(ShopCommandResponse response) =>
        response.WorkSettled && !response.Scan.Running
        && response.Scan.FailureCode == "NativeCodeUnavailable"
        && response.Scan.FailureScope == "Global"
        && response.Scan.FailurePhase == "Preflight";

    private void StopForSettledNativeCodeFailure(ShopCommandResponse response)
    {
        if (continuousRecovery && (response.SessionBlocked || response.SavedScan?.UncertainPages > 0))
            throw Block("原生工作結果仍不明；保留原請求，不因恢復重試而重送。");
        if (!IsSettledGlobalNativeCodeFailure(response) || continuousRecovery) return;
        throw Fail(DescribeStoppedScan(response.Scan)
            + $" 原生程式頁目前不可讀，巡迴已停止；保留 {response.SavedScan!.RemainingPages} 個未送出頁，確認後可手動按開始重試。");
    }
}
