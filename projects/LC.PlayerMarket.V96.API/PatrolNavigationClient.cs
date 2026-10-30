using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record PatrolNavigationReply(HttpStatusCode StatusCode, JsonElement Body);

/// <summary>Owns a private, authenticated navigation process; never attaches to another navigation UI.</summary>
internal sealed class PatrolNavigationClient : IPatrolNavigation, IAsyncDisposable
{
    private readonly string root;
    private readonly PatrolFreshSourceGate freshSource;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<PatrolSourceWaitNotice>? report;
    private readonly Func<HttpMethod, string, object?, CancellationToken, Task<PatrolNavigationReply>>? transport;
    private readonly bool readOnly;
    private readonly bool forceDispatch;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    // Capture's original ReadWindow owns the complete headers/body deadline.
    private readonly HttpClient captureHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim startupGate = new(1, 1);
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private sealed record PendingCapture(string Id, SourceIdentity Source, ulong SelfId);
    private PendingCapture? pendingCapture;
    private long captureSequence;
    private readonly CancellationTokenSource heartbeatStop = new();
    private readonly CancellationTokenSource initializationStop = new();
    private readonly Dictionary<string, PatrolNavigationRequest> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PatrolNavigationState> rejectedJobs = new(StringComparer.Ordinal);
    private Process? process;
    private Uri? address;
    private string? token, activeJob, lastJob;
    private Task? heartbeat;
    private bool disposed;
    private VerifiedDisplayPose? displaySnapshot;
    internal VerifiedDisplayPose? DisplaySnapshot => Volatile.Read(ref displaySnapshot);
    internal int? ChildExitCode { get; private set; }

    internal PatrolNavigationClient(string stateRoot, Func<HubSnapshot?> currentMarket, bool readOnly = false,
        Action<PatrolSourceWaitNotice>? report = null, bool forceDispatch = false)
        : this(stateRoot, currentMarket, readOnly, null, () => DateTimeOffset.UtcNow, Task.Delay, report, forceDispatch) { }

    // Tests run the actual capture/state parsing with a fake transport; no child or HTTP connection is created.
    internal PatrolNavigationClient(Func<HubSnapshot?> currentMarket,
        Func<HttpMethod, string, object?, CancellationToken, Task<PatrolNavigationReply>> transport,
        Func<DateTimeOffset> utcNow, Func<TimeSpan, CancellationToken, Task> delay,
        Action<PatrolSourceWaitNotice>? report = null, bool readOnly = true, bool forceDispatch = false)
        : this(Path.Combine(Path.GetTempPath(), "offline-patrol-client-unused"), currentMarket, readOnly, transport, utcNow, delay, report, forceDispatch) { }

    private PatrolNavigationClient(string stateRoot, Func<HubSnapshot?> currentMarket, bool readOnly,
        Func<HttpMethod, string, object?, CancellationToken, Task<PatrolNavigationReply>>? transport,
        Func<DateTimeOffset> utcNow, Func<TimeSpan, CancellationToken, Task> delay, Action<PatrolSourceWaitNotice>? report, bool forceDispatch)
    {
        root = Path.GetFullPath(stateRoot); this.readOnly = readOnly; this.transport = transport;
        this.forceDispatch = forceDispatch;
        this.utcNow = utcNow; this.delay = delay; this.report = report;
        freshSource = new(currentMarket, utcNow, delay, report);
    }

