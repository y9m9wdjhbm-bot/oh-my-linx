using MemoryHub;

namespace LcPlayerMarket;

internal static class PatrolStartupIdentityRecoverySelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new("startup_identity_recovery_" + name, true, "Production parser/gate/policy with fake snapshots/clock/ports only; no LC/native/input.")); }
            catch (Exception error) { checks.Add(new("startup_identity_recovery_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        await Check("stale_2306ms_never_returns_expected_then_fresh_sample_recovers", async () =>
        {
            var f = new Fixture { GapUntil = 700 };
            Need(f.Current() is null && f.Read().FreshSelfId is null, "Stale self was promoted to expected authority.");
            Need(await f.Recover(default) && f.Elapsed == 700 && f.Current() == f.Expected,
                "New same-source self did not recover within the bounded gap.");
        });
        await Check("persistent_unknown_expires_exactly_four_seconds_and_cannot_revive", async () =>
        {
            var f = new Fixture { GapUntil = int.MaxValue };
            Need(!await f.Recover(default) && f.Elapsed == 4000, "Unknown identity was not bounded.");
            f.GapUntil = 0;
            Need(f.Current() is null && f.Gate.Invalidated, "Expired start intent revived on a later fresh sample.");
        });
        foreach (bool changeSelf in new[] { false, true })
        await Check((changeSelf ? "role" : "source") + "_brief_positive_change_is_sticky", async () =>
        {
            var f = new Fixture { GapUntil = 700 };
            f.OnDelay = () =>
            {
                if (f.Elapsed == 100)
                    f.Gate.Observe(changeSelf ? new(f.Source, 54321UL) : new(f.Source with { CreationFileTime = 123457 }, null));
            };
            Need(!await f.Recover(default) && f.Elapsed == 100 && f.Gate.Invalidated,
                "Positive changed raw identity waited or was ignored.");
            f.GapUntil = 0;
            Need(f.Current() is null, "Restoring the old ID revived a retired intent.");
        });
        await Check("cancellation_during_unknown_wait_prevents_admission", async () =>
        {
            var f = new Fixture { GapUntil = 700 }; using var cancel = new CancellationTokenSource();
            f.OnDelay = () => cancel.Cancel(); bool cancelled = false;
            try { await f.Recover(cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Need(cancelled && f.Elapsed == 100 && f.Calls == 0, "Cancelled identity wait continued to probing.");
        });
        await Check("no_readiness_before_actual_fresh_identity", async () =>
        {
            var f = new Fixture { GapUntil = 700 };
            f.OnDelay = () => Need(f.Calls == 0, "Readiness started while identity was unavailable.");
            var result = await f.Policy();
            Need(result.Ready && f.Calls == 1 && f.Elapsed == 700 && result.FinalObservation!.ReadinessResponse!.At == f.Now,
                "Policy failed to use a newly sampled same identity.");
        });
        await Check("original_pending_probe_survives_gap_without_overlap", async () =>
        {
            var f = new Fixture(); var pending = new TaskCompletionSource<PatrolStartupReadinessObservation>();
            PatrolStartupReadinessObservation? original = null;
            f.OnSample = () => { original = f.Observation(); f.GapUntil = 700; return pending.Task; };
            f.OnDelay = () => { Need(f.Calls == 1, "Original readonly probe was overlapped."); if (f.Elapsed >= 100) pending.TrySetResult(original!); };
            var result = await f.Policy();
            Need(result.Ready && f.Calls == 1 && f.Elapsed == 700
                && result.FinalObservation!.ReadinessResponse!.At == f.Epoch,
                "Same original flight was replaced or timestamps were renewed.");
        });
        await Check("expired_ready_after_gap_requires_a_new_readonly_check", async () =>
        {
            var f = new Fixture(); var pending = new TaskCompletionSource<PatrolStartupReadinessObservation>();
            PatrolStartupReadinessObservation? original = null;
            f.OnSample = () =>
            {
                if (f.Calls > 1) return Task.FromResult(f.Observation());
                original = f.Observation(); f.GapUntil = 2500; return pending.Task;
            };
            f.OnDelay = () => { if (f.Elapsed >= 100) pending.TrySetResult(original!); if (f.Elapsed < 2500) Need(f.Calls == 1, "Probed before fresh identity."); };
            var result = await f.Policy();
            Need(result.Ready && f.Calls == 2 && f.Prepare.SequenceEqual(new[] { true, false })
                && original!.ReadinessResponse!.At == f.Epoch && result.FinalObservation!.ReadinessResponse!.At == f.Now
                && !ReferenceEquals(original.ReadinessResponse, result.FinalObservation.ReadinessResponse),
                "Expired readonly proof was consumed/retimed instead of resampled.");
        });
        await Check("identity_recovery_does_not_reset_outer_thirty_second_budget", async () =>
        {
            var f = new Fixture();
            f.OnSample = () => { var old = f.Observation(); f.Elapsed = 28000; f.GapUntil = 30000; return Task.FromResult(old); };
            var result = await f.Policy();
            Need(!result.Ready && result.Permit is null && result.Code == "TimedOut" && f.Calls == 1 && f.Elapsed == 30000,
                "Identity recovery reset the original global budget or admitted after its deadline.");
        });
        await Check("prewarm_before_capture_waits_for_new_identity", async () =>
        {
            var f = new Fixture { GapUntil = 500 }; int captures = 0;
            var pose = await PatrolNavigationPrewarm.RunAsync(f.Expected, _ =>
            { captures++; return Task.FromResult(new PatrolObservation(f.Source, 12345, 4, 32610, 32778, f.Now, true)); },
                f.Current, () => f.Now, default, f.Recover);
            Need(captures == 1 && f.Elapsed == 500 && pose.SampledAt == f.Now && f.Calls == 0,
                "Prewarm reused stale identity or sent a shop request.");
        });
        await Check("prewarm_after_capture_does_not_retime_pose_while_waiting", async () =>
        {
            var f = new Fixture(); PatrolObservation? original = null;
            var pose = await PatrolNavigationPrewarm.RunAsync(f.Expected, _ =>
            { original = new(f.Source, 12345, 4, 32610, 32778, f.Now, true); f.GapUntil = 2500; return Task.FromResult(original); },
                f.Current, () => f.Now, default, f.Recover);
            Need(ReferenceEquals(pose, original) && pose.SampledAt == f.Epoch && f.Elapsed == 2500 && f.Current() == f.Expected,
                "Prewarm timestamp was renewed instead of reacquiring identity.");
        });
        return checks;
    }

    private sealed class Fixture
    {
        internal readonly DateTimeOffset Epoch = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        internal readonly SourceIdentity Source = new(900001, 123456, @"C:\offline\LC.exe", "0x140000000", 1793101316, 66609152,
            "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC", "offline-fresh-identity");
        internal readonly PatrolStartupReadinessContext Expected;
        internal readonly PatrolStartupIdentityGate Gate;
        internal int Elapsed, GapUntil, Calls;
        internal readonly List<bool> Prepare = [];
        internal Action? OnDelay;
        internal Func<Task<PatrolStartupReadinessObservation>>? OnSample;
        internal DateTimeOffset Now => Epoch.AddMilliseconds(Elapsed);
        internal Fixture() { Expected = new(Source, 12345); Gate = new(Expected, () => Elapsed); }
        internal LiveStartupIdentityObservation Read()
        {
            var snapshot = new HubSnapshot { Source = Source, State = "Reading", PublishedAt = Now,
                Fields = new() { ["selfId"] = FieldReading.Observed(12345UL, Elapsed < GapUntil ? Now.AddMilliseconds(-2306) : Now),
                    ["position"] = FieldReading.Unknown("Independent pose gap") } };
            return LiveStartupIdentityContext.Read(snapshot, Now);
        }
        internal PatrolStartupReadinessContext? Current() => Gate.Observe(Read());
        internal Task<bool> Recover(CancellationToken token) => Gate.WaitFreshAsync(Read, Delay, token);
        internal Task Delay(TimeSpan wait, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Elapsed += (int)wait.TotalMilliseconds; OnDelay?.Invoke();
            token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }
        internal Task<PatrolStartupReadinessResult> Policy() => PatrolStartupReadinessPolicy.WaitAsync(Expected,
            (prepare, _) => { Calls++; Prepare.Add(prepare); return OnSample?.Invoke() ?? Task.FromResult(Observation()); },
            Current, () => Now, () => Elapsed, Delay, default, continuous: true, recoverContext: Recover);
        internal PatrolStartupReadinessObservation Observation()
        {
            var proof = new ShopNativeReadiness { BuildCompatible = true, Ready = true, Code = "NativeReady", Source = Source,
                SampledAt = Now, ExpiresAt = Now.AddMilliseconds(HubProtocol.FreshMilliseconds), ProbeId = "readonly-" + Calls,
                ReadOnly = true, NativeRequests = 0, PendingMask = 0, CurrentRuntimeCodeVerified = true,
                EvidenceMode = "CurrentRuntimeCode", ColdCodePolicyId = ShopNativeCurrentB47CPolicy.Id,
                ColdCodeCertificateSha256 = ShopNativeCurrentB47CPolicy.CertificateSha256 };
            var readiness = new ShopCommandResponse { RequestId = "probe-" + Calls, State = "NativeReady", At = Now,
                CurrentSourceSessionKey = Source.SessionKey, NativeReadiness = proof };
            var settled = new ShopCommandResponse { RequestId = "state-" + Calls, State = "State", At = Now,
                CurrentSourceSessionKey = Source.SessionKey, WorkSettled = true };
            var current = Current(); return new(current, readiness, settled, current == Expected, true);
        }
    }
}
