using MemoryHub;

namespace LcPlayerMarket;

internal static class PatrolFreshSourceGateSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "通過（假時鐘／假採樣；未連線 LC）")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("source_gate_initial_1999ms_sample_valid", async () =>
        {
            var f = new PatrolReadFixture();
            var gate = f.Gate(() => f.Snapshot(f.Now.AddMilliseconds(-1999)));
            Need((await gate.WaitAsync(f.Source, f.Self, default)).Valid && f.Delays == 0);
        });
        await Check("source_gate_null_gap_recovers_new_same_source", async () =>
        {
            var f = new PatrolReadFixture();
            var gate = f.Gate(() => f.Delays < 8 ? null : f.Snapshot());
            Need((await gate.WaitAsync(f.Source, f.Self, default)).Valid && f.Delays == 8);
            Need(f.Notices.Select(n => n.Code).SequenceEqual(["SOURCE_WAIT", "SOURCE_RECOVERED"]));
        });
        await Check("source_gate_stale_samples_wait_for_new_fields", async () =>
        {
            var f = new PatrolReadFixture(); var stale = f.Snapshot(f.Now.AddSeconds(-3));
            var gate = f.Gate(() => f.Delays < 3 ? stale : f.Snapshot());
            Need((await gate.WaitAsync(f.Source, f.Self, default)).Valid && f.Delays == 3);
        });
        foreach (string key in new[] { "selfId", "position" })
            await Check("source_gate_gap_requires_new_" + key, async () =>
            {
                var f = new PatrolReadFixture(); var old = f.Now.AddMilliseconds(-1);
                var gate = f.Gate(() =>
                {
                    if (f.Delays == 0) return null;
                    var current = f.Snapshot();
                    if (f.Delays < 3) current.Fields[key] = current.Fields[key] with { SampledAt = old };
                    return current;
                });
                Need((await gate.WaitAsync(f.Source, f.Self, default)).Valid && f.Delays == 3);
            });
        var original = new PatrolReadFixture().Source;
        var changes = new Dictionary<string, SourceIdentity>
        {
            ["pid"] = original with { ProcessId = original.ProcessId + 1 },
            ["creation"] = original with { CreationFileTime = original.CreationFileTime + 1 },
            ["image_path"] = original with { ImagePath = "other.exe" },
            ["image_base"] = original with { ImageBase = "0x150000000" },
            ["timestamp"] = original with { PeTimestamp = original.PeTimestamp + 1 },
            ["image_size"] = original with { ImageSize = original.ImageSize + 1 },
            ["sha"] = original with { DiskSha256 = new string('B', 64) },
            ["session"] = original with { SessionKey = "new-session" }
        };
        foreach (var change in changes)
            await Check("source_gate_changed_raw_" + change.Key + "_fails_without_wait", async () =>
            {
                var f = new PatrolReadFixture(); var value = f.Snapshot() with { Source = change.Value };
                value.Fields["position"] = FieldReading.Unknown("temporarily missing");
                await Error(() => f.Gate(() => value).WaitAsync(f.Source, f.Self, default), "SourceMismatch");
                Need(f.Delays == 0);
            });
        await Check("source_gate_first_invalid_identity_pins_full_record", async () =>
        {
            var f = new PatrolReadFixture();
            var first = f.Snapshot(); first.Fields["position"] = FieldReading.Unknown("gap");
            var gate = f.Gate(() => f.Delays == 0 ? first : f.Snapshot() with { Source = f.Source with { SessionKey = "changed" } });
            await Error(() => gate.WaitAsync(null, null, default), "SourceMismatch"); Need(f.Delays == 1);
        });
        await Check("source_gate_changed_self_with_unknown_position_fails", async () =>
        {
            var f = new PatrolReadFixture(); var current = f.Snapshot();
            current.Fields["selfId"] = FieldReading.Observed(f.Self + 1, f.Now);
            current.Fields["position"] = FieldReading.Unknown("gap");
            await Error(() => f.Gate(() => current).WaitAsync(f.Source, f.Self, default), "SourceMismatch"); Need(f.Delays == 0);
        });
        await Check("source_gate_unknown_first_self_pins_when_observed", async () =>
        {
            var f = new PatrolReadFixture();
            var gate = f.Gate(() =>
            {
                var s = f.Snapshot(); s.Fields["position"] = FieldReading.Unknown("gap");
                s.Fields["selfId"] = f.Delays == 0 ? FieldReading.Unknown("gap")
                    : FieldReading.Observed(f.Self + (f.Delays > 1 ? 1UL : 0UL), f.Now);
                return s;
            });
            await Error(() => gate.WaitAsync(null, null, default), "SourceMismatch"); Need(f.Delays == 2);
        });
        foreach (var field in new[] { FieldReading.Disabled(), FieldReading.Unsupported("unsupported") })
            await Check("source_gate_" + field.State + "_does_not_wait", async () =>
            {
                var f = new PatrolReadFixture(); var s = f.Snapshot(); s.Fields["position"] = field;
                await Error(() => f.Gate(() => s).WaitAsync(f.Source, f.Self, default), "SourceUnavailable"); Need(f.Delays == 0);
            });
        await Check("source_gate_malformed_observed_not_transient", async () =>
        {
            var f = new PatrolReadFixture(); var s = f.Snapshot();
            s.Fields["position"] = FieldReading.Observed("invalid", f.Now);
            await Error(() => f.Gate(() => s).WaitAsync(f.Source, f.Self, default), "SourceUnavailable"); Need(f.Delays == 0);
        });
        foreach (string badKey in new[] { "selfId", "position" })
            await Check("source_gate_malformed_" + badKey + "_not_hidden_by_other_unknown", async () =>
            {
                var f = new PatrolReadFixture(); var s = f.Snapshot();
                s.Fields[badKey] = FieldReading.Observed("invalid", f.Now);
                s.Fields[badKey == "selfId" ? "position" : "selfId"] = FieldReading.Unknown("gap");
                await Error(() => f.Gate(() => s).WaitAsync(f.Source, null, default), "SourceUnavailable"); Need(f.Delays == 0);
            });
        foreach (int clockMode in new[] { 1, 0, -1 })
            await Check("source_gate_timeout_bounded_clock_" + clockMode, async () =>
            {
                var f = new PatrolReadFixture { ClockDirection = clockMode };
                await Error(() => f.Gate(() => null).WaitAsync(f.Source, f.Self, default), "SourceUnavailableTimeout");
                Need(f.Delays == 40 && f.Reads == 40);
            });
        await Check("source_gate_precancel_no_snapshot_or_delay", async () =>
        {
            var f = new PatrolReadFixture(); using var stop = new CancellationTokenSource(); stop.Cancel();
            await Cancelled(() => f.Gate(() => f.Snapshot()).WaitAsync(f.Source, f.Self, stop.Token));
            Need(f.Reads == 0 && f.Delays == 0);
        });
        await Check("source_gate_cancel_during_gap_no_further_read", async () =>
        {
            var f = new PatrolReadFixture(); using var stop = new CancellationTokenSource(); f.AfterDelay = stop.Cancel;
            await Cancelled(() => f.Gate(() => null).WaitAsync(f.Source, f.Self, stop.Token));
            Need(f.Reads == 1 && f.Delays == 1);
        });
        await Check("source_gate_diagnostics_cannot_authorize_or_break_recovery", async () =>
        {
            var f = new PatrolReadFixture();
            var gate = new PatrolFreshSourceGate(() => f.Delays == 0 ? null : f.Snapshot(), () => f.Now, f.Delay,
                _ => throw new IOException("fixture diagnostic failure"));
            Need((await gate.WaitAsync(f.Source, f.Self, default)).Valid && f.Delays == 1);
        });
        await Check("source_gate_evidence_initial_valid_is_not_recovery", async () =>
        {
            var f = new PatrolReadFixture();
            var value = await f.Gate(() => f.Snapshot()).WaitWithEvidenceAsync(f.Source, f.Self, default);
            Need(value.Context.Valid && !value.Recovered && f.Delays == 0);
        });
        await Check("source_gate_evidence_only_new_full_sample_after_gap_is_recovery", async () =>
        {
            var f = new PatrolReadFixture(); var old = f.Now.AddMilliseconds(-1);
            var gate = f.Gate(() => f.Delays == 0 ? null : f.Snapshot(f.Delays < 3 ? old : f.Now));
            var value = await gate.WaitWithEvidenceAsync(f.Source, f.Self, default);
            Need(value.Context.Valid && value.Recovered && f.Delays == 3);
            Need(!(await gate.WaitWithEvidenceAsync(f.Source, f.Self, default)).Recovered);
        });
        await Check("source_gate_evidence_does_not_depend_on_diagnostic_callback", async () =>
        {
            var f = new PatrolReadFixture();
            var gate = new PatrolFreshSourceGate(() => f.Delays == 0 ? null : f.Snapshot(), () => f.Now, f.Delay,
                _ => throw new IOException("offline diagnostic failure"));
            var value = await gate.WaitWithEvidenceAsync(f.Source, f.Self, default);
            Need(value.Recovered && value.Context.Valid && f.Delays == 1);
        });
        return checks;
    }

    internal static void Need(bool condition) { if (!condition) throw new InvalidOperationException("回歸條件未符合"); }
    internal static async Task Error(Func<Task> action, string code)
    {
        try { await action(); }
        catch (PatrolSourceReadException e) when (e.Code == code) { return; }
        throw new InvalidOperationException("未取得預期停止原因：" + code);
    }
    internal static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("取消未傳遞");
    }
}