    public async Task<PatrolObservation> CaptureAsync(CancellationToken cancellationToken, PatrolObservation? expected = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        using var window = new ReadWindow(utcNow, cancellationToken);
        bool hadTypedReadFailure = false;
        bool ownsCaptureGate = false;
        try
        {
            await captureGate.WaitAsync(window.Token).WaitAsync(window.Token).ConfigureAwait(false);
            ownsCaptureGate = true;
            window.Check();
            // The caller owns the current run's source fence. Historical jobs remain owned for cleanup only.
            var initial = await freshSource.WaitAsync(expected?.Source, expected?.SelfId, window.Token, window.Deadline).ConfigureAwait(false);
            await SettleOwnCaptureAsync(initial, window).ConfigureAwait(false);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                window.Check();
                await freshSource.WaitAsync(initial.Source, initial.SelfId, window.Token, window.Deadline).ConfigureAwait(false);
                window.Check();
                JsonElement data;
                Require(pendingCapture is null, "上一筆唯讀取點尚未確認清理，未送出新的取點。");
                Require(captureSequence < long.MaxValue, "唯讀取點識別序號已達上限，未重用識別碼。");
                var pending = new PendingCapture((++captureSequence).ToString("x32", CultureInfo.InvariantCulture), initial.Source!, initial.SelfId!.Value);
                pendingCapture = pending;
                try { data = await SendAsync(HttpMethod.Post, "/api/capture", forceDispatch ?
                    new { captureId = pending.Id, expectedSource = SourceWire(pending.Source, pending.SelfId), forceDispatch = true } :
                    (object)new { captureId = pending.Id, expectedSource = SourceWire(pending.Source, pending.SelfId) }, window.Token).ConfigureAwait(false); }
                catch (NavigationRejectedException error) when (error.SafeReadCaptureRetry && attempt < 3)
                {
                    hadTypedReadFailure = true;
                    Notice("CAPTURE_READ_RETRY", "唯讀定位點讀取未完成且確認未送輸入；重新讀取完整來源與座標。", attempt);
                    await PauseReadAsync(window).ConfigureAwait(false);
                    continue;
                }
                catch (NavigationRejectedException error) when (forceDispatch && error.SafeReadCaptureRetry && attempt == 3)
                {
                    throw new PatrolSourceReadException("SourceUnavailableTimeout", "唯讀定位點安全重讀已達本次次數上限；原取點已確認清理，保留同點等待冷卻接續。");
                }
                window.Check();
                Require(data.TryGetProperty("inputSent", out var input) && input.ValueKind == JsonValueKind.False, "定位點讀取未提供唯讀證據。");
                Require(SourceMatches(data.GetProperty("source"), initial.Source!, initial.SelfId!.Value), "定位點回覆來源／角色不符，沒有重讀或送出動作。");
                int map = Int(data, "mapId", -1), x = Int(data, "x", -1), y = Int(data, "y", -1);
                Require(map >= 0 && x is >= 0 and <= 65535 && y is >= 0 and <= 65535,
                    "定位點回覆的地圖或座標格式不完整，沒有重讀或送出動作。");
                var postRead = await freshSource.WaitWithEvidenceAsync(initial.Source, initial.SelfId, window.Token, window.Deadline).ConfigureAwait(false);
                var context = postRead.Context;
                window.Check();
                var sampled = SampledAt(data);
                if (Fresh(sampled))
                {
                    var observation = new PatrolObservation(context.Source!, context.SelfId!.Value, map, x, y, sampled, Flag(data, "standing"));
                    Volatile.Write(ref displaySnapshot, new VerifiedDisplayPose(observation.Source, observation.SelfId, map, x, y, sampled));
                    return observation;
                }
                if (attempt == 3) throw new PatrolSourceReadException("NavigationSampleStale", "定位點完整重讀後仍已過期，未使用舊座標。");
                var now = utcNow();
                // Only this successful, read-only capture's actual same-source recovery can earn one fresh read.
                // Invalid/future timestamps and typed capture failures do not extend the original window.
                if (!hadTypedReadFailure && postRead.Recovered && sampled != default && sampled <= now
                    && now - sampled > TimeSpan.FromSeconds(2) && window.TryGrantRecoveredCapture())
                    Notice("CAPTURE_RECOVERY_WINDOW", "同來源空窗已恢復但原唯讀採樣過期；使用一次四秒重新取點額度，總計不超過八秒。", attempt);
                Notice("CAPTURE_SAMPLE_REFRESH", "等待來源恢復後，原定位點採樣已過期；只重新讀取定位點，不重送移動。", attempt);
            }
            throw new PatrolSourceReadException("SourceUnavailableTimeout", "唯讀定位點重讀已達次數上限。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new PatrolSourceReadException("SourceUnavailableTimeout", "定位點等待與唯讀重讀已達本次有界上限。"); }
        finally { if (ownsCaptureGate) captureGate.Release(); }
    }

    private async Task SettleOwnCaptureAsync(LiveContext current, ReadWindow window)
    {
        var pending = pendingCapture;
        if (pending is null) return;
        Require(pending.Source == current.Source && pending.SelfId == current.SelfId,
            "未完成取點屬於不同來源／角色；保留原識別碼且不送出新動作。");
        Notice("CAPTURE_CLEANUP_WAIT", "先等待上一筆唯讀取點清理；只查詢原識別碼，不重送取點或行走。", 0);
        for (int poll = 0; poll < 40; poll++)
        {
            window.Check();
            await freshSource.WaitAsync(pending.Source, pending.SelfId, window.Token, window.Deadline).ConfigureAwait(false);
            var data = await SendAsync(HttpMethod.Get, "/api/capture-status?captureId=" + pending.Id, null, window.Token).ConfigureAwait(false);
            window.Check();
            if (ValidateCaptureReceipt(data, pending))
            {
                RetireConfirmedReadFailure(data, pending);
                string? error = Text(data, "error");
                Require((Text(data, "state") == "Completed" && error == "")
                    || (Text(data, "state") == "Failed" && RetryableCaptureError(error)),
                    "上一筆唯讀取點未提供可恢復的完成原因：" + (error ?? "缺少原因") + "；未重新送出動作。");
                pendingCapture = null;
                Notice("CAPTURE_CLEANUP_RECOVERED", "已確認原取點清理完成並自動清除暫時錯誤；接著重新取得同來源的新座標。", poll + 1);
                return;
            }
            await PauseReadAsync(window).ConfigureAwait(false);
        }
        throw new PatrolSourceReadException("SourceUnavailableTimeout", "原唯讀取點清理仍未完成，已達有界等待上限。");
    }

    private static bool ValidateCaptureReceipt(JsonElement data, PendingCapture expected)
    {
        Require(Text(data, "captureId") == expected.Id && Flag(data, "readOnly")
            && data.TryGetProperty("inputSent", out var input) && input.ValueKind == JsonValueKind.False
            && data.TryGetProperty("expectedSource", out var source) && SourceMatches(source, expected.Source, expected.SelfId),
            "唯讀取點回覆缺少原識別碼、完整來源或未送輸入證據；保留待清理狀態。");
        Require(data.TryGetProperty("terminal", out var terminal) && terminal.ValueKind is JsonValueKind.True or JsonValueKind.False
            && data.TryGetProperty("cleanupConfirmed", out var cleanup) && cleanup.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "唯讀取點回覆缺少完整清理狀態。");
        string? state = Text(data, "state");
        Require(Text(data, "error") is not null && data.TryGetProperty("accepted", out var accepted)
            && accepted.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "唯讀取點回覆缺少受理或結果原因。");
        Require(Flag(data, "accepted") == (state != "Rejected"), "唯讀取點受理與終止狀態矛盾。");
        if (!Flag(data, "terminal"))
        {
            Require(state == "Reading" && Text(data, "error") == "" && !Flag(data, "cleanupConfirmed"), "唯讀取點執行狀態矛盾。");
            return false;
        }
        Require(state is "Completed" or "Failed" or "Rejected", "唯讀取點終止狀態未知。");
        Require(Flag(data, "cleanupConfirmed"), "原唯讀取點清理未確認；不清除錯誤且不送出新動作。");
        return true;
    }

    // A completed read failure is not an unknown in-flight operation. Retiring
    // its fully verified receipt ends this call; it never authorizes a retry
    // inside CaptureAsync or turns a failed read into a valid observation.
    // Only the backend's exact, cleanup-proven bind read classification can
    // ask the resumable controller to cool down and capture again in this phase.
    private void RetireConfirmedReadFailure(JsonElement data, PendingCapture expected)
    {
        if (Text(data, "state") != "Failed" || Text(data, "error") != "READ_CAPTURE_FAILED") return;
        Require(ReferenceEquals(pendingCapture, expected), "唯讀取點歸屬已變更，未清除待確認狀態。");
        pendingCapture = null;
        if (Text(data, "sourceError") == "READ_INCOMPLETE"
            && Text(data, "sourcePhase") == "factory.context")
        {
            string recoverableDetail = "唯讀取點失敗：READ_INCOMPLETE；原取點 " + expected.Id
                + " 已確認結束、未送輸入且清理完成，等待原巡迴階段冷卻後重新取點。";
            Notice("CAPTURE_READ_RECOVERY_READY", recoverableDetail, 0);
            throw new PatrolSourceReadException("NavigationCaptureReadIncomplete", recoverableDetail);
        }
        string detail = "唯讀取點失敗：READ_CAPTURE_FAILED；原取點 " + expected.Id
            + " 已確認結束、未送輸入且清理完成。本次停止，下一次開始可重新取點。";
        Notice("CAPTURE_FAILED_SETTLED", detail, 0);
        throw new InvalidOperationException(detail);
    }

    public async Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PatrolRouteRules.ValidateArrivalRadius(request.ArrivalRadius);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        Require(!readOnly, "唯讀驗證模式不可開始行走。");
        if (!forceDispatch) Require(pendingCapture is null && captureGate.CurrentCount == 1,
            "唯讀取點尚在執行或清理未確認；尚未送出導航。");
        string? previousActive, previousLast;
        lock (jobs)
        {
            Require(!jobs.ContainsKey(request.JobId), "導航工作 ID 已使用，沒有再次送出。");
            previousActive = activeJob; previousLast = lastJob;
            jobs.Add(request.JobId, request); activeJob = lastJob = request.JobId;
        }
        // Register before the one HTTP send: an unknown response is reconciled by this same ID.
        JsonElement data;
        try
        {
        cancellationToken.ThrowIfCancellationRequested();
        // Foreground permission is best effort; the authenticated backend retains its target identity guards.
        if (transport is null)
        {
            bool granted = PatrolForegroundPermission.Prepare(Environment.ProcessId, request.Expected.Source.ProcessId, process!.Id);
            Notice("FOREGROUND_PERMISSION_ATTEMPT", granted ? "導航前景權限已準備。" : "導航前景權限未取得，繼續提交已驗證的導航請求。", 1);
        }
        object navigatePayload = new
        {
            jobId = request.JobId, expectedSource = SourceWire(request.Expected), arrivalRadius = request.ArrivalRadius,
            waypoint = new { mapId = int.Parse(request.Waypoint.MapId, CultureInfo.InvariantCulture), x = request.Waypoint.X, y = request.Waypoint.Y }
        };
        if (forceDispatch) navigatePayload = new
        {
            jobId = request.JobId, expectedSource = SourceWire(request.Expected), arrivalRadius = request.ArrivalRadius, forceDispatch = true,
            waypoint = new { mapId = int.Parse(request.Waypoint.MapId, CultureInfo.InvariantCulture), x = request.Waypoint.X, y = request.Waypoint.Y }
        };
        data = await SendAsync(HttpMethod.Post, "/api/navigate", navigatePayload, cancellationToken).ConfigureAwait(false);
        }
        catch (NavigationRejectedException error)
        {
            // A complete 409 refusal is positive non-acceptance, unlike a lost response.
            var rejected = new PatrolNavigationState
            {
                JobId = request.JobId, WaypointId = request.Waypoint.PointId, TargetX = request.Waypoint.X, TargetY = request.Waypoint.Y,
                ArrivalRadius = request.ArrivalRadius,
                Source = request.Expected.Source, SelfId = request.Expected.SelfId, MapId = request.Expected.MapId,
                X = request.Expected.X, Y = request.Expected.Y, SampledAt = DateTimeOffset.UtcNow,
                State = "Failed", Terminal = true, CleanupConfirmed = true, WorkSettled = true, PositionVerified = false, Error = error.Message
            };
            lock (jobs) { rejectedJobs.Add(request.JobId, rejected); activeJob = previousActive; lastJob = previousLast; }
            return rejected;
        }
        // A later status refusal cannot prove the accepted navigate command was never sent.
        return await ParseStateAsync(data, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = OwnedJob(jobId);
        lock (jobs) if (rejectedJobs.TryGetValue(jobId, out var rejected)) return rejected;
        var data = await SendAsync(HttpMethod.Get, "/api/status?jobId=" + Uri.EscapeDataString(jobId), null, cancellationToken).ConfigureAwait(false);
        return await ParseStateAsync(data, request, cancellationToken, requireFreshCompletion: !forceDispatch).ConfigureAwait(false);
    }

    public async Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = OwnedJob(expectedJobId);
        lock (jobs) if (rejectedJobs.TryGetValue(expectedJobId, out var rejected)) return rejected;
        var data = await SendAsync(HttpMethod.Post, "/api/stop", new { jobId = expectedJobId }, cancellationToken).ConfigureAwait(false);
        return await ParseStateAsync(data, request, cancellationToken, requireFreshCompletion: false).ConfigureAwait(false);
    }

