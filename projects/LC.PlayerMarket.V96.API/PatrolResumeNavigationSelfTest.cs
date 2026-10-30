using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Production navigation parser and resumable controller, using fake transport only.</summary>
internal static class PatrolResumeNavigationSelfTest
{
    private static readonly DateTimeOffset InitialTime = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(123, 456, "offline-LC.exe", "0x140000000", 1, 65536, new string('A', 64), "resume-navigation-session");
    private const ulong SelfId = 9007199254740993;

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try
            {
                await action().WaitAsync(TimeSpan.FromSeconds(15));
                checks.Add(new(name, true, "純離線；實際導航解析器／恢復協調器，沒有子程序、HTTP 連線、LC 存取或輸入。"));
            }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }

        await Check("resume_navigation_force_status_stale_completed_is_metadata_only", async () =>
        {
            await using var f = new ParserFixture(force: true);
            await f.Client.StartAsync(f.Request, default);
            f.Stale = true;
            var state = await f.Client.GetStateAsync(f.Request.JobId, default);
            Need(state.Completed && state.Terminal && state.WorkSettled && state.CleanupConfirmed, "舊完成紀錄無法提供已清理 metadata。");
            Need(!state.PositionVerified && f.Client.DisplaySnapshot is null, "過期完成座標被當成即時到點或 HUD 座標。");
            Need(f.Calls.SequenceEqual(["POST /api/navigate", "GET /api/status?jobId=" + f.Request.JobId]) && f.Reads == 0,
                "force狀態讀取仍先等市場來源或額外輪詢。");
        });
        await Check("resume_navigation_strict_status_stale_completed_still_rejected", async () =>
        {
            await using var f = new ParserFixture(force: false);
            await f.Client.StartAsync(f.Request, default);
            f.Stale = true;
            bool rejected = false;
            try { await f.Client.GetStateAsync(f.Request.JobId, default); }
            catch (PatrolSourceReadException error) when (error.Code == "NavigationSampleStale") { rejected = true; }
            Need(rejected && f.Calls.Count(c => c == "POST /api/navigate") == 1
                && f.Calls.Count(c => c.StartsWith("GET /api/status?", StringComparison.Ordinal)) == 3,
                "strict 過期完成沒有維持拒絕／原工作有界重讀語意。");
        });
        await Check("resume_navigation_force_start_stale_completed_still_rejected", async () =>
        {
            await using var f = new ParserFixture(force: true) { Stale = true, CompleteStart = true };
            await Rejected(() => f.Client.StartAsync(f.Request, default));
            Need(f.Calls.SequenceEqual(["POST /api/navigate"]) && f.Client.DisplaySnapshot is null,
                "force Start 的過期完成被採用，或拒絕後又重送。");
        });
        foreach (string kind in new[] { "job", "source", "self", "target", "radius" })
            await Check("resume_navigation_stale_metadata_rejects_" + kind + "_mismatch", async () =>
            {
                await using var f = new ParserFixture(force: true);
                await f.Client.StartAsync(f.Request, default);
                f.Stale = true; f.Mismatch = kind;
                await Rejected(() => f.Client.GetStateAsync(f.Request.JobId, default));
                Need(f.Calls.Count == 2 && f.Calls.Count(c => c == "POST /api/navigate") == 1,
                    "過期 metadata 失去工作、完整來源、角色、目標或容差的約束。");
            });

        await Check("resume_navigation_completed_stale_requires_fresh_same_source_capture_before_search", async () =>
        {
            await using var f = new ControllerFixture();
            var result = await f.RunAsync();
            Need(result.State == "Completed" && result.CleanupConfirmed && f.Market.Scans == 1, result.Detail);
            int metadata = f.Events.IndexOf("completed-metadata-1"), capture = f.Events.IndexOf("fresh-capture-after-metadata"), refresh = f.Events.IndexOf("refresh");
            Need(metadata >= 0 && capture > metadata && refresh > capture && f.Starts == 1,
                "搜尋沒有依過期完成之後重新核對同來源的新座標，或重送原導航。");
        });
        await Check("resume_navigation_completed_stale_outside_revisits_same_point_before_search", async () =>
        {
            await using var f = new ControllerFixture { OutsideFirst = true };
            var result = await f.RunAsync();
            Need(result.State == "Completed" && f.Starts == 2 && f.Market.Scans == 1, result.Detail);
            int next = f.Events.IndexOf("navigate-2"), refresh = f.Events.IndexOf("refresh");
            Need(next > f.Events.IndexOf("fresh-capture-after-metadata") && refresh > next
                && f.StartedPoints.Distinct().Count() == 1 && f.StartedJobs.Distinct().Count() == 2,
                "範圍外使用舊完成座標搜尋，或改到其他定位點／重用原工作 ID。");
        });
        await Check("resume_navigation_outside_manual_stop_during_cooldown_does_not_search_or_restart", async () =>
        {
            using var stop = new CancellationTokenSource();
            await using var f = new ControllerFixture { OutsideFirst = true, StopOnCooldown = stop };
            var result = await f.RunAsync(stop.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.Starts == 1 && f.Market.Refreshes == 0 && f.Market.Scans == 0,
                "範圍外冷卻中手動停止仍搜尋或自行重啟。");
        });
        foreach (string kind in new[] { "source", "self", "map" })
            await Check("resume_navigation_fresh_capture_changed_" + kind + "_does_not_rebind", async () =>
            {
                await using var f = new ControllerFixture { ChangeAfterMetadata = kind };
                var result = await f.RunAsync();
                Need(result.State != "Completed" && f.Starts == 1 && f.Market.Refreshes == 0 && f.Market.Scans == 0,
                    "自動恢復遇到來源、角色或地圖變更仍改綁新來源或搜尋。");
            });
        return checks.ToArray();
    }

    private static void Need(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }
    private static async Task Rejected(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("預期拒絕，但解析器接受了回覆。");
    }
    private static object Wire(SourceIdentity? source = null, ulong selfId = SelfId)
    {
        source ??= Source;
        return new { pid = source.ProcessId, creationFileTime = source.CreationFileTime.ToString(CultureInfo.InvariantCulture),
            imagePath = source.ImagePath, sha256 = source.DiskSha256, imageBase = source.ImageBase,
            imageSize = source.ImageSize, selfId = selfId.ToString(CultureInfo.InvariantCulture) };
    }
    private static PatrolNavigationReply Reply(object body) => new(HttpStatusCode.OK, JsonSerializer.SerializeToElement(body));
    private static HubSnapshot Snapshot(DateTimeOffset now, SourceIdentity? source = null, ulong selfId = SelfId, int x = 100) => new()
    {
        Source = source ?? Source, State = "Reading", PublishedAt = now,
        Fields = new() { ["selfId"] = FieldReading.Observed(selfId, now), ["position"] = FieldReading.Observed(new { x, y = 200 }, now) }
    };
    private static Dictionary<string, object> NavigationBody(PatrolNavigationRequest request, DateTimeOffset sampled, bool complete, int x) => new()
    {
        ["ok"] = true, ["jobId"] = request.JobId, ["source"] = Wire(request.Expected.Source, request.Expected.SelfId),
        ["arrivalRadius"] = request.ArrivalRadius, ["waypoint"] = new { mapId = 4, x = request.Waypoint.X, y = request.Waypoint.Y },
        ["state"] = complete ? "Completed" : "Moving", ["terminal"] = complete, ["completed"] = complete, ["cleanupConfirmed"] = complete,
        ["standing"] = complete, ["standingSamples"] = complete ? 2 : 0, ["sameMap"] = true, ["mapId"] = 4,
        ["x"] = x, ["y"] = 200, ["distance"] = Math.Abs(x - request.Waypoint.X), ["completionId"] = "offline-completion",
        ["sampledAtUtc"] = sampled.ToString("O"), ["stopRequested"] = false, ["userStop"] = false, ["detail"] = ""
    };

    private sealed class ParserFixture : IAsyncDisposable
    {
        internal readonly List<string> Calls = [];
        internal readonly PatrolNavigationRequest Request = new("offline-original-job", new(Source, SelfId, 4, 100, 200, InitialTime, true),
            new("04c780a53157440aabdfc9bcb3f95e7d", "離線定位點", "4", "離線地圖", 100, 200), 2);
        internal readonly PatrolNavigationClient Client;
        internal bool Stale, CompleteStart;
        internal string? Mismatch;
        internal int Reads;
        private DateTimeOffset now = InitialTime;
        internal ParserFixture(bool force)
        {
            Client = new(() => { Reads++; return Snapshot(now); }, (method, path, payload, token) =>
            {
                token.ThrowIfCancellationRequested(); Calls.Add(method.Method + " " + path);
                bool complete = path != "/api/navigate" || CompleteStart;
                var body = NavigationBody(Request, Stale ? InitialTime.AddSeconds(-10) : now, complete, 100);
                if (Mismatch == "job") body["jobId"] = "foreign-job";
                if (Mismatch == "source") body["source"] = Wire(Source with { CreationFileTime = Source.CreationFileTime + 1 });
                if (Mismatch == "self") body["source"] = Wire(selfId: SelfId + 1);
                if (Mismatch == "target") body["waypoint"] = new { mapId = 4, x = 101, y = 200 };
                if (Mismatch == "radius") body["arrivalRadius"] = 3;
                return Task.FromResult(Reply(body));
            }, () => now, (amount, token) => { token.ThrowIfCancellationRequested(); now += amount; return Task.CompletedTask; },
                readOnly: false, forceDispatch: force);
        }
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class ControllerFixture : IAsyncDisposable
    {
        internal readonly List<string> Events = [], StartedPoints = [], StartedJobs = [];
        internal readonly FakeMarket Market;
        internal readonly PatrolNavigationClient Client;
        internal readonly PatrolController Controller;
        internal bool OutsideFirst;
        internal string? ChangeAfterMetadata;
        internal CancellationTokenSource? StopOnCooldown;
        internal int Starts;
        private DateTimeOffset now = InitialTime;
        private int actualX = 90;
        private bool metadata;
        private readonly Dictionary<string, PatrolNavigationRequest> requests = new(StringComparer.Ordinal);
        private readonly PatrolRouteSnapshot route = new()
        {
            Revision = "fed22de4e95d43a1b5c03b3f31d57770", SavedAt = InitialTime, RetryLimit = 2, ArrivalRadius = 2, RoundCount = 1,
            Points = [new("04c780a53157440aabdfc9bcb3f95e7d", "離線定位點", "4", "離線地圖", 100, 200)]
        };
        private SourceIdentity CurrentSource => metadata && ChangeAfterMetadata == "source"
            ? Source with { CreationFileTime = Source.CreationFileTime + 1, SessionKey = "changed-session" } : Source;
        private ulong CurrentSelf => metadata && ChangeAfterMetadata == "self" ? SelfId + 1 : SelfId;

        internal ControllerFixture()
        {
            Market = new(Events, () => now);
            Client = new(() => Snapshot(now, CurrentSource, CurrentSelf, actualX), SendAsync, () => now, Delay,
                readOnly: false, forceDispatch: true);
            Controller = new(Client, Market, () => now, Delay, TimeSpan.FromMilliseconds(500), forceDispatch: true, autoResume: true);
        }
        internal Task<PatrolRunResult> RunAsync(CancellationToken token = default) => Controller.RunAsync(route, token);
        private Task Delay(TimeSpan amount, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); now += amount;
            if (amount >= TimeSpan.FromSeconds(1)) { Events.Add("cooldown"); StopOnCooldown?.Cancel(); }
            token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }
        private Task<PatrolNavigationReply> SendAsync(HttpMethod method, string path, object? payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var data = JsonSerializer.SerializeToElement(payload);
            if (method == HttpMethod.Post && path == "/api/capture")
            {
                Events.Add(metadata ? "fresh-capture-after-metadata" : "fresh-capture");
                return Task.FromResult(Reply(new
                {
                    ok = true, captureId = data.GetProperty("captureId").GetString(), expectedSource = data.GetProperty("expectedSource"),
                    source = Wire(CurrentSource, CurrentSelf), readOnly = true, inputSent = false, accepted = true,
                    terminal = true, cleanupConfirmed = true, state = "Completed", error = "",
                    mapId = metadata && ChangeAfterMetadata == "map" ? 5 : 4, x = actualX, y = 200,
                    standing = true, sampledAtUtc = now.ToString("O")
                }));
            }
            if (method == HttpMethod.Post && path == "/api/navigate")
            {
                Need(data.GetProperty("forceDispatch").GetBoolean(), "導航缺少 forceDispatch。");
                Starts++; Events.Add("navigate-" + Starts);
                string id = data.GetProperty("jobId").GetString()!;
                var request = new PatrolNavigationRequest(id, new(Source, SelfId, 4, actualX, 200, now, false), route.Points[0], 2);
                requests.Add(id, request); StartedJobs.Add(id); StartedPoints.Add(request.Waypoint.PointId);
                if (Starts > 1) actualX = 100;
                return Task.FromResult(Reply(NavigationBody(request, now, complete: false, actualX)));
            }
            if (method == HttpMethod.Get && path.StartsWith("/api/status?jobId=", StringComparison.Ordinal))
            {
                string id = Uri.UnescapeDataString(path[(path.IndexOf('=') + 1)..]);
                Need(requests.ContainsKey(id), "狀態讀取接管其他導航工作。");
                metadata = true; Events.Add("completed-metadata-" + Starts);
                actualX = OutsideFirst && Starts == 1 ? 90 : 100;
                return Task.FromResult(Reply(NavigationBody(requests[id], now.AddSeconds(-10), complete: true, 100)));
            }
            if (method == HttpMethod.Post && path == "/api/stop")
            {
                string id = data.GetProperty("jobId").GetString()!;
                Need(requests.ContainsKey(id), "停止接管其他導航工作。"); Events.Add("stop-owned-navigation");
                return Task.FromResult(Reply(NavigationBody(requests[id], now.AddSeconds(-10), complete: true, 100)));
            }
            throw new InvalidOperationException("測試不允許的 transport 呼叫：" + method + " " + path);
        }
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class FakeMarket(List<string> events, Func<DateTimeOffset> clock) : IPatrolMarket
    {
        internal int Refreshes, Scans;
        private ShopCommandResponse current = new() { CurrentSourceSessionKey = Source.SessionKey, WorkSettled = true };
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Need(request.ExpectedSourceSessionKey == Source.SessionKey, "商店請求改綁其他來源。");
            if (request.Command == ShopCommands.RefreshTargets)
            {
                Need(request.ForceDispatch, "刷新未直接下達。"); Refreshes++; events.Add("refresh");
                current = current with
                {
                    RequestId = request.RequestId, State = "TargetsRefreshed",
                    TargetSnapshot = new() { SnapshotId = "resume-navigation-snapshot", SourceSessionKey = Source.SessionKey,
                        SelfId = SelfId, RefreshedAt = clock(), CanDispatch = false }
                };
            }
            else if (request.Command == ShopCommands.ScanVisible)
            {
                Need(request.ForceDispatch && request.TargetSnapshotId == current.TargetSnapshot?.SnapshotId, "搜尋未直接使用本點刷新名單。");
                Scans++; events.Add("scan");
                current = current with
                {
                    RequestId = request.RequestId, State = "ScanStarted", WorkSettled = true,
                    Scan = new() { JobId = request.RequestId, SourceSessionKey = Source.SessionKey,
                        TargetSnapshotId = request.TargetSnapshotId, Running = false },
                    SavedScan = new() { PlanId = "resume-navigation-plan", SourceSessionKey = Source.SessionKey,
                        TotalPages = 1, SearchedPages = 1, ConfirmedPages = 1 }
                };
            }
            else if (request.Command == ShopCommands.GetState) current = current with { RequestId = request.RequestId };
            else throw new InvalidOperationException("測試不允許的商店指令：" + request.Command);
            return Task.FromResult(current);
        }
    }
}
