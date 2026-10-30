using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Production capture protocol with fake transport only; no LC, HTTP, child process or native input.</summary>
internal static class PatrolCaptureReadIncompleteVerification
{
    private const string RecoveryCode = "NavigationCaptureReadIncomplete";

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try
            {
                await action();
                checks.Add(new("capture_read_incomplete_v71_" + name, true,
                    "實際 Client／假 transport；精確分類與歸屬驗證，無 LC、HTTP、子程序或原生輸入。"));
            }
            catch (Exception error) { checks.Add(new("capture_read_incomplete_v71_" + name, false, error.ToString())); }
        }

        foreach (bool force in new[] { false, true })
        {
            await Check("post_exact_ends_call_without_fast_retry_force_" + force, async () =>
            {
                await using var f = new Fixture(force);
                await ExpectTyped(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.PostIds.Count == 1 && f.StatusIds.Count == 0 && f.PendingId is null,
                    "完整 POST 終態未結清，或當次自動重送。");
                Need(f.Time.Notices.Count(n => n.Code == "CAPTURE_READ_RECOVERY_READY") == 1,
                    "精確 READ_INCOMPLETE 未提供可冷卻復原的分類通知。");
                f.AssertNoFastRetryOrNative();
                f.Failed = false;
                var fresh = await f.Client.CaptureAsync(default, f.Expected);
                Need(fresh.X == 101 && f.PostIds.Count == 2 && f.PostIds.Distinct().Count() == 2
                    && f.StatusIds.Count == 0 && f.PendingId is null, "下一次呼叫未使用新的 capture ID。");
                f.AssertNoFastRetryOrNative();
            });

            await Check("status_exact_settles_old_id_then_ends_call_force_" + force, async () =>
            {
                await using var f = new Fixture(force) { TimeoutFirst = true };
                await ExpectTimeout(() => f.Client.CaptureAsync(default, f.Expected));
                string original = f.PendingId ?? throw new InvalidOperationException("逾時丟失原 capture ID。");
                await ExpectTyped(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.PostIds.Count == 1 && f.StatusIds.SequenceEqual([original]) && f.PendingId is null,
                    "GET 結清沒有只查原 ID，或在同一次呼叫新增 POST。");
                f.AssertNoFastRetryOrNative();
                f.Failed = false;
                Need((await f.Client.CaptureAsync(default, f.Expected)).X == 101
                    && f.PostIds.Count == 2 && f.PostIds.Distinct().Count() == 2
                    && f.StatusIds.Count == 1, "後續明確呼叫未以新 ID 取樣。");
                f.AssertNoFastRetryOrNative();
            });
        }

        // A complete generic failure can retire its ID, but incomplete or inexact
        // classification must never become a recoverable source-read exception.
        var classifications = new (string Name, bool ViaStatus, Action<Dictionary<string, object?>> Mutate)[]
        {
            ("generic", false, d => { d.Remove("sourceError"); d.Remove("sourcePhase"); }),
            ("missing_error_metadata", false, d => d.Remove("sourceError")),
            ("missing_phase_metadata", true, d => d.Remove("sourcePhase")),
            ("error_metadata_number", false, d => d["sourceError"] = 1),
            ("phase_metadata_boolean", true, d => d["sourcePhase"] = true),
            ("different_error", false, d => d["sourceError"] = "READ_REGION_UNAVAILABLE"),
            ("different_phase", true, d => d["sourcePhase"] = "context.bind"),
            ("error_case_mismatch", true, d => d["sourceError"] = "read_incomplete"),
            ("phase_case_mismatch", false, d => d["sourcePhase"] = "Factory.Context")
        };
        foreach (var variant in classifications)
            await Check("classification_" + variant.Name + "_remains_hard_failure", async () =>
            {
                await using var f = new Fixture(true) { TimeoutFirst = variant.ViaStatus, Mutate = variant.Mutate };
                if (variant.ViaStatus) await ExpectTimeout(() => f.Client.CaptureAsync(default, f.Expected));
                await ExpectHardFailure(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.PendingId is null && f.PostIds.Count == 1 && f.StatusIds.Count == (variant.ViaStatus ? 1 : 0),
                    "完整 generic failure 結清語意改變或發生額外 POST。");
                Need(!f.Time.Notices.Any(n => n.Code == "CAPTURE_READ_RECOVERY_READY"),
                    "缺漏／錯誤分類 metadata 被當成可恢復來源失敗。");
                f.AssertNoFastRetryOrNative();
            });

        // Representative ownership/receipt regressions. The complete earlier
        // receipt mutation matrix remains in PatrolNavigationRecoverySelfTest.
        var receipts = new (string Name, Action<Dictionary<string, object?>> Mutate)[]
        {
            ("foreign_owner", d => d["captureId"] = new string('f', 32)),
            ("foreign_source", d => d["expectedSource"] = new { pid = 999 }),
            ("not_readonly", d => d["readOnly"] = false),
            ("input_sent", d => d["inputSent"] = true),
            ("not_terminal", d => d["terminal"] = false),
            ("not_cleaned", d => d["cleanupConfirmed"] = false),
            ("not_accepted", d => d["accepted"] = false),
            ("wrong_state", d => d["state"] = "Completed")
        };
        foreach (bool viaStatus in new[] { false, true })
            foreach (var variant in receipts)
                await Check((viaStatus ? "status_" : "post_") + variant.Name + "_retains_owned_id", async () =>
                {
                    await using var f = new Fixture(true) { TimeoutFirst = viaStatus, Mutate = variant.Mutate };
                    if (viaStatus) await ExpectTimeout(() => f.Client.CaptureAsync(default, f.Expected));
                    await ExpectHardFailure(() => f.Client.CaptureAsync(default, f.Expected));
                    string owned = f.PostIds.Single();
                    Need(f.PendingId == owned, "不完整收據清除了原 ownership。");
                    await ExpectHardFailure(() => f.Client.CaptureAsync(default, f.Expected));
                    Need(f.PostIds.Count == 1 && f.PendingId == owned
                        && f.StatusIds.Count == (viaStatus ? 2 : 1) && f.StatusIds.All(id => id == owned),
                        "收據拒絕後重送或改查其他 capture ID。");
                    Need(!f.Time.Notices.Any(n => n.Code == "CAPTURE_READ_RECOVERY_READY"),
                        "歸屬／清理證據未閉合卻解鎖冷卻復原。");
                    f.AssertNoFastRetryOrNative();
                });

        var envelopes = new (string Name, bool ViaStatus, HttpStatusCode Status, object? Ok)[]
        {
            ("post_http500", false, HttpStatusCode.InternalServerError, false),
            ("post_http200", false, HttpStatusCode.OK, false),
            ("post_ok_true", false, HttpStatusCode.Conflict, true),
            ("post_ok_string", false, HttpStatusCode.Conflict, "false"),
            ("status_http409", true, HttpStatusCode.Conflict, true),
            ("status_ok_false", true, HttpStatusCode.OK, false),
            ("status_ok_string", true, HttpStatusCode.OK, "true")
        };
        foreach (var variant in envelopes)
            await Check(variant.Name + "_cannot_retire_or_classify", async () =>
            {
                await using var f = new Fixture(true) { TimeoutFirst = variant.ViaStatus };
                if (variant.ViaStatus)
                {
                    f.StatusCode = variant.Status;
                    f.StatusOk = variant.Ok;
                    await ExpectTimeout(() => f.Client.CaptureAsync(default, f.Expected));
                }
                else { f.PostCode = variant.Status; f.PostOk = variant.Ok; }
                await ExpectHardFailure(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.PendingId == f.PostIds.Single() && f.StatusIds.Count == (variant.ViaStatus ? 1 : 0),
                    "無效 HTTP／ok envelope 提前釋放原 ID。");
                Need(!f.Time.Notices.Any(n => n.Code == "CAPTURE_READ_RECOVERY_READY"), "無效 envelope 被分類為可恢復。");
                f.AssertNoFastRetryOrNative();
            });

        await Check("precancel_has_no_capture_or_source_read", async () =>
        {
            await using var f = new Fixture(true); using var stop = new CancellationTokenSource(); stop.Cancel();
            await ExpectCancelled(() => f.Client.CaptureAsync(stop.Token, f.Expected), stop.Token);
            Need(f.PostIds.Count == 0 && f.StatusIds.Count == 0 && f.Reads == 0 && f.PendingId is null,
                "預先取消仍建立 capture／讀取來源。");
            f.AssertNoFastRetryOrNative();
        });
        foreach (bool viaStatus in new[] { false, true })
            await Check((viaStatus ? "status_" : "post_") + "caller_cancel_retains_pending_without_retry", async () =>
            {
                await using var f = new Fixture(true) { TimeoutFirst = viaStatus };
                if (viaStatus) await ExpectTimeout(() => f.Client.CaptureAsync(default, f.Expected));
                using var stop = new CancellationTokenSource();
                f.BeforeReply = stop.Cancel;
                await ExpectCancelled(() => f.Client.CaptureAsync(stop.Token, f.Expected), stop.Token);
                Need(f.PostIds.Count == 1 && f.PendingId == f.PostIds.Single()
                    && f.StatusIds.Count == (viaStatus ? 1 : 0), "取消期間提前清除 ownership 或重送。");
                await ExpectCancelled(() => f.Client.CaptureAsync(stop.Token, f.Expected), stop.Token);
                Need(f.PostIds.Count == 1 && f.StatusIds.Count == (viaStatus ? 1 : 0), "已取消 token 仍重試。");
                Need(!f.Time.Notices.Any(n => n.Code == "CAPTURE_READ_RECOVERY_READY"), "取消被轉成可接續錯誤。");
                f.AssertNoFastRetryOrNative();
            });

        await Check("force_false_controller_does_not_auto_resume_typed_failure", async () =>
        {
            await using var f = new Fixture(false); var market = new NoMarket();
            var controller = new PatrolController(f.Client, market, () => f.Time.Now, f.Time.Delay,
                TimeSpan.FromMilliseconds(100), forceDispatch: false, autoResume: true);
            var route = new PatrolRouteSnapshot { Revision = Guid.NewGuid().ToString("N"), SavedAt = f.Time.Now,
                RoundCount = 1, RetryLimit = 2, ArrivalRadius = 1,
                Points = [new(Guid.NewGuid().ToString("N"), "離線分類驗證", "4", "離線圖", 101, 200)] };
            var result = await controller.RunAsync(route);
            Need(result.State == "Failed" && result.CleanupConfirmed && result.CompletedPoints == 0
                && result.RetriesUsed == 0 && f.PostIds.Count == 1 && f.StatusIds.Count == 0 && market.Calls == 0,
                "非 force 模式被新分類帶入自動接續：" + result.Detail);
            f.AssertNoFastRetryOrNative();
        });
        return checks.ToArray();
    }

    private static void Need(bool ok, string detail)
    { if (!ok) throw new InvalidOperationException(detail); }
    private static async Task ExpectTyped(Func<Task> action)
    {
        try { await action(); }
        catch (PatrolSourceReadException error) when (error.Code == RecoveryCode) { return; }
        throw new InvalidOperationException("精確且已結清的 READ_INCOMPLETE 應結束當次呼叫並提供 " + RecoveryCode);
    }
    private static async Task ExpectTimeout(Func<Task> action)
    {
        try { await action(); }
        catch (PatrolSourceReadException error) when (error.Code == "SourceUnavailableTimeout") { return; }
        throw new InvalidOperationException("fixture 未留下結果未明的原 capture。");
    }
    private static async Task ExpectHardFailure(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (error is not PatrolSourceReadException) { return; }
        throw new InvalidOperationException("非精確分類或無效收據不得變成可接續的來源錯誤。");
    }
    private static async Task ExpectCancelled(Func<Task> action, CancellationToken token)
    {
        try { await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        throw new InvalidOperationException("原 caller cancellation 未保留。");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly PatrolReadFixture Time = new();
        internal readonly PatrolNavigationClient Client;
        internal readonly List<string> PostIds = [], StatusIds = [];
        internal bool Failed = true, TimeoutFirst;
        internal int Reads, NativeRequests;
        internal HttpStatusCode PostCode = HttpStatusCode.Conflict, StatusCode = HttpStatusCode.OK;
        internal object? PostOk = false, StatusOk = true;
        internal Action<Dictionary<string, object?>>? Mutate;
        internal Action? BeforeReply;
        private readonly bool force;
        internal PatrolObservation Expected => new(Time.Source, Time.Self, 4, 100, 200, Time.Now, true);
        internal string? PendingId
        {
            get
            {
                object? pending = typeof(PatrolNavigationClient).GetField("pendingCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client);
                return pending?.GetType().GetProperty("Id")?.GetValue(pending) as string;
            }
        }
        internal Fixture(bool force)
        {
            this.force = force;
            Client = new(() => { Reads++; return Time.Snapshot(); }, Send, () => Time.Now, Time.Delay,
                Time.Notices.Add, readOnly: true, forceDispatch: force);
        }
        private object Wire() => new { pid = Time.Source.ProcessId,
            creationFileTime = Time.Source.CreationFileTime.ToString(CultureInfo.InvariantCulture),
            imagePath = Time.Source.ImagePath, sha256 = Time.Source.DiskSha256, imageBase = Time.Source.ImageBase,
            imageSize = Time.Source.ImageSize, selfId = Time.Self.ToString(CultureInfo.InvariantCulture) };
        private Task<PatrolNavigationReply> Send(HttpMethod method, string path, object? payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool post = method == HttpMethod.Post && path == "/api/capture";
            bool status = method == HttpMethod.Get && path.StartsWith("/api/capture-status?captureId=", StringComparison.Ordinal);
            if (!post && !status) { NativeRequests++; throw new InvalidOperationException("非 capture 請求：" + path); }
            string id;
            if (post)
            {
                var request = JsonSerializer.SerializeToElement(payload);
                id = request.GetProperty("captureId").GetString()!;
                Need(PatrolNavigationClient.SourceMatches(request.GetProperty("expectedSource"), Time.Source, Time.Self), "POST 來源錯誤。");
                Need(force == (request.TryGetProperty("forceDispatch", out var dispatched) && dispatched.ValueKind == JsonValueKind.True), "forceDispatch wire 不符。");
                PostIds.Add(id);
                if (TimeoutFirst && PostIds.Count == 1) throw new TaskCanceledException("Owned fake capture timeout");
            }
            else { id = path[(path.IndexOf('=') + 1)..]; StatusIds.Add(id); }
            var body = new Dictionary<string, object?>
            {
                ["ok"] = Failed ? post ? PostOk : StatusOk : true,
                ["captureId"] = id, ["expectedSource"] = Wire(), ["source"] = Wire(),
                ["readOnly"] = true, ["inputSent"] = false, ["terminal"] = true,
                ["cleanupConfirmed"] = true, ["accepted"] = true,
                ["state"] = Failed ? "Failed" : "Completed", ["error"] = Failed ? "READ_CAPTURE_FAILED" : "",
                ["mapId"] = 4, ["x"] = 101, ["y"] = 200, ["standing"] = true, ["sampledAtUtc"] = Time.Now.ToString("O")
            };
            if (Failed) { body["sourceError"] = "READ_INCOMPLETE"; body["sourcePhase"] = "factory.context"; Mutate?.Invoke(body); }
            BeforeReply?.Invoke();
            return Task.FromResult(new PatrolNavigationReply(Failed ? post ? PostCode : StatusCode : HttpStatusCode.OK,
                JsonSerializer.SerializeToElement(body)));
        }
        internal void AssertNoFastRetryOrNative()
        {
            Need(Time.Delays == 0 && NativeRequests == 0, "分類驗證發生快速重讀延遲或 native 請求。");
            Need(!Time.Notices.Any(n => n.Code is "CAPTURE_READ_RETRY" or "CAPTURE_CLEANUP_RECOVERED" or "CAPTURE_RECOVERY_WINDOW"),
                "新分類誤入舊三次快速重試／清理後立即取點分支。");
            Need(typeof(PatrolNavigationClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client) is null,
                "離線驗證建立了子程序。");
        }
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class NoMarket : IPatrolMarket
    {
        internal int Calls;
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("取點失敗仍呼叫市場：" + request.Command); }
    }
}