    private PatrolNavigationRequest OwnedJob(string id)
    { lock (jobs) return jobs.TryGetValue(id, out var request) ? request : throw new InvalidOperationException("未擁有此導航工作，不會停止其他工作。"); }

    private Task<PatrolNavigationState> ParseStateAsync(JsonElement data, PatrolNavigationRequest request,
        CancellationToken cancellationToken, bool requireFreshCompletion = true)
    {
        if (!forceDispatch) return ParseStrictStateAsync(data, request, cancellationToken, requireFreshCompletion);
        cancellationToken.ThrowIfCancellationRequested();
        Require(Text(data, "jobId") == request.JobId, "導航回覆工作 ID 不符。");
        Require(Int(data, "arrivalRadius", -1) == request.ArrivalRadius, "導航回覆到點容差與本次設定不符。");
        Require(SourceMatches(data.GetProperty("source"), request.Expected.Source, request.Expected.SelfId), "導航回覆來源不符。");
        var target = data.GetProperty("waypoint");
        Require(target.GetProperty("mapId").GetInt32() == int.Parse(request.Waypoint.MapId, CultureInfo.InvariantCulture)
            && target.GetProperty("x").GetInt32() == request.Waypoint.X && target.GetProperty("y").GetInt32() == request.Waypoint.Y,
            "導航回覆的目標不同，沒有開始商店搜尋。");
        bool terminal = Flag(data, "terminal"), completed = Flag(data, "completed"), cleanup = Flag(data, "cleanupConfirmed");
        var source = request.Expected.Source;
        var sampled = SampledAt(data);
        bool positionVerified = data.TryGetProperty("mapId", out var mapValue) && mapValue.ValueKind == JsonValueKind.Number
            && mapValue.TryGetInt32(out int map) && map == request.Expected.MapId
            && data.TryGetProperty("x", out var xValue) && xValue.ValueKind == JsonValueKind.Number
            && xValue.TryGetInt32(out int x) && x is >= 0 and <= 65535
            && data.TryGetProperty("y", out var yValue) && yValue.ValueKind == JsonValueKind.Number
            && yValue.TryGetInt32(out int y) && y is >= 0 and <= 65535 && Fresh(sampled);
        if (completed && requireFreshCompletion)
        {
            Require(positionVerified && Flag(data, "sameMap")
                && Int(data, "distance", -1) >= 0 && Int(data, "distance", int.MaxValue) <= request.ArrivalRadius
                && Int(data, "distance", -1) == Math.Max(Math.Abs((long)Int(data, "x", -1) - request.Waypoint.X), Math.Abs((long)Int(data, "y", -1) - request.Waypoint.Y))
                && !Flag(data, "stopRequested") && !Flag(data, "userStop"), "導航尚無同來源、同地圖且位於指定範圍的新鮮實際位置。");
        }
        if (terminal && cleanup) Interlocked.CompareExchange(ref activeJob, null, request.JobId);
        var parsedState = new PatrolNavigationState()
        {
            JobId = request.JobId, WaypointId = request.Waypoint.PointId, TargetX = request.Waypoint.X, TargetY = request.Waypoint.Y,
            ArrivalRadius = request.ArrivalRadius,
            Source = source, SelfId = request.Expected.SelfId, MapId = Int(data, "mapId", request.Expected.MapId),
            X = Int(data, "x", request.Expected.X), Y = Int(data, "y", request.Expected.Y), SampledAt = sampled,
            State = Text(data, "state") ?? "Unknown", Terminal = terminal, Completed = completed,
            CleanupConfirmed = cleanup, WorkSettled = terminal && cleanup,
            Standing = Flag(data, "standing"), StandingSamples = Int(data, "standingSamples"),
            PositionVerified = positionVerified,
            UserStop = Flag(data, "userStop"), StopRequested = Flag(data, "stopRequested"),
            Error = Text(data, "detail") ?? Text(data, "stopReason") ?? ""
        };
        // Display accepts only actual numeric fields, never the control parser fallback position.
        bool displayValid = data.TryGetProperty("mapId", out var dm) && dm.ValueKind == JsonValueKind.Number && dm.TryGetInt32(out var dmap) && dmap >= 0
            && data.TryGetProperty("x", out var dx) && dx.ValueKind == JsonValueKind.Number && dx.TryGetInt32(out var displayX) && displayX is >= 0 and <= 65535
            && data.TryGetProperty("y", out var dy) && dy.ValueKind == JsonValueKind.Number && dy.TryGetInt32(out var displayY) && displayY is >= 0 and <= 65535
            && sampled <= utcNow() && utcNow() - sampled <= TimeSpan.FromSeconds(2);
        Volatile.Write(ref displaySnapshot, displayValid ? new VerifiedDisplayPose(source, request.Expected.SelfId, parsedState.MapId, parsedState.X, parsedState.Y, sampled, request.JobId, request.Waypoint.X, request.Waypoint.Y, int.Parse(request.Waypoint.MapId, CultureInfo.InvariantCulture)) : null);
        return Task.FromResult(parsedState);
    }

