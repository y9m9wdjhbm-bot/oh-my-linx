namespace MemoryHub.Service;

internal static class ShopScanControlPolicy
{
    internal static ShopCommandRequest BindCurrent(ShopCommandRequest request, MarketScanState current)
    {
        if (!request.ForceDispatch) return request;
        if (string.IsNullOrWhiteSpace(request.ExpectedSourceSessionKey)
            || request.ExpectedSourceSessionKey != current.SourceSessionKey
            || !string.IsNullOrWhiteSpace(request.ExpectedScanJobId) && request.ExpectedScanJobId != current.JobId
            || !string.IsNullOrWhiteSpace(request.TargetSnapshotId) && request.TargetSnapshotId != current.TargetSnapshotId)
            throw new ShopRequestFailure(ShopFailure.Global("ScanControlMismatch", "已指定的搜尋工作、LC 來源或保存名單不同；未操作其他工作。"));
        return request with { ExpectedScanJobId = current.JobId, TargetSnapshotId = current.TargetSnapshotId };
    }
}

public sealed partial class ShopBroker
{
    private ScanPauseCoordinator? scanPause;
    private SourceIdentity? scanSource;

    // Called inside gate: source/job checks and the pause state transition are
    // atomic with EnterPageAsync's commitment of the next page.
    private ShopCommandResponse ControlScan(ShopCommandRequest req)
    {
        try
        {
            req = ShopScanControlPolicy.BindCurrent(req, scan);
            if (!scan.Running || scanPause is null || req.ExpectedScanJobId != scan.JobId ||
                req.ExpectedSourceSessionKey != scan.SourceSessionKey || req.TargetSnapshotId != scan.TargetSnapshotId)
                return State(req.RequestId, "Rejected", "搜尋工作、LC 來源或保存名單版本已不同；未操作其他工作。");
            if (req.Command == ShopCommands.PauseScan)
            {
                scanPause.RequestPause(req.ExpectedScanJobId!);
                Audit("SHOP_SCAN_PAUSE_REQUESTED", "已要求暫停；目前頁面會完成核對與清理，不重送。", new { scan.JobId });
                return State(req.RequestId, "PauseRequested", "目前頁面完成後暫停。");
            }
            var (_, expected) = req.ForceDispatch ? RequireForcedTargets(req) : RequireTargets(req);
            if (expected != scanSource) throw new InvalidOperationException("SCAN_SOURCE_CHANGED：來源已不同於原搜尋工作；請停止並手動刷新。");
            EnsureFields(req.ForceDispatch);
            // Cancelling a pending pause may occur while the current page owns
            // its legitimate pending journal. That owned in-flight work is not
            // an unresolved old call, and Resume itself dispatches no native call.
            if (ReportedSessionBlocked(expected, activeNativeSource, blockedSessions.Contains(SessionStem(expected))
                    || expected != activeNativeSource && (savedScans.BlocksSource(expected) || searchedOwners.BlocksSource(expected)),
                File.Exists(SessionPath(expected)), ObserveNativePending(expected).State != ShopNativePendingState.None))
                throw new ShopRequestFailure(ShopFailure.Unknown("此 LC session 有未解決的請求，無法繼續搜尋。"));
            scanPause.Resume(req.ExpectedScanJobId!);
            Audit("SHOP_SCAN_RESUMED", "繼續原搜尋工作剩餘的下一頁；不重送已完成頁面。", new { scan.JobId });
            return State(req.RequestId, "Resumed", "已繼續原搜尋工作。");
        }
        catch (Exception error) { return Rejected(req.RequestId, error); }
    }

    private MarketScanState CurrentScan()
    {
        var flags = scanPause?.Status ?? (Requested: false, Paused: false);
        return scan with
        {
            PauseRequested = scan.Running && flags.Requested,
            Paused = scan.Running && flags.Paused,
            Detail = scan.Running && flags.Paused ? $"已暫停：完成 {scan.Completed}/{scan.Total} 頁；繼續後從下一頁開始。"
                : scan.Running && flags.Requested ? $"等待目前頁面核對與清理後暫停：完成 {scan.Completed}/{scan.Total} 頁。" : scan.Detail
        };
    }
}