internal sealed class PatrolReadFixture
{
    internal DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    internal readonly SourceIdentity Source = new(123, 133000000000000000, @"C:\game\LC.exe", "0x140000000", 1, 500000, new string('A', 64), "offline-source-session");
    internal const ulong OwnId = 9007199254740993;
    internal ulong Self => OwnId;
    internal int Reads, Delays, ClockDirection = 1;
    internal Action? AfterDelay;
    internal readonly List<PatrolSourceWaitNotice> Notices = [];
    internal HubSnapshot Snapshot(DateTimeOffset? sampled = null) => new()
    {
        Source = Source, State = "Reading", PublishedAt = Now, Sequence = Reads,
        Fields = new()
        {
            ["selfId"] = FieldReading.Observed(Self, sampled ?? Now),
            ["position"] = FieldReading.Observed(new { x = 100, y = 200 }, sampled ?? Now)
        }
    };
    internal Task Delay(TimeSpan amount, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Delays++; Now += amount * ClockDirection; AfterDelay?.Invoke();
        token.ThrowIfCancellationRequested(); return Task.CompletedTask;
    }
    internal PatrolFreshSourceGate Gate(Func<HubSnapshot?> read) => new(() => { Reads++; return read(); }, () => Now, Delay, Notices.Add);
}