    private async Task<PatrolNavigationState> ParseStrictStateAsync(JsonElement data, PatrolNavigationRequest request,
        CancellationToken cancellationToken, bool requireFreshCompletion = true)
    {
        using var window = new ReadWindow(utcNow, cancellationToken);
        try
        {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
        window.Check();
        Require(Text(data, "jobId") == request.JobId, "導航回覆工作 ID 不符。");
        Require(Int(data, "arrivalRadius", -1) == request.ArrivalRadius, "導航回覆到點容差與本次設定不符。");
        Require(SourceMatches(data.GetProperty("source"), request.Expected.Source, request.Expected.SelfId), "導航回覆來源不符。");
        var target = data.GetProperty("waypoint");
        Require(target.GetProperty("mapId").GetInt32() == int.Parse(request.Waypoint.MapId, CultureInfo.InvariantCulture)
            && target.GetProperty("x").GetInt32() == request.Waypoint.X && target.GetProperty("y").GetInt32() == request.Waypoint.Y,
            "導航回覆的目標不同，沒有開始商店搜尋。");
        bool terminal = Flag(data, "terminal"), completed = Flag(data, "completed"), cleanup = Flag(data, "cleanupConfirmed");
        var source = request.Expected.Source;
        var sampled = SampledAt(data);
        if (completed && requireFreshCompletion)
        {
            var live = await freshSource.WaitAsync(request.Expected.Source, request.Expected.SelfId, window.Token, window.Deadline).ConfigureAwait(false);
            window.Check(); source = live.Source!;
            if (!Fresh(sampled))
            {
                if (attempt == 3) throw new PatrolSourceReadException("NavigationSampleStale", "同一導航工作的完成採樣仍過期，未採用舊完成證據。");
                Notice("STATUS_SAMPLE_REFRESH", "等待來源恢復後，原完成採樣已過期；只查詢原導航工作狀態。", attempt);
                data = await SendAsync(HttpMethod.Get, "/api/status?jobId=" + Uri.EscapeDataString(request.JobId), null, window.Token).ConfigureAwait(false);
                continue;
            }
            Require(data.GetProperty("x").ValueKind == JsonValueKind.Number
                && data.GetProperty("y").ValueKind == JsonValueKind.Number && data.GetProperty("mapId").ValueKind == JsonValueKind.Number
                && Flag(data, "sameMap") && Flag(data, "standing")
                && Int(data, "standingSamples") >= 2 && Int(data, "mapId", -1) == request.Expected.MapId
                && Int(data, "x", -1) is >= 0 and <= 65535 && Int(data, "y", -1) is >= 0 and <= 65535
                && Int(data, "distance", -1) >= 0 && Int(data, "distance", int.MaxValue) <= request.ArrivalRadius
                && Int(data, "distance", -1) == Math.Max(Math.Abs((long)Int(data, "x", -1) - request.Waypoint.X), Math.Abs((long)Int(data, "y", -1) - request.Waypoint.Y))
                && terminal && cleanup && !Flag(data, "stopRequested") && !Flag(data, "userStop")
                && !string.IsNullOrWhiteSpace(Text(data, "completionId")), "導航尚無完整站定與清理證據。");
        }
        if (terminal && cleanup) Interlocked.CompareExchange(ref activeJob, null, request.JobId);
        var parsedState = new PatrolNavigationState()
        {
            JobId = request.JobId, WaypointId = request.Waypoint.PointId, TargetX = request.Waypoint.X, TargetY = request.Waypoint.Y,
            ArrivalRadius = request.ArrivalRadius,
            Source = source, SelfId = request.Expected.SelfId, MapId = Int(data, "mapId", request.Expected.MapId),
            X = Int(data, "x", request.Expected.X), Y = Int(data, "y", request.Expected.Y), SampledAt = sampled,
            State = Text(data, "state") ?? "Unknown", Terminal = terminal, Completed = completed,
            CleanupConfirmed = cleanup, WorkSettled = terminal && cleanup,
            Standing = Flag(data, "standing"), StandingSamples = Int(data, "standingSamples"),
            UserStop = Flag(data, "userStop"), StopRequested = Flag(data, "stopRequested"),
            Error = Text(data, "detail") ?? Text(data, "stopReason") ?? ""
        };
        // Display accepts only actual numeric fields, never the control parser fallback position.
        bool displayValid = data.TryGetProperty("mapId", out var dm) && dm.ValueKind == JsonValueKind.Number && dm.TryGetInt32(out var dmap) && dmap >= 0
            && data.TryGetProperty("x", out var dx) && dx.ValueKind == JsonValueKind.Number && dx.TryGetInt32(out var displayX) && displayX is >= 0 and <= 65535
            && data.TryGetProperty("y", out var dy) && dy.ValueKind == JsonValueKind.Number && dy.TryGetInt32(out var displayY) && displayY is >= 0 and <= 65535
            && sampled <= utcNow() && utcNow() - sampled <= TimeSpan.FromSeconds(2);
        Volatile.Write(ref displaySnapshot, displayValid ? new VerifiedDisplayPose(source, request.Expected.SelfId, parsedState.MapId, parsedState.X, parsedState.Y, sampled, request.JobId, request.Waypoint.X, request.Waypoint.Y, int.Parse(request.Waypoint.MapId, CultureInfo.InvariantCulture)) : null);
        return parsedState;
        }
        throw new PatrolSourceReadException("SourceUnavailableTimeout", "同一導航工作狀態重讀已達次數上限。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new PatrolSourceReadException("SourceUnavailableTimeout", "導航完成讀值等待已達四秒上限。"); }
    }

    internal static bool SourceMatches(JsonElement identity, SourceIdentity source, ulong selfId)
    {
        try
        {
            return identity.GetProperty("pid").GetInt32() == source.ProcessId
                && Text(identity, "creationFileTime") == source.CreationFileTime.ToString(CultureInfo.InvariantCulture)
                && string.Equals(Text(identity, "imagePath"), source.ImagePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Text(identity, "sha256"), source.DiskSha256, StringComparison.OrdinalIgnoreCase)
                && Text(identity, "selfId") == selfId.ToString(CultureInfo.InvariantCulture)
                && ParseBase(Text(identity, "imageBase")) == ParseBase(source.ImageBase)
                && identity.GetProperty("imageSize").GetUInt32() == source.ImageSize;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }

    private static object SourceWire(PatrolObservation expected) => SourceWire(expected.Source, expected.SelfId);
    private static object SourceWire(SourceIdentity source, ulong selfId) => new
    {
        pid = source.ProcessId, creationFileTime = source.CreationFileTime.ToString(CultureInfo.InvariantCulture),
        imagePath = source.ImagePath, sha256 = source.DiskSha256,
        selfId = selfId.ToString(CultureInfo.InvariantCulture), imageBase = source.ImageBase, imageSize = source.ImageSize
    };

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (transport is not null) return;
        using var initializing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, initializationStop.Token);
        var startupToken = initializing.Token;
        await startupGate.WaitAsync(startupToken).ConfigureAwait(false);
        try
        {
            startupToken.ThrowIfCancellationRequested();
            if (address is not null) { Require(process is { HasExited: false }, "本次導航元件已退出；不會自動重啟或接管動作。"); return; }
            Require(process is null, "先前導航啟動尚未完整確認，請保留 LOG。");
            string executable = Path.Combine(AppContext.BaseDirectory, "Navigation", "MarketNavigationBackend.exe");
            Notice("BUNDLE_VERIFY_START", "正在核對導航元件完整檔案；尚未開始取得角色資料。", 0);
            await Task.Run(() => VerifyBundle(executable, startupToken), startupToken).ConfigureAwait(false);
            startupToken.ThrowIfCancellationRequested();
            string run = Path.Combine(root, DateTimeOffset.UtcNow.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            string ready = Path.Combine(run, "ready.json");
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in new[] { "--ready-file", ready, "--log-dir", Path.Combine(run, "log"),
                "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--parent-created", Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            if (readOnly) start.ArgumentList.Add("--read-only");
            startupToken.ThrowIfCancellationRequested();
            Notice("BACKEND_START", "導航檔案核對完成，正在啟動導航元件。", NavigationBundleContract.ExpectedFileCount);
            process = Process.Start(start) ?? throw new IOException("導航元件無法啟動。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(startupToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            JsonElement value;
            while (true)
            {
                Require(!process.HasExited, "導航元件未就緒即退出；可能有另一個導航程式正在使用。請查看本次導航 LOG。");
                if (File.Exists(ready))
                {
                    try
                    {
                        Require(new FileInfo(ready).Length <= 8192, "導航就緒檔過大。");
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ready, deadline.Token).ConfigureAwait(false));
                        value = document.RootElement.Clone(); break;
                    }
                    catch (Exception error) when (error is IOException or JsonException) { }
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            Require(Text(value, "programId") == "LC.PlayerMarket.Navigation.V1" && Int(value, "protocol") == 1
                && Int(value, "pid") == process.Id && Int(value, "parentPid") == Environment.ProcessId
                && Flag(value, "readOnly") == readOnly, "導航就緒身分不同。");
            Require(Uri.TryCreate(Text(value, "url"), UriKind.Absolute, out var parsed) && IsLocalUrl(parsed!), "導航端點不在指定本機位址。");
            string secret = Text(value, "token") ?? "";
            Require(secret.Length is >= 32 and <= 128 && secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "導航驗證碼無效。");
            startupToken.ThrowIfCancellationRequested();
            address = parsed; token = secret;
            heartbeat = HeartbeatLoopAsync();
            Notice("BACKEND_READY", "導航元件已就緒，接著取得目前角色與位置。", NavigationBundleContract.ExpectedFileCount);
        }
        catch
        {
            if (address is null && process is not null)
            {
                // Startup has not permitted a navigate command. Cancel only this idle child.
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); }
                process.Dispose(); process = null;
            }
            throw;
        }
        finally { startupGate.Release(); }
    }

    private void VerifyBundle(string executable, CancellationToken cancellationToken) =>
        NavigationBundleContract.Verify(executable, cancellationToken, progress =>
            Notice(progress.Complete ? "BUNDLE_VERIFY_READY" : "BUNDLE_VERIFY_PROGRESS",
                $"導航檔案完整核對：{progress.VerifiedFiles}/{progress.TotalFiles}。" + (progress.Complete ? "全部檔案已通過驗證。" : "尚未取得角色資料。"),
                progress.VerifiedFiles));

    // Release acceptance uses the same bootstrap/cleanup as real patrol without any LC read or input.
    internal async Task<int> InitializeReadOnlyAsync(CancellationToken cancellationToken)
    {
        Require(readOnly && transport is null, "發布啟動驗收僅允許真實唯讀子程序。");
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        Require(process is { HasExited: false } && address is not null, "導航後端未完成就緒核對。");
        return process!.Id;
    }

    internal async Task ProbeReadOnlyReadyAsync(CancellationToken cancellationToken)
    {
        Require(readOnly && transport is null && pendingCapture is null && lastJob is null,
            "就緒探測僅限尚未執行取點或導航的唯讀子程序。");
        var reply = await SendHttpAsync(HttpMethod.Get, "/api/capture-status?captureId=" + Guid.NewGuid().ToString("N"),
            null, cancellationToken).ConfigureAwait(false);
        Require(reply.StatusCode == HttpStatusCode.Conflict && reply.Body.TryGetProperty("ok", out var ok)
            && ok.ValueKind == JsonValueKind.False && Text(reply.Body, "error") == "CAPTURE_NOT_FOUND",
            "導航後端的已驗證就緒探測回覆不符。");
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!heartbeatStop.IsCancellationRequested)
            {
                if (Volatile.Read(ref activeJob) is { } id)
                {
                    try { await SendAsync(HttpMethod.Post, "/api/heartbeat", new { jobId = id }, heartbeatStop.Token).ConfigureAwait(false); }
                    catch (Exception) when (!heartbeatStop.IsCancellationRequested) { /* The adapter independently stops on lease expiry; state polling will report it. */ }
                }
                await Task.Delay(500, heartbeatStop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = transport is not null
            ? await transport(method, path, payload, cancellationToken).ConfigureAwait(false)
            : await SendHttpAsync(method, path, payload, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var body = reply.Body;
        if (method == HttpMethod.Post && path == "/api/capture")
        {
            var pending = pendingCapture;
            Require(pending is not null, "唯讀取點回覆沒有本程式持有的識別碼。");
            if (!ValidateCaptureReceipt(body, pending!))
                throw new PatrolSourceReadException("SourceUnavailableTimeout", "原唯讀取點尚在執行，後續只能查詢原識別碼。");
            if (reply.StatusCode == HttpStatusCode.Conflict
                && body.TryGetProperty("ok", out var failedReadOk) && failedReadOk.ValueKind == JsonValueKind.False)
                RetireConfirmedReadFailure(body, pending!);
            bool completed = (int)reply.StatusCode is >= 200 and <= 299 && Flag(body, "ok")
                && Text(body, "state") == "Completed" && Text(body, "error") == "";
            bool retryableRead = reply.StatusCode == HttpStatusCode.Conflict
                && body.TryGetProperty("ok", out var captureOk) && captureOk.ValueKind == JsonValueKind.False
                && Text(body, "state") == "Failed" && RetryableCaptureError(Text(body, "error"));
            Require(completed || retryableRead,
                "唯讀取點尚未提供可恢復的完成結果：" + (Text(body, "error") ?? "缺少原因") + "；保留原識別碼，不重新送出。");
            pendingCapture = null;
        }
        if (reply.StatusCode == HttpStatusCode.Conflict && body.TryGetProperty("ok", out var accepted)
            && accepted.ValueKind == JsonValueKind.False)
        {
            bool safeReadCaptureRetry = method == HttpMethod.Post && path == "/api/capture"
                && RetryableCaptureError(Text(body, "error")) && Flag(body, "readOnly")
                && body.TryGetProperty("inputSent", out var input) && input.ValueKind == JsonValueKind.False;
            throw new NavigationRejectedException(Text(body, "error") ?? "導航要求未受理。", safeReadCaptureRetry);
        }
        Require((int)reply.StatusCode is >= 200 and <= 299 && Flag(body, "ok"),
            "導航元件拒絕要求：" + (Text(body, "error") ?? reply.StatusCode.ToString()));
        return body;
    }

    // Only exact backend classifications, plus the complete owned receipt above,
    // authorize another read. Generic failure/bounds/source changes remain errors.
    private static bool RetryableCaptureError(string? error) => error is
        "READ_CAPTURE_MODULE_PATH_FAILED" or "READ_CAPTURE_VECTOR_CHANGED";

    private async Task<PatrolNavigationReply> SendHttpAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(address is not null && token is not null && process is { HasExited: false }, "導航元件尚未就緒或已退出。");
        return await SendHttpCoreAsync(address!, token!, method, path, payload, cancellationToken).ConfigureAwait(false);
    }

    // Offline loopback tests bridge the fake bootstrap into this same HTTP implementation.
    // Production reaches it only after the original owned-child/ready checks above.
    internal async Task<PatrolNavigationReply> SendHttpCoreAsync(Uri endpoint, string secret,
        HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(IsLocalUrl(endpoint), "導航端點不在指定本機位址。");
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Add("X-MapNav-Token", secret);
        request.Headers.Add("Origin", endpoint.GetLeftPart(UriPartial.Authority));
        if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload, HubProtocol.Json), Encoding.UTF8, "application/json");
        var client = method == HttpMethod.Post && path == "/api/capture" ? captureHttp : http;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        Require(response.Content.Headers.ContentLength is not > 1024 * 1024, "導航回覆過大。");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
        { Require(buffer.Length + count <= 1024 * 1024, "導航回覆超出上限。"); buffer.Write(bytes, 0, count); }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return new(response.StatusCode, document.RootElement.Clone());
    }

