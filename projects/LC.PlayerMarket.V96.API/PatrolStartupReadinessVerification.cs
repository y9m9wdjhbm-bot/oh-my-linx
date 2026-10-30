using System.Security.Principal;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

// Pure offline acceptance tests: the only start callback increments a counter.
internal static class PatrolStartupReadinessVerification
{
    internal static int RunReport(string output)
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = RunAsync().GetAwaiter().GetResult();
        var report = new
        {
            Version = 88, Success = checks.All(x => x.Passed), IsAdministrator = admin,
            Passed = checks.Count(x => x.Passed), Failed = checks.Count(x => !x.Passed), Checks = checks,
            LcReads = 0, NativeCalls = 0, GameInput = 0, ProductStarted = false,
            Scope = "Production startup policy and permit with fake clock, sampler, context and start callback."
        };
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
        return report.Success ? 0 : 1;
    }

    private static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "Offline behavioral oracle passed; no product/game action.")); }
            catch (Exception ex) { checks.Add(new(name, false, ex.ToString())); }
        }

        await Check("Cold refusal then fresh ready starts exactly once", async () =>
        {
            var f = new Fixture();
            var result = await f.Run();
            Require(result.Ready && result.Attempts == 2 && f.Calls == 2, "Must sample a new ready response after refusal.");
            Require(f.PrepareFlags.SequenceEqual(new[] { true, false }), "Only first sample prepares; retry checks afresh.");
            Require(f.Delays.Count == 1 && f.Delays[0].TotalMilliseconds >= 2000, "Cold refusal requires cooldown.");
            int starts = 0;
            Require(Commit(result, f, () => starts++), "Valid final observation should commit.");
            Require(!Commit(result, f, () => starts++) && starts == 1, "Permit must be single-use.");
            Require(!result.FinalObservation!.ReadinessResponse!.PreflightNotEnteredConfirmed,
                "Legal readonly proof does not fabricate scan-only no-entry flags.");
        });
        await Check("Recovery beyond ten seconds remains bounded", async () =>
        {
            var f = new Fixture();
            f.OnSample = (_, _, _) =>
            {
                if (f.Calls <= 4) { f.Elapsed += 1000; return Task.FromResult(f.Refused()); }
                return Task.FromResult(f.Ready());
            };
            var r = await f.Run();
            Require(r.Ready && r.Attempts == 5 && r.ElapsedMilliseconds >= 12000 && r.ElapsedMilliseconds < 30000,
                "A twelve-second natural recovery must not inherit the old ten-second design.");
        });
        await Check("Cancellation before first sample", async () =>
        {
            var f = new Fixture(); using var c = new CancellationTokenSource(); c.Cancel();
            var r = await f.Run(c.Token);
            Require(!r.Ready && r.Permit is null && f.Calls == 0 && f.Delays.Count == 0, "Cancelled startup must not sample/start.");
        });
        await Check("Cancellation during cooldown prevents new sample", async () =>
        {
            var f = new Fixture(); using var c = new CancellationTokenSource();
            f.OnDelay = (_, _) => c.Cancel();
            var r = await f.Run(c.Token);
            Require(!r.Ready && r.Permit is null && f.Calls == 1, "Stop during wait must prevent the ready follow-up.");
        });
        await Check("Cancellation reaches in-flight readonly sample", async () =>
        {
            var f = new Fixture(); using var c = new CancellationTokenSource();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnSample = async (_, _, token) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return f.Ready();
            };
            Task<PatrolStartupReadinessResult> pending = f.Run(c.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); c.Cancel();
            var r = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!r.Ready && r.Permit is null && f.Calls == 1, "In-flight cancellation cannot mint a permit.");
        });
        await Check("Source and character changes stop startup", async () =>
        {
            foreach (int kind in new[] { 0, 1 })
            {
                var f = new Fixture();
                f.OnDelay = (_, _) => f.Current = kind switch
                {
                    0 => f.Expected with { Source = f.Expected.Source with { SessionKey = "different-session" } },
                    _ => f.Expected with { SelfId = f.Expected.SelfId + 1 }
                };
                var r = await f.Run();
                Require(!r.Ready && r.Permit is null && f.Calls <= 2, "Changed context must never start.");
            }
        });
        await Check("Unsafe or unknown refusals never enter retry", async () =>
        {
            var mutations = new Func<PatrolStartupReadinessObservation, PatrolStartupReadinessObservation>[]
            {
                o => o with { SettledState = o.SettledState! with { SessionBlocked = true } },
                o => o with { SettledState = o.SettledState! with { WorkSettled = false } },
                o => o with { SettledState = o.SettledState! with { Scan = new() { Running = true } } },
                o => o with { SettledState = o.SettledState! with { SavedScan = new() { PlanId = "pending", SourceSessionKey = o.Current!.Source.SessionKey, UncertainPages = 1 } } },
                o => o with { ReadinessResponse = o.ReadinessResponse! with { NativeEntered = true } },
                o => o with { ReadinessResponse = o.ReadinessResponse! with { NativeReadiness = null } },
                o => ChangeProof(o, p => p with { ReadOnly = false }),
                o => ChangeProof(o, p => p with { NativeRequests = 1 }),
                o => ChangeProof(o, p => p with { ProbeId = "" }),
                o => ChangeProof(o, p => p with { Code = "Unknown" }),
                o => ChangeProof(o, p => p with { PendingMask = 1 }),
                o => ChangeProof(o, p => p with { Source = null }),
                o => o with { NoOutstandingUiWork = false },
                o => o with { ContextFresh = false }
            };
            for (int i = 0; i < mutations.Length; i++)
            {
                var f = new Fixture(); var mutate = mutations[i];
                f.OnSample = (_, _, _) => Task.FromResult(f.Calls == 1 ? mutate(f.Refused()) : f.Ready());
                var r = await f.Run();
                Require(!r.Ready && r.Permit is null && f.Calls == 1 && f.Delays.Count == 0, $"Unsafe case {i} was retried/accepted.");
            }
        });
        await Check("Final permit rechecks context proof reference freshness and stop", async () =>
        {
            for (int kind = 0; kind < 8; kind++)
            {
                var f = new Fixture(); f.OnSample = (_, _, _) => Task.FromResult(f.Ready());
                var r = await f.Run(); Require(r.Ready && r.Permit is not null, "Fixture must first be ready.");
                var o = r.FinalObservation!; var now = f.Now; using var c = new CancellationTokenSource();
                switch (kind)
                {
                    case 0: f.Current = f.Expected with { Source = f.Expected.Source with { SessionKey = "changed" } }; break;
                    case 1: f.Current = f.Expected with { SelfId = f.Expected.SelfId + 1 }; break;
                    case 2: o = o with { ContextFresh = false }; break;
                    case 3: o = o with { NoOutstandingUiWork = false }; break;
                    case 4: o = o with { ReadinessResponse = o.ReadinessResponse! with { } }; break;
                    case 5: o = o with { SettledState = o.SettledState! with { } }; break;
                    case 6: now = o.ReadinessResponse!.NativeReadiness!.ExpiresAt.AddTicks(1); break;
                    default: c.Cancel(); break;
                }
                int starts = 0;
                Require(!r.Permit!.TryCommit(o, now, c.Token, () => starts++, out _) && starts == 0,
                    $"Final invalidation case {kind} executed start.");
            }
        });
        await Check("Six-attempt ceiling has no seventh sample", async () =>
        {
            var f = new Fixture(); f.OnSample = (_, _, _) => Task.FromResult(f.Refused());
            var r = await f.Run();
            Require(!r.Ready && r.Permit is null && r.Attempts == 6 && f.Calls == 6 && f.Delays.Count == 5,
                "Persistent cold code must end at six completed observations.");
            Require(f.Delays.All(x => x.TotalMilliseconds >= 2000) && f.Elapsed < 30000, "Wait must remain bounded.");
        });
        await Check("Monotonic total budget includes sampler time", async () =>
        {
            var f = new Fixture();
            f.OnSample = (_, _, _) =>
            {
                f.Elapsed += f.Calls == 1 ? 19000 : 10000;
                if (f.Calls > 1) f.WallShift = TimeSpan.FromHours(-1);
                return Task.FromResult(f.Calls == 1 ? f.Refused() : f.Ready());
            };
            var r = await f.Run();
            Require(!r.Ready && r.Permit is null && f.Calls == 2 && r.ElapsedMilliseconds >= 30000,
                "Fresh wall-clock response cannot extend the monotonic startup budget.");
        });
        await Check("Initially ready requires no cooldown", async () =>
        {
            var f = new Fixture(); f.OnSample = (_, _, _) => Task.FromResult(f.Ready());
            var r = await f.Run();
            Require(r.Ready && r.Attempts == 1 && f.Calls == 1 && f.Delays.Count == 0 &&
                PatrolStartupReadinessPolicy.FinalValid(f.Expected, r.FinalObservation!, f.Now), "Fresh ready should use the original start path immediately.");
        });
        await Check("Concurrent commits invoke the start callback once", async () =>
        {
            var f = new Fixture(); f.OnSample = (_, _, _) => Task.FromResult(f.Ready());
            var r = await f.Run(); Require(r.Ready && r.Permit is not null, "Ready permit required.");
            int starts = 0;
            var commits = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
                r.Permit!.TryCommit(r.FinalObservation!, f.Now, CancellationToken.None,
                    () => Interlocked.Increment(ref starts), out _))));
            Require(commits.Count(x => x) == 1 && starts == 1, "Concurrent callers must not duplicate startup.");
        });
        return checks.ToArray();
    }

    private static bool Commit(PatrolStartupReadinessResult result, Fixture f, Action start)
        => result.Permit is not null && result.Permit.TryCommit(result.FinalObservation!, f.Now, CancellationToken.None, start, out _);
    private static PatrolStartupReadinessObservation ChangeProof(PatrolStartupReadinessObservation o,
        Func<ShopNativeReadiness, ShopNativeReadiness> change)
        => o with { ReadinessResponse = o.ReadinessResponse! with { NativeReadiness = change(o.ReadinessResponse.NativeReadiness!) } };
    private static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Epoch = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        internal readonly PatrolStartupReadinessContext Expected = new(new SourceIdentity(900001, 123456,
            @"C:\offline\LC.exe", "0x140000000", 1792498244, 66535424,
            "79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9", "offline-patrol-startup"), 12345);
        internal PatrolStartupReadinessContext? Current;
        internal long Elapsed;
        internal TimeSpan WallShift;
        internal int Calls;
        internal readonly List<bool> PrepareFlags = new();
        internal readonly List<TimeSpan> Delays = new();
        internal Func<int, bool, CancellationToken, Task<PatrolStartupReadinessObservation>>? OnSample;
        internal Action<TimeSpan, CancellationToken>? OnDelay;
        internal DateTimeOffset Now => Epoch.AddMilliseconds(Elapsed).Add(WallShift);
        internal Fixture() { Current = Expected; }
        internal Task<PatrolStartupReadinessResult> Run(CancellationToken token = default)
            => PatrolStartupReadinessPolicy.WaitAsync(Expected, Sample, () => Current, () => Now, () => Elapsed, Delay, token);
        private Task<PatrolStartupReadinessObservation> Sample(bool prepare, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; PrepareFlags.Add(prepare);
            if (Calls > 16) throw new InvalidOperationException("Harness sample bound exceeded.");
            return OnSample?.Invoke(Calls, prepare, token) ?? Task.FromResult(Calls == 1 ? Refused() : Ready());
        }
        private Task Delay(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Delays.Add(duration); OnDelay?.Invoke(duration, token);
            token.ThrowIfCancellationRequested(); Elapsed += checked((long)duration.TotalMilliseconds);
            return Task.CompletedTask;
        }
        internal PatrolStartupReadinessObservation Refused() => Observation(false);
        internal PatrolStartupReadinessObservation Ready() => Observation(true);
        private PatrolStartupReadinessObservation Observation(bool ready)
        {
            var proof = new ShopNativeReadiness
            {
                BuildCompatible = true, Ready = ready, Code = ready ? "NativeReady" : "NativeCodeUnavailable",
                FailureRva = ready ? null : "0x7710F0", Source = Expected.Source,
                SampledAt = Now, ExpiresAt = Now.AddMilliseconds(HubProtocol.FreshMilliseconds),
                ProbeId = "offline-probe-" + Calls, ReadOnly = true, NativeRequests = 0, PendingMask = 0,
                CurrentRuntimeCodeVerified = ready, EvidenceMode = "CurrentRuntimeCode",
                ColdCodePolicyId = ShopNativeCurrent79D1Policy.Id,
                ColdCodeCertificateSha256 = ShopNativeCurrent79D1Policy.CertificateSha256
            };
            var response = new ShopCommandResponse
            {
                RequestId = "readiness-" + Calls, State = ready ? "NativeReady" : "Rejected", At = Now,
                CurrentSourceSessionKey = Expected.Source.SessionKey, NativeReadiness = proof,
                WorkSettled = false,
                FailureCode = ready ? null : "NativeCodeUnavailable", FailureScope = ready ? null : "Global",
                FailurePhase = ready ? null : "BeforePublish", FailureRva = ready ? null : "0x7710F0"
            };
            var state = new ShopCommandResponse
            {
                RequestId = "state-" + Calls, State = "State", At = Now,
                CurrentSourceSessionKey = Expected.Source.SessionKey, WorkSettled = true
            };
            return new(Current, response, state, true, true);
        }
    }
}
