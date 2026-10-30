using MemoryHub;

namespace LcPlayerMarket;

// Production policy with injected clock/context/sampler. No helper, native call,
// LC process, navigation, or product entry point is used by this suite.
internal static class PatrolStartupContinuousVerification
{
    internal static int RunReport(string output)
    {
        bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = RunAsync().GetAwaiter().GetResult();
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        System.Text.Json.JsonSerializer.Serialize(file, new { Version = 92, IsAdministrator = admin,
            Success = checks.All(c => c.Passed), Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed),
            Checks = checks, LcReads = 0, NativeCalls = 0, GameInput = 0, ProductStarted = false }, HubProtocol.Json);
        file.Flush(true);
        return checks.All(c => c.Passed) ? 0 : 1;
    }

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "Finite startup, fake clock/commands only; no LC/native/input.")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("startup_ready_within_six_has_one_prepare", async () =>
        {
            var f = new Fixture { ReadyAt = 6 }; var result = await f.Run();
            Need(result.Ready && result.Attempts == 6 && f.Elapsed == 10000, "Fresh sixth probe should pass.");
            Need(f.PrepareFlags.SequenceEqual(Enumerable.Range(0, 6).Select(i => i == 0)), "Prepare was repeated.");
        });
        await Check("startup_persistent_unavailable_stops_at_six", async () =>
        {
            var f = new Fixture { ReadyAt = 7 }; var result = await f.Run();
            Need(!result.Ready && result.Permit is null && result.Code == "AttemptLimit" && f.Calls == 6
                && f.Elapsed == 10000, "Persistent unavailable retried without a bound.");
        });
        await Check("startup_pending_probe_deadline_cancels_original_without_replacement", async () =>
        {
            var f = new Fixture(); var pending = new TaskCompletionSource<PatrolStartupReadinessObservation>();
            CancellationToken owned = default; f.OnSample = (_, t) => { owned = t; return pending.Task; };
            var result = await f.Run();
            Need(result.Code == "TimedOut" && !result.Ready && result.Permit is null && f.Calls == 1
                && f.Elapsed == 30000 && owned.IsCancellationRequested, "Deadline did not stop the original wait.");
            pending.SetResult(f.Observation(true)); await Task.Yield();
            Need(result.Permit is null && f.Calls == 1, "Late helper completion revived intent or replaced probe.");
        });
        await Check("startup_cancel_and_context_loss_stop_owned_wait", async () =>
        {
            foreach (bool change in new[] { false, true })
            {
                var f = new Fixture(); using var stop = new CancellationTokenSource();
                var pending = new TaskCompletionSource<PatrolStartupReadinessObservation>(); CancellationToken owned = default;
                f.OnSample = (_, t) => { owned = t; return pending.Task; };
                f.OnDelay = _ => { if (f.Elapsed >= 500) { if (change) f.Current = null; else stop.Cancel(); } };
                var result = await f.Run(stop.Token);
                Need(result.Code == (change ? "ContextChangedOrUnknown" : "Cancelled") && owned.IsCancellationRequested
                    && f.Calls == 1 && result.Permit is null, "Lost/cancelled context issued another sample.");
                pending.TrySetResult(f.Observation(true));
            }
        });
        await Check("startup_changed_unknown_identity_latch_never_revives", () =>
        {
            var f = new Fixture();
            foreach (var changed in new PatrolStartupReadinessContext?[] { null, f.Expected with { SelfId = 2 },
                f.Expected with { Source = f.Expected.Source with { SessionKey = "changed" } } })
            {
                var latch = new PatrolStartupContextLatch(f.Expected);
                Need(latch.Observe(f.Expected) && !latch.Observe(changed) && !latch.Observe(f.Expected), "Old intent revived.");
            }
            return Task.CompletedTask;
        });
        await Check("startup_unknown_or_unsettled_is_not_retryable", async () =>
        {
            foreach (string mutation in new[] { "missing", "native", "pending", "unsettled", "blocked", "ui" })
            {
                var f = new Fixture { ReadyAt = 20 };
                f.OnSample = (_, _) => { var o = f.Observation(false); return Task.FromResult(mutation switch {
                    "missing" => o with { ReadinessResponse = o.ReadinessResponse! with { NativeReadiness = null } },
                    "native" => o with { ReadinessResponse = o.ReadinessResponse! with { NativeEntered = true } },
                    "pending" => o with { ReadinessResponse = o.ReadinessResponse! with { NativeReadiness = o.ReadinessResponse.NativeReadiness! with { PendingMask = 1 } } },
                    "unsettled" => o with { SettledState = o.SettledState! with { WorkSettled = false } },
                    "blocked" => o with { SettledState = o.SettledState! with { SessionBlocked = true } },
                    _ => o with { NoOutstandingUiWork = false } }); };
                var result = await f.Run();
                Need(result.Code == "ReadinessNotRetryable" && f.Calls == 1 && result.Permit is null, "Unknown work got a retry.");
            }
        });
        await Check("startup_pending_sampler_is_not_overlapped", async () =>
        {
            var f = new Fixture(); var pending = new TaskCompletionSource<PatrolStartupReadinessObservation>();
            f.OnSample = (_, _) => pending.Task;
            f.OnDelay = _ => { if (f.Elapsed >= 750) pending.TrySetResult(f.Observation(true)); };
            var result = await f.Run(); Need(result.Ready && f.Calls == 1, "An in-flight sample was overlapped.");
        });
        await Check("startup_late_or_stale_result_never_gets_permit", async () =>
        {
            foreach (bool beyondBudget in new[] { false, true })
            {
                var f = new Fixture(); f.OnSample = (_, _) => { var o = f.Observation(true);
                    f.Elapsed += beyondBudget ? 30000 : HubProtocol.FreshMilliseconds + 1; return Task.FromResult(o); };
                var result = await f.Run(); Need(!result.Ready && result.Permit is null && f.Calls == 1, "Old evidence was renewed.");
            }
        });
        await Check("startup_concurrent_commits_start_exactly_once", async () =>
        {
            var f = new Fixture(); var result = await f.Run(); int starts = 0;
            var commits = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Commit(result, f, () => Interlocked.Increment(ref starts)))));
            Need(commits.Count(x => x) == 1 && starts == 1, "Permit started twice.");
        });
        await Check("startup_permit_monotonic_expiry_retained", async () =>
        {
            var f = new Fixture(); var result = await f.Run(); var sampled = f.Now; f.Elapsed += HubProtocol.FreshMilliseconds + 1;
            Need(!result.Permit!.TryCommit(result.FinalObservation!, sampled, default, () => throw new Exception("started"), out _), "Expired permit admitted.");
        });
        return checks.ToArray();
    }

    private static bool Commit(PatrolStartupReadinessResult result, Fixture f, Action action) => result.Permit is not null
        && result.Permit.TryCommit(result.FinalObservation!, f.Now, CancellationToken.None, action, out _);
    private static void Need(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Epoch = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        internal readonly PatrolStartupReadinessContext Expected = new(new SourceIdentity(900001, 123456,
            @"D:\offline\LC.exe", "0x140000000", 1792498244, 66535424,
            "79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9", "offline-continuous-startup"), 1);
        internal PatrolStartupReadinessContext? Current;
        internal long Elapsed;
        internal int Calls, ReadyAt = 2;
        internal readonly List<bool> PrepareFlags = new();
        internal readonly List<PatrolStartupReadinessProgress> Progress = new();
        internal Func<bool, CancellationToken, Task<PatrolStartupReadinessObservation>>? OnSample;
        internal Action<TimeSpan>? OnDelay;
        internal DateTimeOffset Now => Epoch.AddMilliseconds(Elapsed);
        internal Fixture() { Current = Expected; }
        internal Task<PatrolStartupReadinessResult> Run(CancellationToken token = default) =>
            PatrolStartupReadinessPolicy.WaitAsync(Expected, Sample, () => Current, () => Now, () => Elapsed, Delay, token, Progress.Add, continuous: true);
        private Task<PatrolStartupReadinessObservation> Sample(bool prepare, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; PrepareFlags.Add(prepare);
            if (Calls > 24) throw new InvalidOperationException("Fixture sample bound exceeded.");
            return OnSample?.Invoke(prepare, token) ?? Task.FromResult(Observation(Calls >= ReadyAt));
        }
        private Task Delay(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); OnDelay?.Invoke(duration); token.ThrowIfCancellationRequested();
            Elapsed += checked((long)duration.TotalMilliseconds); return Task.CompletedTask;
        }
        internal PatrolStartupReadinessObservation Observation(bool ready)
        {
            var proof = new ShopNativeReadiness { BuildCompatible = true, Ready = ready,
                Code = ready ? "NativeReady" : "NativeCodeUnavailable", FailureRva = ready ? null : "0x731AA0", Source = Expected.Source,
                SampledAt = Now, ExpiresAt = Now.AddMilliseconds(HubProtocol.FreshMilliseconds), ProbeId = "probe-" + Calls,
                ReadOnly = true, NativeRequests = 0, PendingMask = 0, CurrentRuntimeCodeVerified = ready,
                EvidenceMode = "CurrentRuntimeCode", ColdCodePolicyId = ShopNativeCurrent79D1Policy.Id,
                ColdCodeCertificateSha256 = ShopNativeCurrent79D1Policy.CertificateSha256 };
            var response = new ShopCommandResponse { RequestId = "readiness-" + Calls, State = ready ? "NativeReady" : "Rejected", At = Now,
                CurrentSourceSessionKey = Expected.Source.SessionKey, NativeReadiness = proof,
                FailureCode = ready ? null : "NativeCodeUnavailable", FailureScope = ready ? null : "Global",
                FailurePhase = ready ? null : "BeforePublish", FailureRva = ready ? null : "0x731AA0" };
            var settled = new ShopCommandResponse { RequestId = "state-" + Calls, State = "State", At = Now,
                CurrentSourceSessionKey = Expected.Source.SessionKey, WorkSettled = true };
            return new(Current, response, settled, true, true);
        }
    }
}