    private async Task PauseReadAsync(ReadWindow window)
    {
        window.Check();
        var remaining = window.Deadline - utcNow();
        await delay(remaining < PatrolFreshSourceGate.PollInterval ? remaining : PatrolFreshSourceGate.PollInterval, window.Token).ConfigureAwait(false);
        window.Check();
    }

    private void Notice(string code, string detail, int attempts)
    {
        try { report?.Invoke(new(code, detail, attempts, TimeSpan.Zero)); }
        catch { /* Diagnostics do not authorize operations. */ }
    }

    private sealed class ReadWindow : IDisposable
    {
        private readonly Func<DateTimeOffset> clock;
        private readonly CancellationTokenSource stop;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly DateTimeOffset hardDeadline;
        private TimeSpan wallDeadline = PatrolFreshSourceGate.MaximumWait;
        private bool recoveryGranted;
        internal ReadWindow(Func<DateTimeOffset> clock, CancellationToken token)
        {
            this.clock = clock; var began = clock(); Deadline = began + PatrolFreshSourceGate.MaximumWait;
            hardDeadline = began + TimeSpan.FromSeconds(8);
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            stop.CancelAfter(PatrolFreshSourceGate.MaximumWait);
        }
        internal DateTimeOffset Deadline { get; private set; }
        internal CancellationToken Token => stop.Token;
        internal void Check()
        {
            Token.ThrowIfCancellationRequested();
            if (clock() >= Deadline || elapsed.Elapsed >= wallDeadline)
                throw new PatrolSourceReadException("SourceUnavailableTimeout", "唯讀等待與重讀已達本次有界上限。");
        }
        internal bool TryGrantRecoveredCapture()
        {
            Check();
            if (recoveryGranted) return false;
            recoveryGranted = true;
            var now = clock();
            var wallNow = elapsed.Elapsed;
            var remaining = new[] { PatrolFreshSourceGate.MaximumWait, hardDeadline - now, TimeSpan.FromSeconds(8) - wallNow }.Min();
            if (remaining <= TimeSpan.Zero) throw new PatrolSourceReadException("SourceUnavailableTimeout", "唯讀來源恢復已達總計八秒上限。");
            Deadline = now + remaining; wallDeadline = wallNow + remaining;
            // Keep the original caller token; resetting this timer cannot revive an already cancelled source.
            stop.CancelAfter(remaining);
            Check();
            return true;
        }
        public void Dispose() => stop.Dispose();
    }

