namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private MarketDebugApiV1? debugApi;
    private readonly System.Windows.Forms.Timer debugApiTimer = new() { Interval = 1000 };
    private string? debugApiLastError;
    private void StartDebugApiV1()
    {
        try
        {
            debugApi = new MarketDebugApiV1(message =>
            {
                try { log.Write("DEBUG_API_ERROR", message); } catch { }
            });
            debugApiTimer.Tick += (_, _) => PublishDebugApiSnapshotV1();
            PublishDebugApiSnapshotV1();
            debugApiTimer.Start();
            FormClosed += (_, _) => StopDebugApiV1();
            log.Write("DEBUG_API_READY", "Local diagnostic API ready", new { pipe = MarketDebugApiV1.PipeName, protocol = 1 });
        }
        catch (Exception error) { log.Write("DEBUG_API_ERROR", error.GetType().Name + ": " + error.Message); }
    }
    private void StopDebugApiV1()
    {
        debugApiTimer.Stop();
        debugApi?.Dispose();
    }
    private void PublishDebugApiSnapshotV1()
    {
        try
        {
            // This callback runs on the UI timer. All fields below are copied from
            // current UI caches; no backend call, capture, file scan or LC access.
            debugApi?.Publish(new
            {
                version = typeof(Program).Assembly.GetName().Version?.ToString(), api = true,
                processId = Environment.ProcessId, executable = Environment.ProcessPath,
                snapshotAt = DateTimeOffset.UtcNow, snapshotSequence = snapshot?.Sequence,
                source = snapshot?.Source, sourceStatus = sourceStatus.Text,
                pageOpenIntervalMilliseconds = (int)pageWaitMilliseconds.Value,
                savedConfiguredMs = (int)pageWaitMilliseconds.Value,
                noAdditionalPageWait = noAdditionalPageWait.Checked,
                effectiveIntervalMilliseconds = noAdditionalPageWait.Checked ? 0 : (int)pageWaitMilliseconds.Value,
                scan = new { status = scanStatus.Text, state = lastConfirmedScan, actionBusy, cancelBusy, scanControlBusy },
                patrol = new { running = patrolRunning, blocked = patrolBlocked, stopRequested = patrolStopRequested, status = patrolStatus },
                navigation = navigationStatus.Text, shopState = shopStateStatus.Text,
                error = debugApiLastError, localReadFailure, closing,
                logRoot = store.DiagnosticsRoot, currentLog = log.CurrentPath,
                sessionRoot = SessionRoot, diagnosticsRoot = sessionLifecycle?.DiagnosticsRoot,
                observationOnly = true, diagnosticRequestsPerformLcReads = false, diagnosticRequestsPerformNativeCalls = false
            });
        }
        catch (Exception error) { try { log.Write("DEBUG_API_SNAPSHOT_ERROR", error.GetType().Name); } catch { } }
    }
}
