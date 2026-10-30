using System.Net;
using System.Reflection;
using System.Text.Json;
using MemoryHub;
using static LcPlayerMarket.PatrolFreshSourceGateSelfTest;

namespace LcPlayerMarket;

internal static class PatrolNavigationRecoverySelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, "通過（實際 Client／假 transport；無子程序、HTTP 或 LC）")); }
            catch (Exception e) { checks.Add(new(name, false, e.ToString())); }
        }
        await Check("nav_capture_fresh_once_no_navigation", async () =>
        {
            await using var f = new Fixture(); var value = await f.Client.CaptureAsync(default);
            Need(value.Source == f.Time.Source && value.SelfId == f.Time.Self && f.Calls.SequenceEqual(["POST /api/capture"]));
        });
        await Check("nav_new_source_next_round_and_settings_capture_ignore_old_cleaned_job_fence", async () =>
        {
            await using var f = new Fixture(readOnly: false); var first = f.Request();
            f.Reply = (_, _, _) => Okay(f.CompletedBody(first));
            Need((await f.Client.StartAsync(first, default)).CleanupConfirmed);
            var nextSource = f.Time.Source with { CreationFileTime = f.Time.Source.CreationFileTime + 1, SessionKey = "new-run-session" };
            ulong nextSelf = f.Time.Self + 1;
            f.Read = () =>
            {
                var snapshot = f.Time.Snapshot() with { Source = nextSource };
                snapshot.Fields["selfId"] = FieldReading.Observed(nextSelf, f.Time.Now); return snapshot;
            };
            Dictionary<string, object> NextWire()
            {
                var wire = f.Wire(); wire["creationFileTime"] = nextSource.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
                wire["selfId"] = nextSelf.ToString(System.Globalization.CultureInfo.InvariantCulture); return wire;
            }
            f.Reply = (_, _, _) => { var data = f.CaptureBody(); data["source"] = NextWire(); return Okay(data); };
            var initial = await f.Client.CaptureAsync(default); // Both a new run and the settings page deliberately have no prior run fence.
            Need(initial.Source == nextSource && initial.SelfId == nextSelf && f.LastJob() == first.JobId && f.OwnedJobs().ContainsKey(first.JobId));
            var second = first with { JobId = "offline-second-job", Expected = initial };
            f.Reply = (_, _, _) => { var data = f.CompletedBody(second); data["source"] = NextWire(); return Okay(data); };
            Need((await f.Client.StartAsync(second, default)).CleanupConfirmed && f.LastJob() == second.JobId && f.OwnedJobs().Count == 2);
            f.Reply = (_, _, _) => Okay(f.CompletedBody(first));
            Need((await f.Client.StopAsync(first.JobId, default)).CleanupConfirmed && f.LastJob() == second.JobId);
            Need(f.Calls.SequenceEqual(["POST /api/navigate", "POST /api/capture", "POST /api/navigate", "POST /api/stop"]));
        });
        foreach (string changed in new[] { "source", "self" })
            await Check("nav_explicit_run_" + changed + "_fence_rejects_unknown_position_before_http", async () =>
            {
                await using var f = new Fixture(); var expected = f.Request().Expected;
                f.Read = () =>
                {
                    var snapshot = f.Time.Snapshot(); snapshot.Fields["position"] = FieldReading.Unknown("gap");
                    if (changed == "source") snapshot = snapshot with { Source = f.Time.Source with { SessionKey = "different" } };
                    else snapshot.Fields["selfId"] = FieldReading.Observed(f.Time.Self + 1, f.Time.Now);
                    return snapshot;
                };
                await Error(() => f.Client.CaptureAsync(default, expected), "SourceMismatch");
                Need(f.Calls.Count == 0 && f.Time.Delays == 0);
            });
        await Check("nav_unscoped_capture_busy_preserves_unsettled_job_ownership", async () =>
        {
            await using var f = new Fixture(); var old = f.OwnJob();
            typeof(PatrolNavigationClient).GetField("activeJob", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Client, old.JobId);
            f.Reply = (_, _, _) => new(HttpStatusCode.Conflict, JsonSerializer.SerializeToElement(new { ok = false, error = "NAVIGATION_BUSY" }));
            await Rejected(() => f.Client.CaptureAsync(default));
            Need(f.Calls.SequenceEqual(["POST /api/capture"]) && f.LastJob() == old.JobId && f.OwnedJobs().Count == 1 && f.Time.Delays == 0);
            Need((string?)typeof(PatrolNavigationClient).GetField("activeJob", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Client) == old.JobId);
        });
        foreach (int ticks in new[] { 8, 22 })
            await Check("nav_capture_gap_" + ticks + "_only_recaptures_if_expired", async () =>
            {
                await using var f = new Fixture();
                f.Reply = (_, _, _) =>
                {
                    if (f.Calls.Count == 1) f.Read = () => f.Time.Delays < ticks ? null : f.Time.Snapshot();
                    return f.Capture();
                };
                var value = await f.Client.CaptureAsync(default);
                Need(value.Source == f.Time.Source && f.Time.Delays == ticks);
                Need(f.Calls.Count == (ticks > 20 ? 2 : 1) && f.Calls.All(c => c == "POST /api/capture"));
                Need(f.Time.Now - value.SampledAt <= TimeSpan.FromSeconds(2));
            });
        await Check("nav_capture_typed_readonly_409_retries_whole_capture", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) => f.Calls.Count == 1 ? RetryReply() : f.Capture();
            Need((await f.Client.CaptureAsync(default)).Source == f.Time.Source && f.Calls.Count == 2 && f.Time.Delays == 1);
            Need(f.Calls.All(c => c == "POST /api/capture") && f.Time.Notices.Any(n => n.Code == "CAPTURE_READ_RETRY"));
        });
        foreach (string variant in new[] { "generic500", "wrong_code", "missing_readonly", "input_true", "missing_input", "wrong_status" })
            await Check("nav_capture_" + variant + "_never_retries", async () =>
            {
                await using var f = new Fixture();
                var body = RetryBody(); var status = HttpStatusCode.Conflict;
                if (variant == "generic500") { status = HttpStatusCode.InternalServerError; body["error"] = "SERVER_ERROR"; }
                if (variant == "wrong_code") body["error"] = "MODULE_PATH_FAILED";
                if (variant == "missing_readonly") body.Remove("readOnly");
                if (variant == "input_true") body["inputSent"] = true;
                if (variant == "missing_input") body.Remove("inputSent");
                if (variant == "wrong_status") status = HttpStatusCode.ServiceUnavailable;
                f.Reply = (_, _, _) => new(status, JsonSerializer.SerializeToElement(body));
                await Rejected(() => f.Client.CaptureAsync(default)); Need(f.Calls.Count == 1 && f.Time.Delays == 0);
            });
        await Check("nav_capture_typed_retry_maximum_three_sends", async () =>
        {
            await using var f = new Fixture(); f.Reply = (_, _, _) => RetryReply();
            await Rejected(() => f.Client.CaptureAsync(default));
            Need(f.Calls.Count == 3 && f.Time.Delays == 2 && f.Calls.All(c => c == "POST /api/capture"));
        });
        await Check("nav_capture_retry_total_four_second_budget", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) => { f.Time.Now += TimeSpan.FromMilliseconds(3900); return RetryReply(); };
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout"); Need(f.Calls.Count == 1);
        });
        await Check("nav_capture_precancel_creates_no_child_or_request", async () =>
        {
            await using var f = new Fixture(); using var stop = new CancellationTokenSource(); stop.Cancel();
            await Cancelled(() => f.Client.CaptureAsync(stop.Token)); Need(f.Calls.Count == 0 && f.ReadCount == 0 && f.ChildIsNull());
        });
        await Check("nav_capture_cancel_during_gap_does_not_recapture", async () =>
        {
            await using var f = new Fixture(); using var stop = new CancellationTokenSource();
            f.Time.AfterDelay = stop.Cancel;
            f.Reply = (_, _, _) => { f.Read = () => null; return f.Capture(); };
            await Cancelled(() => f.Client.CaptureAsync(stop.Token)); Need(f.Calls.Count == 1 && f.ChildIsNull());
        });
        await Check("nav_capture_raw_session_change_never_recaptures", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) =>
            {
                f.Read = () => f.Time.Snapshot() with { Source = f.Time.Source with { SessionKey = "other" } };
                return f.Capture();
            };
            await Error(() => f.Client.CaptureAsync(default), "SourceMismatch"); Need(f.Calls.Count == 1 && f.Time.Delays == 0);
        });
        await Check("nav_capture_foreign_wire_identity_never_retries", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) =>
            {
                var value = f.CaptureBody(); var wire = f.Wire(); wire["selfId"] = "99"; value["source"] = wire;
                return Okay(value);
            };
            await Rejected(() => f.Client.CaptureAsync(default)); Need(f.Calls.Count == 1);
        });
        await Check("nav_completed_wait_refreshes_same_original_job_only", async () =>
        {
            await using var f = new Fixture(); var request = f.OwnJob();
            f.Reply = (_, _, _) =>
            {
                if (f.Calls.Count == 1) f.Read = () => f.Time.Delays < 22 ? null : f.Time.Snapshot();
                return Okay(f.CompletedBody(request));
            };
            var state = await f.Client.GetStateAsync(request.JobId, default);
            Need(state.Completed && state.CleanupConfirmed && state.Source == request.Expected.Source);
            Need(f.Calls.Count == 2 && f.Calls.All(c => c == "GET /api/status?jobId=" + request.JobId));
            Need(f.Time.Now - state.SampledAt <= TimeSpan.FromSeconds(2));
        });
        await Check("nav_status_typed_capture_envelope_does_not_retry", async () =>
        {
            await using var f = new Fixture(); var request = f.OwnJob(); f.Reply = (_, _, _) => RetryReply();
            await Rejected(() => f.Client.GetStateAsync(request.JobId, default)); Need(f.Calls.Count == 1);
        });
        await Check("nav_completed_fixed_timestamp_remains_stale_without_resend", async () =>
        {
            await using var f = new Fixture(); var request = f.OwnJob(); var fixedAt = f.Time.Now.AddSeconds(-3);
            f.Reply = (_, _, _) => { var data = f.CompletedBody(request); data["sampledAtUtc"] = fixedAt.ToString("O"); return Okay(data); };
            await Error(() => f.Client.GetStateAsync(request.JobId, default), "NavigationSampleStale");
            Need(f.Calls.Count == 3 && f.Calls.All(c => c == "GET /api/status?jobId=" + request.JobId));
        });
        await Check("nav_accepted_post_then_status_409_is_not_unsent_cleanup_receipt", async () =>
        {
            await using var f = new Fixture(readOnly: false); var request = f.Request();
            f.Reply = (_, path, _) =>
            {
                if (path != "/api/navigate") return RetryReply();
                var data = f.CompletedBody(request); data["sampledAtUtc"] = f.Time.Now.AddSeconds(-3).ToString("O"); return Okay(data);
            };
            await Rejected(() => f.Client.StartAsync(request, default));
            Need(f.Calls.SequenceEqual(["POST /api/navigate", "GET /api/status?jobId=" + request.JobId]));
            var rejected = (Dictionary<string, PatrolNavigationState>)typeof(PatrolNavigationClient).GetField("rejectedJobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Client)!;
            Need(rejected.Count == 0 && (string?)typeof(PatrolNavigationClient).GetField("lastJob", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Client) == request.JobId);
            Need(f.ChildIsNull());
        });
        await Check("nav_status_foreign_job_no_refresh", async () =>
        {
            await using var f = new Fixture(); var request = f.OwnJob();
            f.Reply = (_, _, _) => { var data = f.CompletedBody(request); data["jobId"] = "other"; return Okay(data); };
            await Rejected(() => f.Client.GetStateAsync(request.JobId, default)); Need(f.Calls.Count == 1);
        });
        await Check("nav_stop_cleanup_does_not_wait_for_live_source", async () =>
        {
            await using var f = new Fixture(); var request = f.OwnJob(); f.Read = () => null;
            f.Reply = (_, _, _) => Okay(f.CompletedBody(request));
            var state = await f.Client.StopAsync(request.JobId, default);
            Need(state.CleanupConfirmed && state.WorkSettled && f.ReadCount == 0 && f.Time.Delays == 0);
            Need(f.Calls.SequenceEqual(["POST /api/stop"]));
        });
        await Check("nav_readonly_fixture_never_starts_navigation", async () =>
        {
            await using var f = new Fixture();
            await Rejected(() => f.Client.StartAsync(f.Request(), default)); Need(f.Calls.Count == 0 && f.ChildIsNull());
        });
        foreach (int radius in new[] { 1, 2, 10 })
        {
            await Check("nav_radius_" + radius + "_wire_echo_diagonal_handoff_and_historical_stop", async () =>
            {
                await using var f = new Fixture(readOnly: false); var request = f.Request() with { ArrivalRadius = radius };
                f.Reply = (_, path, payload) =>
                {
                    if (path == "/api/navigate") Need(JsonSerializer.SerializeToElement(payload).GetProperty("arrivalRadius").GetInt32() == radius);
                    var data = f.CompletedBody(request); data["x"] = request.Waypoint.X + radius; data["y"] = request.Waypoint.Y + radius;
                    data["distance"] = radius; return Okay(data);
                };
                var state = await f.Client.StartAsync(request, default);
                Need(state.Completed && state.ArrivalRadius == radius);
                f.Read = () => null; f.Time.Now += TimeSpan.FromSeconds(30);
                Need((await f.Client.StopAsync(request.JobId, default)).ArrivalRadius == radius);
                Need(f.Calls.SequenceEqual(["POST /api/navigate", "POST /api/stop"]));
            });
            await Check("nav_radius_" + radius + "_outside_coordinate_rejects_even_reported_zero", async () =>
            {
                await using var f = new Fixture(readOnly: false); var request = f.Request() with { ArrivalRadius = radius };
                f.Reply = (_, _, _) => { var data = f.CompletedBody(request); data["x"] = request.Waypoint.X + radius + 1; data["distance"] = 0; return Okay(data); };
                await Rejected(() => f.Client.StartAsync(request, default)); Need(f.Calls.SequenceEqual(["POST /api/navigate"]) && f.LastJob() == request.JobId);
            });
        }
        foreach (string invalid in new[] { "missing", "null", "true", "2.5", "\"2\"", "0", "11", "5" })
            await Check("nav_radius_echo_invalid_" + invalid + "_cannot_handoff_or_claim_cleanup", async () =>
            {
                await using var f = new Fixture(readOnly: false); var request = f.Request();
                f.Reply = (_, _, _) =>
                {
                    var data = f.CompletedBody(request);
                    if (invalid == "missing") data.Remove("arrivalRadius"); else data["arrivalRadius"] = JsonSerializer.Deserialize<JsonElement>(invalid);
                    return Okay(data);
                };
                await Rejected(() => f.Client.StartAsync(request, default));
                await Rejected(() => f.Client.StopAsync(request.JobId, default));
                Need(f.Calls.SequenceEqual(["POST /api/navigate", "POST /api/stop"]) && f.OwnedJobs().ContainsKey(request.JobId));
            });
        foreach (int invalid in new[] { 0, 11 })
            await Check("nav_invalid_radius_" + invalid + "_no_registration_or_request", async () =>
            {
                await using var f = new Fixture(readOnly: false);
                try { await f.Client.StartAsync(f.Request() with { ArrivalRadius = invalid }, default); throw new InvalidOperationException("Invalid radius accepted"); }
                catch (InvalidDataException) { }
                Need(f.Calls.Count == 0 && f.OwnedJobs().Count == 0 && f.ChildIsNull());
            });
        await Check("nav_known_rejected_radius_preserved_without_stop_send", async () =>
        {
            await using var f = new Fixture(readOnly: false); var request = f.Request() with { ArrivalRadius = 7 };
            f.Reply = (_, _, _) => new(HttpStatusCode.Conflict, JsonSerializer.SerializeToElement(new { ok = false, error = "NAVIGATION_BUSY" }));
            var state = await f.Client.StartAsync(request, default);
            Need(state.ArrivalRadius == 7 && state.CleanupConfirmed && !state.Completed);
            Need((await f.Client.StopAsync(request.JobId, default)).ArrivalRadius == 7 && f.Calls.SequenceEqual(["POST /api/navigate"]));
        });
        await Check("nav_capture_realistic_slow_read_then_recovered_gap_gets_one_fresh_read", async () =>
        {
            await using var f = new Fixture(); var began = f.Time.Now;
            f.Reply = (_, _, _) =>
            {
                f.Time.Now += TimeSpan.FromMilliseconds(1600);
                if (f.Calls.Count == 1) f.Read = () => f.Time.Delays < 22 ? null : f.Time.Snapshot();
                return f.Capture();
            };
            var value = await f.Client.CaptureAsync(default);
            Need(f.Calls.SequenceEqual(["POST /api/capture", "POST /api/capture"]));
            Need(f.Time.Delays == 22 && f.Time.Now - began == TimeSpan.FromMilliseconds(5400));
            Need(value.Source == f.Time.Source && value.SelfId == f.Time.Self && value.SampledAt == f.Time.Now);
            Need(f.ChildIsNull());
        });
        await Check("nav_capture_second_recovered_gap_cannot_renew_window_again", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) =>
            {
                f.Time.Now += TimeSpan.FromMilliseconds(1600);
                if (f.Calls.Count == 1) f.Read = () => f.Time.Delays < 22 ? null : f.Time.Snapshot();
                if (f.Calls.Count == 2) f.Read = () => f.Time.Delays < 44 ? null : f.Time.Snapshot();
                return f.Capture();
            };
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            Need(f.Calls.Count <= 3 && f.Calls.All(c => c == "POST /api/capture"));
            Need(f.Time.Delays == 44 && f.ChildIsNull());
        });
        await Check("nav_capture_unrecovered_post_gap_retains_original_four_second_limit", async () =>
        {
            await using var f = new Fixture(); var began = f.Time.Now;
            f.Reply = (_, _, _) =>
            {
                f.Time.Now += TimeSpan.FromMilliseconds(1600); f.Read = () => null; return f.Capture();
            };
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            Need(f.Time.Now - began == TimeSpan.FromSeconds(4) && f.Calls.Count == 1 && f.Time.Delays == 24);
        });
        await Check("nav_capture_stale_reply_without_post_gate_recovery_gets_no_extension", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) =>
            {
                f.Time.Now += TimeSpan.FromMilliseconds(f.Calls.Count == 1 ? 3900 : 1600);
                var data = f.CaptureBody();
                if (f.Calls.Count == 1) data["sampledAtUtc"] = f.Time.Now.AddSeconds(-3).ToString("O");
                return Okay(data);
            };
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            Need(f.Calls.Count <= 2 && f.Time.Delays == 0 && f.ChildIsNull());
        });
        await Check("nav_capture_source_change_after_recovery_grant_stops_before_next_capture", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) =>
            {
                f.Time.Now += TimeSpan.FromMilliseconds(1600);
                f.Read = () =>
                {
                    if (f.Time.Delays < 22) return null;
                    f.Read = () => f.Time.Snapshot() with { Source = f.Time.Source with { SessionKey = "changed-after-recovery" } };
                    return f.Time.Snapshot();
                };
                return f.Capture();
            };
            await Error(() => f.Client.CaptureAsync(default), "SourceMismatch");
            Need(f.Calls.Count == 1 && f.Time.Delays == 22 && f.ChildIsNull());
        });
        await Check("nav_capture_recovery_window_still_obeys_original_caller_cancel", async () =>
        {
            await using var f = new Fixture(); using var stop = new CancellationTokenSource();
            f.Reply = (_, _, _) =>
            {
                f.Time.Now += TimeSpan.FromMilliseconds(1600);
                if (f.Calls.Count == 1) f.Read = () => f.Time.Delays < 22 ? null : f.Time.Snapshot();
                else stop.Cancel();
                return f.Capture();
            };
            await Cancelled(() => f.Client.CaptureAsync(stop.Token));
            Need(f.Calls.SequenceEqual(["POST /api/capture", "POST /api/capture"]) && f.ChildIsNull());
        });
        
        foreach (string invalid in new[] { "missing", "future" })
            await Check("nav_capture_" + invalid + "_timestamp_gap_cannot_grant_fresh_budget", async () =>
            {
                await using var f = new Fixture();
                f.Reply = (_, _, _) =>
                {
                    f.Time.Now += TimeSpan.FromMilliseconds(1600);
                    if (f.Calls.Count == 1) f.Read = () => f.Time.Delays < 22 ? null : f.Time.Snapshot();
                    var data = f.CaptureBody();
                    if (invalid == "missing") data.Remove("sampledAtUtc");
                    else data["sampledAtUtc"] = f.Time.Now.AddMinutes(1).ToString("O");
                    return Okay(data);
                };
                await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
                Need(f.Calls.Count == 2 && !f.Time.Notices.Any(n => n.Code == "CAPTURE_RECOVERY_WINDOW"));
            });
        await Check("nav_capture_prior_typed_failure_cannot_extend_recovered_gap_budget", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) =>
            {
                f.Time.Now += TimeSpan.FromMilliseconds(f.Calls.Count == 1 ? 1000 : 500);
                if (f.Calls.Count == 1) return RetryReply();
                if (f.Calls.Count == 2) f.Read = () => f.Time.Delays < 23 ? null : f.Time.Snapshot();
                else f.Time.Now += TimeSpan.FromMilliseconds(1000);
                return f.Capture();
            };
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            Need(f.Calls.Count == 3 && !f.Time.Notices.Any(n => n.Code == "CAPTURE_RECOVERY_WINDOW"));
        });
        await Check("nav_capture_malformed_coordinates_cannot_hide_behind_recovery", async () =>
        {
            foreach (string field in new[] { "mapId", "x", "y" })
            {
                await using var f = new Fixture();
                f.Reply = (_, _, _) =>
                {
                    f.Time.Now += TimeSpan.FromMilliseconds(1600);
                    f.Read = () => f.Time.Delays < 22 ? null : f.Time.Snapshot();
                    var data = f.CaptureBody(); data[field] = "invalid"; return Okay(data);
                };
                await Rejected(() => f.Client.CaptureAsync(default));
                Need(f.Calls.Count == 1 && f.Time.Delays == 0 && !f.Time.Notices.Any(n => n.Code == "CAPTURE_RECOVERY_WINDOW"));
            }
        });
        await Check("nav_owned_capture_timeout_waits_cleanup_then_fresh_sample_and_clears_error", async () =>
        {
            await using var f = new OwnedCaptureFixture();
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            var observed = await f.Client.CaptureAsync(default, f.Expected);
            Need(f.Posts == 2 && f.StatusReads == 2 && observed.X == 101 && observed.Y == 200);
            Need(f.StatusIds.All(id => id == f.FirstId) && f.PostIds.Distinct().Count() == 2);
            Need(f.Time.Notices.Any(n => n.Code == "CAPTURE_CLEANUP_RECOVERED") && f.NativeRequests == 0);
        });
        foreach (string malformed in new[] { "unknown", "wrong_id", "missing_readonly", "input_true", "missing_input", "missing_terminal", "cleanup_false", "source_changed", "wrong_state", "fatal", "missing_error", "failed_empty", "rejected_empty", "accepted_false" })
            await Check("nav_owned_capture_status_" + malformed + "_never_clears_pending_or_reposts", async () =>
            {
                await using var f = new OwnedCaptureFixture { Malformed = malformed };
                await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
                await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.Posts == 1 && f.StatusReads == 2 && f.StatusIds.All(id => id == f.FirstId) && f.NativeRequests == 0);
            });
        await Check("nav_owned_capture_never_settles_has_bounded_status_only_wait", async () =>
        {
            await using var f = new OwnedCaptureFixture { NeverSettles = true };
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            var began = f.Time.Now;
            await Error(() => f.Client.CaptureAsync(default, f.Expected), "SourceUnavailableTimeout");
            Need(f.Time.Now-began <= TimeSpan.FromSeconds(4) && f.Posts == 1 && f.StatusReads <= 40 && f.NativeRequests == 0);
        });
        await Check("nav_owned_capture_cancellation_keeps_original_pending", async () =>
        {
            await using var f = new OwnedCaptureFixture();
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Cancelled(() => f.Client.CaptureAsync(stop.Token, f.Expected));
            Need(f.Posts == 1 && f.StatusReads == 0);
            Need((await f.Client.CaptureAsync(default, f.Expected)).X == 101 && f.Posts == 2);
        });
        await Check("nav_owned_capture_source_change_blocks_before_status_or_new_post", async () =>
        {
            await using var f = new OwnedCaptureFixture();
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            f.SourceChanged = true;
            await Error(() => f.Client.CaptureAsync(default, f.Expected), "SourceMismatch");
            Need(f.Posts == 1 && f.StatusReads == 0);
        });
        await Check("nav_owned_capture_unknown_blocks_navigate_without_send", async () =>
        {
            await using var f = new OwnedCaptureFixture(readOnly: false);
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            var request = new PatrolNavigationRequest("offline-move", f.Expected, new("point", "test", "4", "map", 100, 200));
            await Rejected(() => f.Client.StartAsync(request, default));
            Need(f.Posts == 1 && f.NativeRequests == 0);
        });
        foreach (string failure in new[] { "SERVER_ERROR", "SOURCE_CHANGED" })
            await Check("nav_capture_direct_fatal_" + failure + "_persists_across_two_calls_and_start", async () =>
            {
                await using var f = new OwnedCaptureFixture(readOnly: false) { TimeoutFirst = false, DirectFatal = failure };
                await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                var request = new PatrolNavigationRequest("fatal-followup-move", f.Expected, new("point", "test", "4", "map", 100, 200));
                await Rejected(() => f.Client.StartAsync(request, default));
                Need(f.Posts == 1 && f.StatusReads == 2 && f.NativeRequests == 0 && !f.Time.Notices.Any(n => n.Code == "CAPTURE_CLEANUP_RECOVERED"));
            });
        await Check("nav_owned_capture_semaphore_serializes_concurrent_callers", async () =>
        {
            await using var f = new OwnedCaptureFixture { TimeoutFirst = false, BlockFirst = true };
            var first = f.Client.CaptureAsync(default);
            await f.FirstEntered.Task;
            var second = f.Client.CaptureAsync(default);
            await Task.Delay(30);
            Need(f.Posts == 1);
            f.ReleaseFirst.TrySetResult();
            await Task.WhenAll(first, second);
            Need(f.Posts == 2 && f.StatusReads == 0 && f.PostIds.Distinct().Count() == 2 && f.NativeRequests == 0);
        });
        await Check("nav_owned_capture_sequence_overflow_never_reuses_id", async () =>
        {
            await using var f = new OwnedCaptureFixture { TimeoutFirst = false };
            typeof(PatrolNavigationClient).GetField("captureSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Client, long.MaxValue);
            await Rejected(() => f.Client.CaptureAsync(default));
            Need(f.Posts == 0);
        });
        await Check("nav_vector_changed_fresh_capture_recovers_without_restart_or_movement", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) => f.Calls.Count == 1 ? RetryReply("READ_CAPTURE_VECTOR_CHANGED") : f.Capture();
            var result = await f.Client.CaptureAsync(default);
            Need(result.Source == f.Time.Source && f.Calls.Count == 2 && f.Time.Delays == 1);
            Need(f.Calls.All(c => c == "POST /api/capture") && f.ChildIsNull());
        });
        await Check("nav_vector_changed_persistent_stops_after_three_fresh_reads", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) => RetryReply("READ_CAPTURE_VECTOR_CHANGED");
            await Rejected(() => f.Client.CaptureAsync(default));
            Need(f.Calls.Count == 3 && f.Time.Delays == 2);
            f.Reply = (_, _, _) => f.Capture();
            Need((await f.Client.CaptureAsync(default)).Source == f.Time.Source && f.Calls.Count == 4);
        });
        await Check("nav_vector_changed_does_not_extend_four_second_budget", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) => { f.Time.Now += TimeSpan.FromMilliseconds(3900); return RetryReply("READ_CAPTURE_VECTOR_CHANGED"); };
            await Error(() => f.Client.CaptureAsync(default), "SourceUnavailableTimeout");
            Need(f.Calls.Count == 1 && !f.Time.Notices.Any(n => n.Code == "CAPTURE_RECOVERY_WINDOW"));
        });
        foreach (string variant in new[] { "cleanup_false", "input_true", "source_changed", "wrong_id", "nonterminal", "bounds", "unknown" })
            await Check("nav_vector_changed_" + variant + "_never_unlocks_or_retries", async () =>
            {
                await using var f = new Fixture();
                f.Reply = (_, _, _) =>
                {
                    var data = RetryBody("READ_CAPTURE_VECTOR_CHANGED");
                    if (variant == "cleanup_false") data["cleanupConfirmed"] = false;
                    if (variant == "input_true") data["inputSent"] = true;
                    if (variant == "source_changed") data["expectedSource"] = new { pid = 999 };
                    if (variant == "wrong_id") data["captureId"] = new string('f', 32);
                    if (variant == "nonterminal") { data["state"] = "Reading"; data["terminal"] = false; data["cleanupConfirmed"] = false; data["error"] = ""; }
                    if (variant == "bounds") data["error"] = "READ_CAPTURE_VECTOR_BOUNDS";
                    if (variant == "unknown") data["error"] = "READ_CAPTURE_FAILED";
                    return new(HttpStatusCode.Conflict, JsonSerializer.SerializeToElement(data));
                };
                await Rejected(() => f.Client.CaptureAsync(default));
                Need(f.Calls.Count == 1 && f.Time.Delays == 0);
            });
        await Check("nav_restart_has_no_stale_capture_but_does_not_reset_prior_client", async () =>
        {
            await using var old = new OwnedCaptureFixture { TimeoutFirst = false, DirectFatal = "SERVER_ERROR" };
            await Rejected(() => old.Client.CaptureAsync(default));
            await using var fresh = new Fixture();
            Need((await fresh.Client.CaptureAsync(default)).Source == fresh.Time.Source && fresh.Calls.Count == 1);
            await Rejected(() => old.Client.CaptureAsync(default));
            Need(old.Posts == 1 && old.StatusReads == 1 && old.NativeRequests == 0);
        });
        // A generic read failure ends this call. A verified terminal receipt may
        // retire its ID, but must not turn it into an automatic read/navigation retry.
        foreach (bool force in new[] { false, true })
        {
            await Check("nav_capture_failed_direct_settles_only_this_call_force_" + force, async () =>
            {
                await using var f = new OwnedCaptureFixture(readOnly: false, forceDispatch: force)
                    { TimeoutFirst = false, DirectFatal = "READ_CAPTURE_FAILED" };
                await TerminalReadFailure(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.Posts == 1 && f.StatusReads == 0 && f.NativeRequests == 0 && f.Time.Delays == 0);
                Need(!f.Time.Notices.Any(n => n.Code is "CAPTURE_READ_RETRY" or "CAPTURE_CLEANUP_RECOVERED"));
                f.DirectFatal = "";
                var fresh = await f.Client.CaptureAsync(default, f.Expected);
                Need(fresh.X == 101 && f.Posts == 2 && f.StatusReads == 0 && f.PostIds.Distinct().Count() == 2);
                Need(f.NativeRequests == 0 && f.Time.Delays == 0);
            });
            await Check("nav_capture_failed_status_settles_then_stops_before_new_post_force_" + force, async () =>
            {
                await using var f = new OwnedCaptureFixture(readOnly: false, forceDispatch: force)
                    { DirectFatal = "READ_CAPTURE_FAILED" };
                await Error(() => f.Client.CaptureAsync(default, f.Expected), "SourceUnavailableTimeout");
                await TerminalReadFailure(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.Posts == 1 && f.StatusReads == 1 && f.StatusIds.Single() == f.FirstId);
                Need(f.NativeRequests == 0 && f.Time.Delays == 0
                    && !f.Time.Notices.Any(n => n.Code is "CAPTURE_READ_RETRY" or "CAPTURE_CLEANUP_RECOVERED"));
                f.DirectFatal = "";
                Need((await f.Client.CaptureAsync(default, f.Expected)).X == 101);
                Need(f.Posts == 2 && f.StatusReads == 1 && f.PostIds.Distinct().Count() == 2 && f.NativeRequests == 0);
            });
        }
        await Check("nav_capture_failed_repeated_manual_calls_each_stop_after_one_new_id", async () =>
        {
            await using var f = new OwnedCaptureFixture(readOnly: false, forceDispatch: true)
                { TimeoutFirst = false, DirectFatal = "READ_CAPTURE_FAILED" };
            for (int manual = 1; manual <= 3; manual++)
            {
                await TerminalReadFailure(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.Posts == manual && f.StatusReads == 0 && f.NativeRequests == 0 && f.Time.Delays == 0);
            }
            Need(f.PostIds.Distinct().Count() == 3);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(() => f.Client.CaptureAsync(cancelled.Token, f.Expected));
            Need(f.Posts == 3 && f.StatusReads == 0 && f.NativeRequests == 0);
        });
        foreach (string anomalous in new[] { "http500", "ok_true" })
            await Check("nav_capture_failed_direct_" + anomalous + "_keeps_id_for_status", async () =>
            {
                await using var f = new OwnedCaptureFixture(readOnly: false, forceDispatch: true)
                    { TimeoutFirst = false, DirectFatal = "READ_CAPTURE_FAILED",
                      DirectStatus = anomalous == "http500" ? HttpStatusCode.InternalServerError : HttpStatusCode.Conflict,
                      DirectOk = anomalous == "ok_true" };
                await TerminalReadFailure(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.Posts == 1 && f.StatusReads == 0 && f.NativeRequests == 0);
                // The later valid status is a distinct proof. The anomalous POST
                // itself must not retire ownership and let this call post again.
                await TerminalReadFailure(() => f.Client.CaptureAsync(default, f.Expected));
                Need(f.Posts == 1 && f.StatusReads == 1 && f.StatusIds.Single() == f.FirstId && f.NativeRequests == 0);
            });
        var invalidReceipts = new (string Name, Action<Dictionary<string, object>> Mutate)[]
        {
            ("wrong_id", d => d["captureId"] = new string('f', 32)),
            ("missing_source", d => d.Remove("expectedSource")),
            ("incomplete_source", d => d["expectedSource"] = new { pid = 123 }),
            ("changed_source", d => d["expectedSource"] = new { pid = 999 }),
            ("missing_readonly", d => d.Remove("readOnly")),
            ("readonly_false", d => d["readOnly"] = false),
            ("missing_input", d => d.Remove("inputSent")),
            ("input_true", d => d["inputSent"] = true),
            ("missing_terminal", d => d.Remove("terminal")),
            ("terminal_false", d => d["terminal"] = false),
            ("missing_cleanup", d => d.Remove("cleanupConfirmed")),
            ("cleanup_false", d => d["cleanupConfirmed"] = false),
            ("missing_accepted", d => d.Remove("accepted")),
            ("accepted_false", d => d["accepted"] = false),
            ("wrong_state", d => d["state"] = "Completed"),
            ("rejected", d => { d["state"] = "Rejected"; d["accepted"] = false; }),
            ("missing_error", d => d.Remove("error")),
            ("server_error", d => d["error"] = "SERVER_ERROR"),
            ("source_changed", d => d["error"] = "SOURCE_CHANGED")
        };
        foreach (bool viaStatus in new[] { false, true })
            foreach (var invalid in invalidReceipts)
                await Check("nav_capture_failed_" + (viaStatus ? "status_" : "direct_") + invalid.Name + "_keeps_owned_id", async () =>
                {
                    await using var f = new OwnedCaptureFixture(readOnly: false, forceDispatch: true)
                        { TimeoutFirst = viaStatus, DirectFatal = "READ_CAPTURE_FAILED", MutateReceipt = invalid.Mutate };
                    if (viaStatus) await Error(() => f.Client.CaptureAsync(default, f.Expected), "SourceUnavailableTimeout");
                    else await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                    await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                    await Rejected(() => f.Client.CaptureAsync(default, f.Expected));
                    Need(f.Posts == 1 && f.StatusReads == 2 && f.StatusIds.All(id => id == f.FirstId)
                        && f.NativeRequests == 0 && f.Time.Delays == 0);
                });
        foreach (bool midPatrol in new[] { false, true })
            foreach (bool reset in new[] { false, true })
                await Check("nav_capture_failed_production_controller_" + (midPatrol ? "arrival_" : "initial_")
                    + (reset ? "reset_then_start" : "manual_start") + "_uses_new_id", async () =>
                {
                    await using var f = new CaptureFailurePatrolFixture();
                    if (midPatrol) f.Navigation.AfterStart = () => f.Capture.DirectFatal = "READ_CAPTURE_FAILED";
                    else f.Capture.DirectFatal = "READ_CAPTURE_FAILED";
                    var failed = await f.Controller.RunAsync(f.Route);
                    int failedPosts = midPatrol ? 3 : 1;
                    Need(failed.State == "Failed" && failed.CleanupConfirmed && failed.Detail.Contains("READ_CAPTURE_FAILED")
                        && failed.RetriesUsed == 0 && f.Capture.Posts == failedPosts && f.Capture.StatusReads == 0
                        && f.Navigation.Starts == (midPatrol ? 1 : 0) && f.Market.Scans == 0 && f.Market.Calls == 0
                        && f.Capture.NativeRequests == 0 && f.Cooldowns == 0 && f.Capture.Time.Delays == 0);
                    f.Navigation.AfterStart = null; f.Capture.DirectFatal = "";
                    if (reset) await f.Controller.ResetProgressAsync();
                    Need(f.Capture.Posts == failedPosts && f.Capture.StatusReads == 0 && f.Navigation.Starts == (midPatrol ? 1 : 0));
                    var restarted = await f.Controller.RunAsync(f.Route);
                    Need(restarted.State == "Completed" && restarted.CleanupConfirmed && restarted.CompletedPoints == 1
                        && f.Capture.Posts > failedPosts && f.Capture.StatusReads == 0 && f.Capture.NativeRequests == 0
                        && f.Capture.PostIds.Distinct().Count() == f.Capture.Posts && f.Market.Scans == 1 && f.Cooldowns == 0);
                    Need(f.Navigation.Starts == (midPatrol && reset ? 2 : 1));
                });
        return checks;
    }

    private static Dictionary<string, object> RetryBody(string error = "READ_CAPTURE_MODULE_PATH_FAILED") => new()
    { ["ok"] = false, ["error"] = error, ["readOnly"] = true, ["inputSent"] = false,
      ["terminal"] = true, ["cleanupConfirmed"] = true, ["state"] = "Failed", ["accepted"] = true };
    private static PatrolNavigationReply RetryReply(string error = "READ_CAPTURE_MODULE_PATH_FAILED") => new(HttpStatusCode.Conflict, JsonSerializer.SerializeToElement(RetryBody(error)));
    private static PatrolNavigationReply Okay(object value) => new(HttpStatusCode.OK, JsonSerializer.SerializeToElement(value));
    private static async Task Rejected(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("預期拒絕但被接受");
    }

    private static async Task TerminalReadFailure(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (error is not PatrolSourceReadException
            && error.Message.Contains("READ_CAPTURE_FAILED", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("完整失敗收據必須結束當次呼叫，不能自動重讀或當成取點成功。");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly PatrolReadFixture Time = new();
        internal readonly List<string> Calls = [];
        internal Func<HubSnapshot?> Read;
        internal Func<HttpMethod, string, object?, PatrolNavigationReply> Reply;
        internal readonly PatrolNavigationClient Client;
        internal int ReadCount;
        private string captureId = "";
        private JsonElement captureExpectedSource;
        internal Fixture(bool readOnly = true)
        {
            Read = () => Time.Snapshot(); Reply = (_, _, _) => Capture();
            Client = new(() => { ReadCount++; return Read(); }, (method, path, body, token) =>
            {
                token.ThrowIfCancellationRequested(); Calls.Add(method.Method + " " + path);
                if (path == "/api/capture")
                {
                    var request = JsonSerializer.SerializeToElement(body);
                    captureId = request.GetProperty("captureId").GetString()!;
                    captureExpectedSource = request.GetProperty("expectedSource").Clone();
                }
                var reply = Reply(method, path, body);
                if (path == "/api/capture")
                {
                    var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reply.Body.GetRawText())!;
                    value.TryAdd("captureId", JsonSerializer.SerializeToElement(captureId));
                    value.TryAdd("expectedSource", captureExpectedSource);
                    reply = reply with { Body = JsonSerializer.SerializeToElement(value) };
                }
                return Task.FromResult(reply);
            },
                () => Time.Now, Time.Delay, Time.Notices.Add, readOnly: readOnly);
        }
        internal Dictionary<string, object> Wire() => new()
        {
            ["pid"] = Time.Source.ProcessId, ["creationFileTime"] = Time.Source.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["imagePath"] = Time.Source.ImagePath, ["sha256"] = Time.Source.DiskSha256, ["imageBase"] = Time.Source.ImageBase,
            ["imageSize"] = Time.Source.ImageSize, ["selfId"] = Time.Self.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        internal Dictionary<string, object> CaptureBody() => new()
        {
            ["ok"] = true, ["inputSent"] = false, ["source"] = Wire(), ["mapId"] = 4,
            ["captureId"] = captureId, ["expectedSource"] = captureExpectedSource.ValueKind == JsonValueKind.Undefined ? Wire() : captureExpectedSource, ["readOnly"] = true,
            ["terminal"] = true, ["cleanupConfirmed"] = true, ["state"] = "Completed",
            ["accepted"] = true, ["error"] = "",
            ["x"] = 100, ["y"] = 200, ["standing"] = true, ["sampledAtUtc"] = Time.Now.ToString("O")
        };
        internal PatrolNavigationReply Capture() => Okay(CaptureBody());
        internal PatrolNavigationRequest Request() => new("offline-original-job", new(Time.Source, Time.Self, 4, 100, 200, Time.Now, true),
            new("04c780a53157440aabdfc9bcb3f95e7d", "測試點", "4", "地圖", 100, 200));
        internal PatrolNavigationRequest OwnJob()
        {
            var request = Request();
            var jobs = (Dictionary<string, PatrolNavigationRequest>)typeof(PatrolNavigationClient).GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!;
            jobs.Add(request.JobId, request);
            typeof(PatrolNavigationClient).GetField("lastJob", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client, request.JobId);
            return request;
        }
        internal Dictionary<string, PatrolNavigationRequest> OwnedJobs() =>
            (Dictionary<string, PatrolNavigationRequest>)typeof(PatrolNavigationClient).GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!;
        internal string? LastJob() => (string?)typeof(PatrolNavigationClient).GetField("lastJob", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client);
        internal Dictionary<string, object> CompletedBody(PatrolNavigationRequest request)
        {
            var data = CaptureBody(); data["jobId"] = request.JobId;
            data["arrivalRadius"] = request.ArrivalRadius;
            data["waypoint"] = new { mapId = 4, x = 100, y = 200 }; data["state"] = "Completed";
            data["terminal"] = true; data["completed"] = true; data["cleanupConfirmed"] = true;
            data["sameMap"] = true; data["standingSamples"] = 2; data["distance"] = 0; data["completionId"] = "same-completion";
            return data;
        }
        internal bool ChildIsNull() => typeof(PatrolNavigationClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client) is null;
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class OwnedCaptureFixture : IAsyncDisposable
    {
        internal readonly PatrolReadFixture Time = new();
        internal readonly PatrolNavigationClient Client;
        internal readonly List<string> PostIds = [], StatusIds = [];
        internal readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Posts, StatusReads, NativeRequests, TerminalStatusReads;
        internal string FirstId = "", Malformed = "", DirectFatal = "";
        internal bool NeverSettles, SourceChanged, BlockFirst, TimeoutFirst = true;
        internal bool DirectOk;
        internal HttpStatusCode DirectStatus = HttpStatusCode.Conflict;
        internal Action<Dictionary<string, object>>? MutateReceipt;
        private readonly bool forceDispatch;
        internal PatrolObservation Expected => new(Time.Source, Time.Self, 4, 100, 200, Time.Now, true);
        internal OwnedCaptureFixture(bool readOnly = true, bool forceDispatch = false)
        {
            this.forceDispatch = forceDispatch;
            Client = new(() => SourceChanged ? Time.Snapshot() with { Source = Time.Source with { SessionKey = "changed" } } : Time.Snapshot(),
                Send, () => Time.Now, Time.Delay, Time.Notices.Add, readOnly, forceDispatch);
        }
        private object Wire() => new { pid = Time.Source.ProcessId, creationFileTime = Time.Source.CreationFileTime.ToString(),
            imagePath = Time.Source.ImagePath, sha256 = Time.Source.DiskSha256, imageBase = Time.Source.ImageBase,
            imageSize = Time.Source.ImageSize, selfId = Time.Self.ToString() };
        private async Task<PatrolNavigationReply> Send(HttpMethod method, string path, object? payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (method == HttpMethod.Post && path == "/api/capture")
            {
                Posts++; var request = JsonSerializer.SerializeToElement(payload); string id = request.GetProperty("captureId").GetString()!;
                Need(PatrolNavigationClient.SourceMatches(request.GetProperty("expectedSource"), Time.Source, Time.Self));
                Need(!forceDispatch || request.TryGetProperty("forceDispatch", out var force) && force.ValueKind == JsonValueKind.True);
                PostIds.Add(id);
                if (Posts == 1)
                {
                    FirstId = id; FirstEntered.TrySetResult();
                    if (BlockFirst) await ReleaseFirst.Task.WaitAsync(token);
                    if (TimeoutFirst) throw new TaskCanceledException("Client timeout; server still reading");
                }
                if (DirectFatal.Length > 0)
                {
                    var failed = new Dictionary<string, object> { ["ok"] = DirectOk, ["captureId"] = id,
                        ["readOnly"] = true, ["inputSent"] = false, ["terminal"] = true, ["cleanupConfirmed"] = true,
                        ["accepted"] = true, ["state"] = "Failed", ["expectedSource"] = Wire(), ["error"] = DirectFatal };
                    MutateReceipt?.Invoke(failed);
                    return new(DirectStatus, JsonSerializer.SerializeToElement(failed));
                }
                Need(!TimeoutFirst || TerminalStatusReads >= 1);
                return Okay(new { ok = true, captureId = id, readOnly = true, inputSent = false, terminal = true,
                    cleanupConfirmed = true, state = "Completed", accepted = true, error = "", expectedSource = Wire(), source = Wire(), mapId = 4,
                    x = 101, y = 200, standing = true, sampledAtUtc = Time.Now.ToString("O") });
            }
            if (method == HttpMethod.Get && path.StartsWith("/api/capture-status?captureId=", StringComparison.Ordinal))
            {
                StatusReads++; string id = path[(path.IndexOf('=')+1)..]; StatusIds.Add(id);
                if (Malformed == "unknown") return new(HttpStatusCode.Conflict, JsonSerializer.SerializeToElement(new { ok = false, error = "CAPTURE_NOT_FOUND" }));
                bool done = !NeverSettles && (StatusReads >= 2 || Malformed.Length > 0 || DirectFatal.Length > 0);
                var data = new Dictionary<string, object> { ["ok"] = true, ["captureId"] = id, ["readOnly"] = true,
                    ["inputSent"] = false, ["terminal"] = done, ["cleanupConfirmed"] = done,
                    ["state"] = done ? "Completed" : "Reading", ["expectedSource"] = Wire(), ["error"] = "",
                    ["accepted"] = true,
                    ["x"] = 999, ["y"] = 999 };
                if (Malformed == "wrong_id") data["captureId"] = "f".PadLeft(32, '0');
                if (Malformed == "missing_readonly") data.Remove("readOnly");
                if (Malformed == "input_true") data["inputSent"] = true;
                if (Malformed == "missing_input") data.Remove("inputSent");
                if (Malformed == "missing_terminal") data.Remove("terminal");
                if (Malformed == "cleanup_false") data["cleanupConfirmed"] = false;
                if (Malformed == "source_changed") data["expectedSource"] = new { pid = 999 };
                if (Malformed == "wrong_state") data["state"] = "Moving";
                if (Malformed == "fatal") { data["state"] = "Failed"; data["error"] = "SERVER_ERROR"; }
                if (Malformed == "missing_error") { data["state"] = "Failed"; data.Remove("error"); }
                if (Malformed == "failed_empty") data["state"] = "Failed";
                if (Malformed == "rejected_empty") { data["state"] = "Rejected"; data["accepted"] = false; }
                if (Malformed == "accepted_false") data["accepted"] = false;
                if (DirectFatal.Length > 0) { data["state"] = "Failed"; data["error"] = DirectFatal; }
                MutateReceipt?.Invoke(data);
                if (data.GetValueOrDefault("terminal") is true) TerminalStatusReads++;
                return Okay(data);
            }
            NativeRequests++; throw new InvalidOperationException("Unexpected non-capture request: " + path);
        }
        public ValueTask DisposeAsync() { ReleaseFirst.TrySetResult(); return Client.DisposeAsync(); }
    }

    // Real client and production resumable controller, with only movement/shop
    // ports faked. This exercises the same client surviving a user start/reset.
    private sealed class CaptureFailurePatrolFixture : IAsyncDisposable
    {
        internal readonly OwnedCaptureFixture Capture = new(readOnly: false, forceDispatch: true) { TimeoutFirst = false };
        internal readonly CaptureFailureNavigation Navigation;
        internal readonly CaptureFailureMarket Market;
        internal readonly PatrolController Controller;
        internal readonly PatrolRouteSnapshot Route;
        internal int Cooldowns;
        internal CaptureFailurePatrolFixture()
        {
            Navigation = new(Capture); Market = new(Capture);
            Route = new() { Revision = Guid.NewGuid().ToString("N"), SavedAt = Capture.Time.Now, RoundCount = 1,
                RetryLimit = 1, ArrivalRadius = 1, Points = [new(Guid.NewGuid().ToString("N"), "離線取點復原", "4", "離線圖", 101, 200)] };
            var checkpoints = new PatrolCheckpointStore(Path.Combine(Path.GetTempPath(), "LcCaptureTerminalOffline", Guid.NewGuid().ToString("N")));
            Controller = new(Navigation, Market, () => Capture.Time.Now,
                (amount, token) => { token.ThrowIfCancellationRequested(); if (amount >= TimeSpan.FromSeconds(2)) Cooldowns++; return Capture.Time.Delay(amount, token); },
                TimeSpan.FromMilliseconds(100), forceDispatch: true, checkpointStore: checkpoints, autoResume: true);
        }
        public ValueTask DisposeAsync() => Capture.DisposeAsync();
    }

    private sealed class CaptureFailureNavigation(OwnedCaptureFixture capture) : IPatrolNavigation
    {
        internal int Starts;
        internal Action? AfterStart;
        private PatrolNavigationState current = new();
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
            => capture.Client.CaptureAsync(token, expected);
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Starts++;
            current = new() { JobId = request.JobId, WaypointId = request.Waypoint.PointId, Source = capture.Time.Source,
                SelfId = capture.Time.Self, MapId = 4, X = 101, Y = 200, SampledAt = capture.Time.Now,
                TargetX = request.Waypoint.X, TargetY = request.Waypoint.Y, ArrivalRadius = request.ArrivalRadius,
                State = "Completed", Completed = true, Terminal = true, CleanupConfirmed = true, WorkSettled = true, PositionVerified = true };
            AfterStart?.Invoke(); return Task.FromResult(current);
        }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Need(jobId == current.JobId); return Task.FromResult(current); }
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token)
            => GetStateAsync(expectedJobId, token);
    }

    private sealed class CaptureFailureMarket(OwnedCaptureFixture capture) : IPatrolMarket
    {
        internal int Calls, Scans;
        private ShopCommandResponse current = new();
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            var source = capture.Time.Source;
            if (request.Command == ShopCommands.RefreshTargets)
                return Task.FromResult(current with { RequestId = request.RequestId, State = "TargetsRefreshed",
                    CurrentSourceSessionKey = source.SessionKey, TargetSnapshot = new() { SnapshotId = "capture-reset-targets",
                        SourceSessionKey = source.SessionKey, SelfId = capture.Time.Self, RefreshedAt = capture.Time.Now, CanDispatch = true } });
            if (request.Command == ShopCommands.ScanVisible)
            {
                Scans++;
                current = new() { RequestId = request.RequestId, State = "ScanStarted", WorkSettled = true,
                    CurrentSourceSessionKey = source.SessionKey,
                    Scan = new() { JobId = request.RequestId, SourceSessionKey = source.SessionKey,
                        TargetSnapshotId = request.TargetSnapshotId, Running = false },
                    SavedScan = new() { PlanId = "capture-reset-plan-" + Scans, SourceSessionKey = source.SessionKey,
                        TotalPages = 1, SearchedPages = 1, RemainingPages = 0, AvailablePages = 0, CanContinue = false },
                    TargetSnapshot = new() { SnapshotId = request.TargetSnapshotId!, SourceSessionKey = source.SessionKey,
                        SelfId = capture.Time.Self, RefreshedAt = capture.Time.Now, CanDispatch = true } };
                return Task.FromResult(current);
            }
            Need(request.Command is ShopCommands.GetState or ShopCommands.CancelScan);
            if (request.Command == ShopCommands.CancelScan) Need(request.ExpectedScanJobId == current.Scan.JobId);
            return Task.FromResult(current with { RequestId = request.RequestId });
        }
    }
}