    public ValueTask DisposeAsync() => DisposeAsync(CancellationToken.None);

    internal async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed)) return;
        cancellationToken.ThrowIfCancellationRequested();
        // Cancellation applies only to bootstrap, before any native request.
        // Stop/shutdown below keep their own cleanup deadline and owned job ID.
        try { initializationStop.Cancel(); }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed)) { return; }
        await startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (disposed) return;
        if (process is not null && !process.HasExited)
        {
            if (address is not null)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                string? id = lastJob;
                if (id is not null)
                {
                    var state = await StopAsync(id, deadline.Token).ConfigureAwait(false);
                    while (!state.WorkSettled || !state.CleanupConfirmed)
                    { await Task.Delay(200, deadline.Token).ConfigureAwait(false); state = await GetStateAsync(id, deadline.Token).ConfigureAwait(false); }
                }
                await SendAsync(HttpMethod.Post, "/api/shutdown", id is null ? new { } : (object)new { jobId = id }, deadline.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            else
            {
                // No navigate can be sent before a validated ready endpoint exists.
                cancellationToken.ThrowIfCancellationRequested();
                process.Kill(); await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        heartbeatStop.Cancel(); if (heartbeat is not null) await heartbeat.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (process is { HasExited: true }) ChildExitCode = process.ExitCode;
        process?.Dispose(); http.Dispose(); captureHttp.Dispose(); heartbeatStop.Dispose();
        Volatile.Write(ref disposed, true); initializationStop.Dispose();
        }
        finally { startupGate.Release(); }
    }

    internal static bool IsLocalUrl(Uri url) => url.Scheme == "http" && url.Host == System.Net.IPAddress.Loopback.ToString() && url.Port > 0
        && string.IsNullOrEmpty(url.UserInfo) && url.AbsolutePath == "/" && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment);
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement value, string key, int fallback = 0) => value.TryGetProperty(key, out var item)
        && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var result) ? result : fallback;
    private static ulong ParseBase(string? value) => ulong.Parse((value ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    private static DateTimeOffset SampledAt(JsonElement value) => DateTimeOffset.TryParse(Text(value, "sampledAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default;
    private bool Fresh(DateTimeOffset at)
    { var now = utcNow(); return at != default && at <= now.AddMilliseconds(100) && now - at <= TimeSpan.FromSeconds(2); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class NavigationRejectedException(string message, bool safeReadCaptureRetry = false) : InvalidOperationException(message)
    { internal bool SafeReadCaptureRetry { get; } = safeReadCaptureRetry; }
}
