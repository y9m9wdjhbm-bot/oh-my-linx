namespace MemoryHub.Reader;

public sealed record ShopDispatchTimeoutCheck(string Name, bool Passed, string Detail);

/// <summary>Deterministic fake reads only. This test never constructs CurrentLcSource or opens a process.</summary>
public static class ShopDispatchTimeoutSelfTest
{
    public static IReadOnlyList<ShopDispatchTimeoutCheck> Run()
    {
        var checks = new List<ShopDispatchTimeoutCheck>();
        var source = new SourceIdentity(4242, 987654321, "offline-LC", "0x100000", 1, 4096,
            new string('A', 64), "offline-dispatch-timeout");
        var now = DateTimeOffset.UtcNow;
        string[] fields = ["selfId", "position", "nearbyPlayers", "playerShopStates", "personalShopListing"];
        ReadResult Timeout() => new(null, fields.ToDictionary(key => key, _ => FieldReading.Unknown("TIME_LIMIT")), "Unknown", "offline deadline")
            { ReadFailureCode = "TIME_LIMIT", ReadFailureNativeError = 0 };
        ReadResult Good() => new(source, fields.ToDictionary(key => key, _ => FieldReading.Observed(1, now)), "Reading", "offline closed read");
        ReadResult Changed()
        {
            var result = Good();
            foreach (string key in new[] { "nearbyPlayers", "playerShopStates" })
                result.Fields[key] = FieldReading.Unknown("ACTOR_COLLECTION_CHANGED：offline");
            return result with { RetryableFields = ["nearbyPlayers", "playerShopStates"] };
        }
        void Check(string name, Action run)
        {
            try { run(); checks.Add(new(name, true, "fake reads only; native calls=0")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        static void Need(bool pass, string detail) { if (!pass) throw new InvalidOperationException(detail); }
        static bool TimeoutException(Exception? error) => error?.GetType().FullName == "MemoryHub.Reader.ShopDispatchReadTimeoutException";
        ReadResult Recover(Func<ReadResult> capture, Action? validate = null, CancellationToken token = default,
            Action<int, CancellationToken>? delay = null, Func<double>? elapsed = null, Action<ShopDispatchRetryEvent>? log = null)
            => ShopDispatchReadRecovery.Read(() =>
            {
                var result = capture();
                ShopDispatchSourceValidation.RequireCurrent(result, source);
                return result;
            }, source, validate ?? (() => { }), token, log ?? (_ => { }), () => now, elapsed ?? (() => 0), delay ?? ((_, _) => { }));

        Check("dispatch_timeout_then_fresh_read_recovers_before_publication", () =>
        {
            int reads = 0, delays = 0;
            var result = Recover(() => ++reads == 1 ? Timeout() : Good(), delay: (_, _) => delays++);
            Need(reads == 2 && delays == 1 && result.Source == source, $"reads={reads}; delays={delays}");
        });
        Check("dispatch_timeout_shared_three_pass_limit_with_actor_recovery", () =>
        {
            int reads = 0, delays = 0;
            var result = Recover(() => ++reads switch { 1 => Timeout(), 2 => Changed(), _ => Good() }, delay: (_, _) => delays++);
            Need(reads == 3 && delays == 2 && result.Source == source, $"reads={reads}; delays={delays}");
        });
        Check("dispatch_timeout_exhaustion_is_typed_and_three_reads_only", () =>
        {
            int reads = 0, delays = 0; Exception? failure = null;
            try { Recover(() => { reads++; return Timeout(); }, delay: (_, _) => delays++); }
            catch (Exception error) { failure = error; }
            Need(TimeoutException(failure) && reads == 3 && delays == 2, $"reads={reads}; delays={delays}; error={failure}");
        });
        Check("dispatch_timeout_mixed_exhaustion_does_not_reset_attempt_count", () =>
        {
            int reads = 0; Exception? failure = null;
            try { Recover(() => ++reads switch { 1 => Changed(), 2 => Timeout(), _ => Timeout() }); }
            catch (Exception error) { failure = error; }
            Need(TimeoutException(failure) && reads == 3, $"reads={reads}; error={failure}");
        });
        Check("dispatch_timeout_four_second_admission_stops_second_pass", () =>
        {
            int reads = 0, delays = 0; double elapsed = 0; Exception? failure = null;
            try { Recover(() => { reads++; return Timeout(); }, elapsed: () => elapsed,
                delay: (_, _) => { delays++; elapsed = 4000; }); }
            catch (Exception error) { failure = error; }
            Need(TimeoutException(failure) && reads == 1 && delays == 1, $"reads={reads}; delays={delays}; error={failure}");
        });
        Check("dispatch_timeout_long_first_pass_has_no_retry_admission", () =>
        {
            int reads = 0, delays = 0; Exception? failure = null;
            try { Recover(() => { reads++; return Timeout(); }, elapsed: () => 4001, delay: (_, _) => delays++); }
            catch (Exception error) { failure = error; }
            Need(TimeoutException(failure) && reads == 1 && delays == 0, $"reads={reads}; delays={delays}; error={failure}");
        });
        foreach (string kind in new[] { "source", "no-source-read", "native-error", "other-code", "missing-field", "stale-value", "timestamp", "state" })
            Check("dispatch_timeout_invalid_envelope_rejected_" + kind, () =>
            {
                var result = Timeout();
                switch (kind)
                {
                    case "source": result = result with { Source = source with { SessionKey = "different" } }; break;
                    case "no-source-read": result = result with { NoSourceRead = true }; break;
                    case "native-error": result = result with { ReadFailureNativeError = 5 }; break;
                    case "other-code": result = result with { ReadFailureCode = "RPM_INCOMPLETE" }; break;
                    case "missing-field": result.Fields.Remove("personalShopListing"); break;
                    case "stale-value": result.Fields["selfId"] = FieldReading.Observed(11, now); break;
                    case "timestamp": result.Fields["position"] = result.Fields["position"] with { SampledAt = now }; break;
                    case "state": result = result with { State = "Reading" }; break;
                }
                int reads = 0, delays = 0; Exception? failure = null;
                try { Recover(() => { reads++; return result; }, delay: (_, _) => delays++); }
                catch (Exception error) { failure = error; }
                Need(failure is not null && !TimeoutException(failure) && reads == 1 && delays == 0,
                    $"invalid envelope recovered: reads={reads}; delays={delays}; error={failure}");
            });
        Check("dispatch_timeout_changed_source_on_next_pass_never_recovers", () =>
        {
            int reads = 0; Exception? failure = null;
            try { Recover(() => ++reads == 1 ? Timeout() : Good() with { Source = source with { CreationFileTime = 9 } }); }
            catch (Exception error) { failure = error; }
            Need(reads == 2 && failure is not null && !TimeoutException(failure), $"reads={reads}; error={failure}");
        });
        Check("dispatch_timeout_cancelled_during_backoff_never_reads_again", () =>
        {
            using var stop = new CancellationTokenSource(); int reads = 0; Exception? failure = null;
            try { Recover(() => { reads++; return Timeout(); }, token: stop.Token, delay: (_, _) => stop.Cancel()); }
            catch (Exception error) { failure = error; }
            Need(reads == 1 && failure is OperationCanceledException, $"reads={reads}; error={failure}");
        });
        Check("dispatch_timeout_configuration_change_during_backoff_stops", () =>
        {
            bool changed = false; int reads = 0; Exception? failure = null;
            try { Recover(() => { reads++; return Timeout(); }, validate: () =>
                { if (changed) throw new InvalidOperationException("SHOP_CONFIGURATION_CHANGED"); }, delay: (_, _) => changed = true); }
            catch (Exception error) { failure = error; }
            Need(reads == 1 && failure?.Message == "SHOP_CONFIGURATION_CHANGED", $"reads={reads}; error={failure}");
        });
        Check("dispatch_timeout_arbitrary_exception_is_not_retried", () =>
        {
            int reads = 0; Exception? failure = null;
            try { Recover(() => { reads++; throw new IOException("unclassified"); }); }
            catch (Exception error) { failure = error; }
            Need(reads == 1 && failure is IOException, $"reads={reads}; error={failure}");
        });
        foreach (double badElapsed in new[] { double.NaN, -1, double.PositiveInfinity })
            Check("dispatch_timeout_invalid_timer_" + badElapsed, () =>
            {
                int reads = 0; Exception? failure = null;
                try { Recover(() => { reads++; return Timeout(); }, elapsed: () => badElapsed); }
                catch (Exception error) { failure = error; }
                Need(reads == 1 && failure is InvalidOperationException && !TimeoutException(failure)
                    && failure.Message.Contains("計時"), $"reads={reads}; error={failure}");
            });
        Check("dispatch_timeout_actor_expiry_after_backoff_does_not_gain_freshness", () =>
        {
            int reads = 0; var atStart = now;
            try
            {
                var result = Recover(() => { reads++; return Changed(); }, delay: (_, _) => now = now.AddSeconds(3));
                Need(reads == 1 && !result.Fields["selfId"].Fresh(now), $"reads={reads}");
            }
            finally { now = atStart; }
        });
        checks.AddRange(ShopDispatchSelfSpatialSelfTest.Run());
        return checks;
    }
}
