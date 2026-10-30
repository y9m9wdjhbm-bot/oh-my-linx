using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Exercises the production HTTP selector and read window against an owned loopback fixture.</summary>
internal static class PatrolCaptureHttpBudgetSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task<string>> action)
        {
            try { checks.Add(new("patrol_capture_http_budget_" + name, true, await action())); }
            catch (Exception error) { checks.Add(new("patrol_capture_http_budget_" + name, false, error.ToString())); }
        }

        await Check("capture_headers_3200ms_within_original_window", async () =>
        {
            // Warm the first-use JIT/serializer/socket path with a separate immediate, owned fixture.
            // Each measured case retains its own single POST and unchanged original read window.
            await using (var warm = new HttpFixture())
            {
                var warmed = await warm.Client.CaptureAsync(default);
                Need(warmed.Source == warm.Source && warm.CapturePosts == 1 && PendingId(warm.Client) is null, "私有即時 HTTP 暖機未閉合。");
                warm.VerifyNoNativeOrServerFailure();
            }
            await using var f = new HttpFixture { HeaderDelay = TimeSpan.FromMilliseconds(3200) };
            var watch = Stopwatch.StartNew();
            PatrolObservation observed;
            try { observed = await f.Client.CaptureAsync(default); }
            catch (Exception error) { throw new InvalidOperationException($"3.2 秒唯讀 HTTP 應在原四秒期限內完成；elapsed={watch.Elapsed.TotalMilliseconds:F0}ms, POST={f.CapturePosts}, trace={f.TraceJson()}", error); }
            Need(watch.Elapsed >= TimeSpan.FromMilliseconds(3100) && watch.Elapsed < TimeSpan.FromSeconds(4), "成功取點未符合原四秒期限。");
            Need(observed.Source == f.Source && observed.SelfId == f.Self && observed.MapId == 4 && observed.X == 101 && observed.Y == 200, "完整來源／角色／座標不符。");
            Need(f.CapturePosts == 1 && f.StatusIds.Length == 0 && PendingId(f.Client) is null, "成功取點重送或未清理自有識別碼。");
            f.VerifyNoNativeOrServerFailure();
            return $"真 HTTP headers 延遲 3200ms，elapsed={watch.Elapsed.TotalMilliseconds:F0}ms，唯一 capture POST；無 LC／導航／原生輸入；trace={f.TraceJson()}。";
        });

        await Check("capture_headers_8000ms_keeps_owned_pending", async () =>
        {
            await using var f = new HttpFixture { HeaderDelay = TimeSpan.FromMilliseconds(8000) };
            var watch = Stopwatch.StartNew();
            await ExpectReadWindowTimeout(() => f.Client.CaptureAsync(default));
                        double elapsed = watch.Elapsed.TotalMilliseconds;
            Need(f.CoreWindowTokenCancelled && !f.CoreUsedThreeSecondTimeout,
                $"必須由原 ReadWindow token 取消，而非三秒 HTTP timer；configured=4000ms, observed={elapsed:F0}ms, POST={f.CapturePosts}, trace={f.TraceJson()}");
            Need(elapsed >= 3600 && elapsed < 6000, $"原四秒token取消的觀察耗時須有界(<6000ms)，configured=4000ms, observed={elapsed:F0}ms, POST={f.CapturePosts}, ownedId={f.LastCaptureId}。");
            string id = PendingId(f.Client) ?? throw new InvalidOperationException("未知 HTTP 完成結果丟失原 capture ID。");
            Need(id == f.LastCaptureId && f.CapturePosts == 1, "未知結果未保留唯一已送識別碼。");
            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await ExpectCallerCancelled(() => f.Client.CaptureAsync(caller.Token), caller.Token);
            Need(f.CapturePosts == 1 && f.StatusIds.Length > 0 && f.StatusIds.All(x => x == id) && PendingId(f.Client) == id,
                "未確認清理前必須只查原 ID，不能送出第二次取點。");
            f.VerifyNoNativeOrServerFailure();
            return $"configured ReadWindow=4000ms，8秒回覆未完成前token取消；observed={elapsed:F0}ms（含排程誤差）；同 ID capture-status Reading，POST 維持 1，pending 保留。";
        });

        await Check("ordinary_status_retains_three_second_http_timer", async () =>
        {
            await using var f = new HttpFixture { HeaderDelay = TimeSpan.FromMilliseconds(3200) };
            var watch = Stopwatch.StartNew();
            await ExpectHttpTimeout(() => f.Client.SendHttpCoreAsync(f.Endpoint, HttpFixture.Secret,
                HttpMethod.Get, "/api/status?jobId=offline-owned", null, default));
            Need(watch.Elapsed >= TimeSpan.FromMilliseconds(2700) && watch.Elapsed < TimeSpan.FromMilliseconds(3200), "一般 status 的原三秒 HTTP 計時器被改動。");
            Need(f.CapturePosts == 0 && f.Requests.Length == 1 && f.Requests[0] == "GET /api/status?jobId=offline-owned", "一般 status 發生額外請求。");
            f.VerifyNoNativeOrServerFailure();
            return $"一般 GET status 延遲 headers，原三秒 HTTP 取消於 {watch.Elapsed.TotalMilliseconds:F0}ms；無 capture 或 native POST。";
        });

        await Check("caller_cancellation_preserves_capture_pending", async () =>
        {
            await using var f = new HttpFixture { HeaderDelay = TimeSpan.FromMilliseconds(8000) };
            using var caller = new CancellationTokenSource();
            var capture = f.Client.CaptureAsync(caller.Token);
            await f.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();
            await ExpectCallerCancelled(() => capture, caller.Token);
            Need(f.CapturePosts == 1 && PendingId(f.Client) == f.LastCaptureId && f.StatusIds.Length == 0,
                "呼叫者取消不能清掉未知結果或重送。");
            f.VerifyNoNativeOrServerFailure();
            return "收到唯一 HTTP capture 後取消呼叫者；原 pending ID 留存，沒有重送／導航／原生輸入。";
        });

        await Check("capture_body_still_uses_original_four_second_window", async () =>
        {
            await using var f = new HttpFixture { BodyDelay = TimeSpan.FromMilliseconds(8000) };
            var watch = Stopwatch.StartNew();
            await ExpectReadWindowTimeout(() => f.Client.CaptureAsync(default));
                        double elapsed = watch.Elapsed.TotalMilliseconds;
            Need(f.CoreWindowTokenCancelled && !f.CoreUsedThreeSecondTimeout,
                $"必須由原 ReadWindow token 取消，而非三秒 HTTP timer；configured=4000ms, observed={elapsed:F0}ms, POST={f.CapturePosts}, trace={f.TraceJson()}");
            Need(elapsed >= 3600 && elapsed < 6000 && f.HeadersSent == 1, $"headers 後的 body 仍須受原四秒 token 管理，configured=4000ms, observed={elapsed:F0}ms（<6000ms觀察上界）。");
            Need(f.CapturePosts == 1 && PendingId(f.Client) == f.LastCaptureId, "body 超時未保留原 capture ID。");
            f.VerifyNoNativeOrServerFailure();
            return $"headers 已送出，body 延遲 8000ms；configured ReadWindow=4000ms，observed={elapsed:F0}ms（含排程誤差），原 pending 保留。";
        });

        await Check("get_capture_does_not_select_capture_post_client", async () =>
        {
            await using var f = new HttpFixture { HeaderDelay = TimeSpan.FromMilliseconds(3200) };
            var watch = Stopwatch.StartNew();
            await ExpectHttpTimeout(() => f.Client.SendHttpCoreAsync(f.Endpoint, HttpFixture.Secret,
                HttpMethod.Get, "/api/capture", null, default));
            Need(watch.Elapsed >= TimeSpan.FromMilliseconds(2700) && watch.Elapsed < TimeSpan.FromMilliseconds(3200), "非精確 POST capture 被放進 capture 專用 client。");
            Need(f.CapturePosts == 0 && f.Requests.SequenceEqual(["GET /api/capture"]), "非 POST case 發生額外請求。");
            f.VerifyNoNativeOrServerFailure();
            return $"GET /api/capture 保持一般三秒計時器，取消於 {watch.Elapsed.TotalMilliseconds:F0}ms；selector 限精確 POST。";
        });
        return checks;
    }

    internal static async Task<int> RunReportAsync(string path)
    {
        var checks = await RunAsync();
        var report = new
        {
            Version = "83.0.0", Success = checks.Count == 6 && checks.All(x => x.Passed),
            Passed = checks.Count(x => x.Passed), Failed = checks.Count(x => !x.Passed),
            HttpOnlyLoopback = true, OpenedGameProcess = false, ConnectedToService = false, CreatedWindow = false,
            LcReadCalls = 0, NativeCalls = 0, GameInputs = 0, FocusChanges = 0, Checks = checks
        };
        string output = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string json = JsonSerializer.Serialize(report, HubProtocol.Json);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        await writer.WriteLineAsync(json); await writer.FlushAsync(); file.Flush(true);
        Console.WriteLine(json);
        return report.Success ? 0 : 1;
    }

    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string? PendingId(PatrolNavigationClient client)
    {
        object? pending = typeof(PatrolNavigationClient).GetField("pendingCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
        return pending?.GetType().GetProperty("Id")?.GetValue(pending) as string;
    }
    private static async Task ExpectReadWindowTimeout(Func<Task> action)
    {
        try { await action(); }
        catch (PatrolSourceReadException error) when (error.Code == "SourceUnavailableTimeout") { return; }
        throw new InvalidOperationException("未取得原 ReadWindow 的 SourceUnavailableTimeout。");
    }
    private static async Task ExpectCallerCancelled(Func<Task> action, CancellationToken token)
    {
        try { await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        throw new InvalidOperationException("呼叫者取消未保留為取消結果。");
    }
    private static async Task ExpectHttpTimeout(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException error) when (error.InnerException is TimeoutException) { return; }
        throw new InvalidOperationException("未取得既有三秒 HttpClient timeout。");
    }

    private sealed class HttpFixture : IAsyncDisposable
    {
        internal const string Secret = "offline-private-http-budget-fixture-token";
        internal readonly SourceIdentity Source = new(123, 133000000000000000, @"C:\offline\LC.exe", "0x140000000", 1, 500000, new string('A', 64), "offline-http-budget-session");
        internal ulong Self => 9007199254740993;
        internal readonly TaskCompletionSource FirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly PatrolNavigationClient Client;
        internal readonly Uri Endpoint;
        internal TimeSpan HeaderDelay, BodyDelay;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task accept;
        private readonly List<Task> handlers = [];
        private readonly List<string> requests = [], statusIds = [], captureIds = [], serverFailures = [];
        private int headersSent;
        private DateTimeOffset sampledClock = DateTimeOffset.UtcNow;
        private DateTimeOffset? postReceivedAt, receiptCreatedAt, headersSentAt, coreBeganAt, coreFinishedAt;
        private Exception? coreError;
        private DateTimeOffset? coreTokenCancelledAt;
        private bool coreWindowTokenCancelled;
        internal bool CoreWindowTokenCancelled { get { lock (requests) return coreWindowTokenCancelled; } }
        internal bool CoreUsedThreeSecondTimeout
        {
            get
            {
                lock (requests) for (Exception? error = coreError; error is not null; error = error.InnerException)
                    if (error is TimeoutException || error.Message.Contains("configured HttpClient.Timeout", StringComparison.Ordinal)) return true;
                return false;
            }
        }
        private readonly List<PatrolSourceWaitNotice> notices = [];
        internal int HeadersSent => Volatile.Read(ref headersSent);
        internal int CapturePosts { get { lock (requests) return captureIds.Count; } }
        internal string LastCaptureId { get { lock (requests) return captureIds.LastOrDefault() ?? ""; } }
        internal string[] Requests { get { lock (requests) return requests.ToArray(); } }
        internal string[] StatusIds { get { lock (requests) return statusIds.ToArray(); } }

        internal HttpFixture()
        {
            listener.Start(); Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            Client = new(() => Snapshot(), BridgeAsync, Clock, Task.Delay,
                notice => { lock (requests) notices.Add(notice); });
            accept = AcceptAsync();
        }
        private async Task<PatrolNavigationReply> BridgeAsync(HttpMethod method, string path, object? payload, CancellationToken token)
        {
            bool capture = method == HttpMethod.Post && path == "/api/capture";
            if (capture) lock (requests) coreBeganAt = DateTimeOffset.UtcNow;
            using var registration = capture ? token.Register(() =>
                { lock (requests) { coreTokenCancelledAt = DateTimeOffset.UtcNow; coreWindowTokenCancelled = true; } }) : default;
            try { return await Client.SendHttpCoreAsync(Endpoint, Secret, method, path, payload, token); }
            catch (Exception error) { if (capture) lock (requests) coreError = error; throw; }
            finally { if (capture) lock (requests) { coreFinishedAt = DateTimeOffset.UtcNow; coreWindowTokenCancelled |= token.IsCancellationRequested; } }
        }
        internal string TraceJson()
        {
            lock (requests) return JsonSerializer.Serialize(new { postReceivedAt, receiptCreatedAt, headersSentAt, coreBeganAt, coreFinishedAt,
                coreError = coreError?.ToString(), coreTokenCancelledAt, coreWindowTokenCancelled, configuredReadWindowMilliseconds = 4000, HeadersSent, capturePosts = captureIds.Count, requests, notices }, HubProtocol.Json);
        }
        // Each production clock read uses real wall time; the fake sample is complete at that read.
        private DateTimeOffset Clock() => sampledClock = DateTimeOffset.UtcNow;
        private HubSnapshot Snapshot()
        {
            var now = sampledClock;
            return new() { Source = Source, State = "Reading", PublishedAt = now, Fields = new()
            {
                ["selfId"] = FieldReading.Observed(Self, now),
                ["position"] = FieldReading.Observed(new { x = 100, y = 200 }, now)
            }};
        }
        private object Wire() => new { pid = Source.ProcessId, creationFileTime = Source.CreationFileTime.ToString(CultureInfo.InvariantCulture),
            imagePath = Source.ImagePath, sha256 = Source.DiskSha256, imageBase = Source.ImageBase, imageSize = Source.ImageSize,
            selfId = Self.ToString(CultureInfo.InvariantCulture) };
        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var peer = await listener.AcceptTcpClientAsync(stop.Token);
                    var handler = ServeAsync(peer); lock (handlers) handlers.Add(handler);
                }
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task ServeAsync(TcpClient peer)
        {
            using (peer)
            {
                try
                {
                    using var stream = peer.GetStream();
                    using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
                    string line = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("Missing HTTP request line.");
                    Need(line.Length <= 2048, "Fixture request line too large.");
                    string[] request = line.Split(' '); Need(request.Length == 3, "Fixture request line invalid.");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; ; i++)
                    {
                        Need(i < 64, "Fixture header count too large.");
                        string header = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("Incomplete HTTP headers.");
                        if (header.Length == 0) break;
                        int colon = header.IndexOf(':'); Need(colon > 0 && header.Length <= 4096, "Invalid HTTP header.");
                        headers.Add(header[..colon], header[(colon + 1)..].Trim());
                    }
                    Need(headers.GetValueOrDefault("X-MapNav-Token") == Secret && headers.GetValueOrDefault("Origin") == Endpoint.GetLeftPart(UriPartial.Authority), "Fixture authentication differs from production headers.");
                    int length = int.Parse(headers.GetValueOrDefault("Content-Length", "0"), CultureInfo.InvariantCulture);
                    Need(length is >= 0 and <= 32768, "Fixture HTTP body bounds.");
                    char[] chars = new char[length]; int offset = 0;
                    while (offset < length)
                    {
                        int got = await reader.ReadAsync(chars.AsMemory(offset), stop.Token); Need(got > 0, "Incomplete fixture request body."); offset += got;
                    }
                    string method = request[0], path = request[1], id = "";
                    if (method == "POST" && path == "/api/capture")
                    {
                        using var payload = JsonDocument.Parse(new string(chars));
                        id = payload.RootElement.GetProperty("captureId").GetString()!;
                        Need(id.Length == 32 && PatrolNavigationClient.SourceMatches(payload.RootElement.GetProperty("expectedSource"), Source, Self), "Fixture capture ownership differs.");
                    }
                    lock (requests)
                    {
                        requests.Add(method + " " + path);
                        if (method == "POST" && path == "/api/capture") { captureIds.Add(id); postReceivedAt = DateTimeOffset.UtcNow; }
                        if (method == "GET" && path.StartsWith("/api/capture-status?captureId=", StringComparison.Ordinal)) statusIds.Add(path[(path.IndexOf('=') + 1)..]);
                    }
                    FirstRequest.TrySetResult();
                    bool isCaptureStatus = method == "GET" && path.StartsWith("/api/capture-status?captureId=", StringComparison.Ordinal);
                    if (!isCaptureStatus) await Task.Delay(HeaderDelay, stop.Token);
                    object body = isCaptureStatus
                        ? new { ok = true, captureId = path[(path.IndexOf('=') + 1)..], readOnly = true, inputSent = false,
                            terminal = false, cleanupConfirmed = false, accepted = true, state = "Reading", error = "", expectedSource = Wire() }
                        : method == "POST" && path == "/api/capture"
                        ? new { ok = true, captureId = id, readOnly = true, inputSent = false, terminal = true,
                            cleanupConfirmed = true, accepted = true, state = "Completed", error = "", expectedSource = Wire(), source = Wire(),
                            mapId = 4, x = 101, y = 200, standing = true, sampledAtUtc = DateTimeOffset.UtcNow.ToString("O") }
                        : new { ok = true };
                    if (method == "POST" && path == "/api/capture") lock (requests) receiptCreatedAt = DateTimeOffset.UtcNow;
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, HubProtocol.Json);
                    byte[] responseHeaders = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(responseHeaders, stop.Token); await stream.FlushAsync(stop.Token);
                    if (method == "POST" && path == "/api/capture") lock (requests) headersSentAt = DateTimeOffset.UtcNow;
                    Interlocked.Increment(ref headersSent);
                    await Task.Delay(BodyDelay, stop.Token);
                    await stream.WriteAsync(bytes, stop.Token); await stream.FlushAsync(stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException && stop.IsCancellationRequested || error is IOException or SocketException) { }
                catch (Exception error) { lock (requests) serverFailures.Add(error.ToString()); }
            }
        }
        internal void VerifyNoNativeOrServerFailure()
        {
            lock (requests)
            {
                Need(serverFailures.Count == 0, "Fixture server failed: " + string.Join("\n", serverFailures));
                Need(requests.All(x => x.StartsWith("POST /api/capture", StringComparison.Ordinal)
                    || x.StartsWith("GET /api/capture", StringComparison.Ordinal) || x.StartsWith("GET /api/status?", StringComparison.Ordinal)),
                    "Fixture observed a navigation/heartbeat/stop/native request.");
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            await accept;
            Task[] tasks; lock (handlers) tasks = handlers.ToArray();
            await Task.WhenAll(tasks);
            await Client.DisposeAsync(); stop.Dispose();
        }
    }
}
