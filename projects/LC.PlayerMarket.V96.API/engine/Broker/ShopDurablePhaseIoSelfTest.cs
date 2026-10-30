using LcPlayerMarket;

namespace MemoryHub.Service;

internal static class ShopDurablePhaseIoSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try
            {
                await body();
                checks.Add(new("durable_phase_io_" + name, true,
                    "Pure fake writers/gates only; no disk flush, LC read, native request, process launch, or game input."));
            }
            catch (Exception error) { checks.Add(new("durable_phase_io_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task<Exception> Failure(Func<Task> body)
        {
            try { await body(); }
            catch (Exception error) { return error; }
            throw new InvalidOperationException("Expected refusal did not occur.");
        }

        await Check("two_writers_join_and_report_in_input_order", async () =>
        {
            var calls = new int[2];
            var result = await ShopDurablePhaseIo.RunAsync("initial-intent", [
                () => Interlocked.Increment(ref calls[0]), () => Interlocked.Increment(ref calls[1])]);
            Need(result.Phase == "initial-intent" && calls.All(n => n == 1)
                && result.WriterElapsedMilliseconds.Count == 2 && !result.CancellationObservedAfterJoin,
                "Successful writers were not observed exactly once.");
            Need(double.IsFinite(result.ElapsedMilliseconds) && result.ElapsedMilliseconds >= 0
                && result.WriterElapsedMilliseconds.All(ms => double.IsFinite(ms) && ms >= 0 && ms <= result.ElapsedMilliseconds),
                "Elapsed observations are not finite or do not fit the joined phase.");
        });
        foreach (int count in new[] { 2, 3 })
        await Check(count + "_writers_can_overlap_but_no_extra_writer_is_started", async () =>
        {
            using var fixture = new BlockedWriters(count);
            Task<ShopDurablePhaseIoReceipt> phase = ShopDurablePhaseIo.RunAsync("overlap", fixture.Actions);
            try
            {
                await fixture.AllStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Need(fixture.Active == count && fixture.MaximumActive == count && !phase.IsCompleted,
                    "The admitted writers did not overlap or the phase returned while writers were active.");
            }
            finally { fixture.Release(); await Observe(phase); }
            var result = await phase;
            Need(fixture.Calls.All(n => n == 1) && fixture.Active == 0
                && fixture.MaximumActive <= 3 && result.WriterElapsedMilliseconds.Count == count,
                "Writer count exceeded admission or a writer was replayed.");
        });
        foreach (int count in new[] { 0, 1, 4 })
        await Check("reject_" + count + "_writers_before_start", async () =>
        {
            int calls = 0;
            Action[] actions = Enumerable.Range(0, count).Select(_ => (Action)(() => Interlocked.Increment(ref calls))).ToArray();
            Need(await Failure(() => ShopDurablePhaseIo.RunAsync("invalid-count", actions)) is ArgumentOutOfRangeException && calls == 0,
                "Invalid writer count started work.");
        });
        await Check("reject_null_array_before_start", async () =>
            Need(await Failure(() => ShopDurablePhaseIo.RunAsync("null-array", null!)) is ArgumentNullException,
                "Null writers were accepted."));
        await Check("reject_null_writer_before_any_start", async () =>
        {
            int calls = 0;
            Need(await Failure(() => ShopDurablePhaseIo.RunAsync("null-writer", [() => calls++, null!])) is ArgumentException && calls == 0,
                "Earlier writers started before all inputs were validated.");
        });
        foreach (string? phase in new string?[] { null, "", " " })
        await Check("reject_invalid_phase_" + (phase is null ? "null" : phase.Length == 0 ? "empty" : "blank"), async () =>
        {
            int calls = 0;
            Need(await Failure(() => ShopDurablePhaseIo.RunAsync(phase!, [() => calls++, () => calls++])) is ArgumentException && calls == 0,
                "Invalid phase name started a writer.");
        });
        await Check("cancellation_before_admission_starts_nothing", async () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); int calls = 0;
            Exception error = await Failure(() => ShopDurablePhaseIo.RunAsync("pre-cancel", [() => calls++, () => calls++], cancel.Token));
            Need(error is OperationCanceledException cancelled && cancelled.CancellationToken == cancel.Token && calls == 0,
                "Pre-admission cancellation started work or lost its token.");
        });
        await Check("phase_cancellation_waits_for_every_successful_writer", async () =>
        {
            using var cancel = new CancellationTokenSource(); using var fixture = new BlockedWriters(3);
            Task<ShopDurablePhaseIoReceipt> phase = ShopDurablePhaseIo.RunAsync("cancel-after-start", fixture.Actions, cancel.Token);
            try
            {
                await fixture.AllStarted.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
                Need(!phase.IsCompleted && fixture.Active == 3, "Cancellation abandoned an admitted writer.");
            }
            finally { fixture.Release(); await Observe(phase); }
            var receipt = await phase;
            Need(receipt.CancellationObservedAfterJoin && fixture.Calls.All(n => n == 1) && fixture.Active == 0,
                "Joined cancellation was thrown away or prevented a successful receipt.");
        });
        await Check("fault_joins_slow_sibling_before_returning", async () =>
        {
            var original = new IOException("first-writer-failed"); using var fixture = new BlockedWriters(2, 0, original);
            Task<ShopDurablePhaseIoReceipt> phase = ShopDurablePhaseIo.RunAsync("fault-join", fixture.Actions);
            try
            {
                await fixture.AllStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Need(!phase.IsCompleted && fixture.Active == 1, "First fault released a still-active sibling.");
            }
            finally { fixture.Release(); await Observe(phase); }
            var failure = await Failure(() => phase);
            Need(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
                && ReferenceEquals(aggregate.InnerExceptions[0], original) && fixture.Active == 0
                && fixture.Calls.All(n => n == 1), "First fault was replaced, lost, or retried.");
        });
        await Check("all_writer_faults_are_preserved_without_retry", async () =>
        {
            Exception[] originals = [new IOException("writer-zero"), new InvalidDataException("writer-one"), new UnauthorizedAccessException("writer-two")];
            var calls = new int[3];
            Action[] actions = Enumerable.Range(0, 3).Select(i => (Action)(() => { Interlocked.Increment(ref calls[i]); throw originals[i]; })).ToArray();
            var failure = await Failure(() => ShopDurablePhaseIo.RunAsync("all-fault", actions));
            Need(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 3
                && originals.Select((error, i) => ReferenceEquals(error, aggregate.InnerExceptions[i])).All(equal => equal)
                && calls.All(n => n == 1), "Not every original writer cause was retained in input order.");
        });
        await Check("writer_nested_aggregate_is_not_flattened_or_replaced", async () =>
        {
            var original = new AggregateException("writer-aggregate", new IOException("underlying"));
            var failure = await Failure(() => ShopDurablePhaseIo.RunAsync("nested-fault", [() => throw original, () => { }]));
            Need(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
                && ReferenceEquals(aggregate.InnerExceptions[0], original), "Writer's original aggregate cause was rewritten.");
        });
        await Check("writer_own_cancellation_is_a_preserved_fault", async () =>
        {
            using var writerCancel = new CancellationTokenSource(); writerCancel.Cancel();
            var original = new OperationCanceledException("writer-itself-cancelled", writerCancel.Token);
            var failure = await Failure(() => ShopDurablePhaseIo.RunAsync("writer-cancel", [() => throw original, () => { }]));
            Need(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
                && ReferenceEquals(aggregate.InnerExceptions[0], original), "Writer cancellation erased its own failure evidence.");
        });
        await Check("phase_cancellation_cannot_cover_a_writer_fault", async () =>
        {
            using var cancel = new CancellationTokenSource();
            var original = new IOException("durability-failed"); using var fixture = new BlockedWriters(2, 0, original);
            Task<ShopDurablePhaseIoReceipt> phase = ShopDurablePhaseIo.RunAsync("fault-and-cancel", fixture.Actions, cancel.Token);
            try
            {
                await fixture.AllStarted.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
                Need(!phase.IsCompleted && fixture.Active == 1, "Cancellation bypassed the outstanding writer after a fault.");
            }
            finally { fixture.Release(); await Observe(phase); }
            var failure = await Failure(() => phase);
            Need(failure is AggregateException aggregate && ReferenceEquals(aggregate.InnerExceptions.Single(), original),
                "Phase cancellation hid a writer failure.");
        });
        await Check("cancelled_admitted_intent_keeps_gate_then_fake_native_once", async () =>
        {
            using var gate = new SemaphoreSlim(1, 1); using var cancel = new CancellationTokenSource();
            using var fixture = new BlockedWriters(2); int native = 0; ShopDurablePhaseIoReceipt? receipt = null;
            async Task Caller()
            {
                await gate.WaitAsync();
                try
                {
                    receipt = await ShopDurablePhaseIo.RunAsync("admitted-intent", fixture.Actions, cancel.Token);
                    Interlocked.Increment(ref native); // Fake original once-only caller step; no native invocation.
                }
                finally { gate.Release(); }
            }
            Task caller = Caller();
            try
            {
                await fixture.AllStarted.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
                Need(!caller.IsCompleted && native == 0 && gate.CurrentCount == 0 && fixture.Active == 2,
                    "Cancellation released ownership or advanced the caller before durable join.");
            }
            finally { fixture.Release(); await Observe(caller); }
            await caller;
            Need(receipt?.CancellationObservedAfterJoin == true && native == 1 && gate.CurrentCount == 1
                && fixture.Calls.All(n => n == 1), "Admitted cancellation lost or replayed the fake once-only step.");
        });
        await Check("failed_admitted_intent_never_advances_fake_native", async () =>
        {
            using var gate = new SemaphoreSlim(1, 1); using var cancel = new CancellationTokenSource();
            var original = new IOException("intent-not-durable"); using var fixture = new BlockedWriters(2, 0, original); int native = 0;
            async Task Caller()
            {
                await gate.WaitAsync();
                try
                {
                    await ShopDurablePhaseIo.RunAsync("failed-intent", fixture.Actions, cancel.Token);
                    Interlocked.Increment(ref native);
                }
                finally { gate.Release(); }
            }
            Task caller = Caller();
            try
            {
                await fixture.AllStarted.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
                Need(!caller.IsCompleted && gate.CurrentCount == 0 && native == 0, "Fault or cancellation released the caller before join.");
            }
            finally { fixture.Release(); await Observe(caller); }
            var failure = await Failure(() => caller);
            Need(failure is AggregateException aggregate && ReferenceEquals(aggregate.InnerExceptions.Single(), original)
                && native == 0 && fixture.Active == 0 && fixture.Calls.All(n => n == 1),
                "Failed intent was treated as durable or fake native work was dispatched.");
        });
        await Check("receipt_writer_timings_cannot_be_mutated", async () =>
        {
            var receipt = await ShopDurablePhaseIo.RunAsync("readonly-receipt", [() => { }, () => { }]);
            Need(receipt.WriterElapsedMilliseconds is IList<double> list && list.IsReadOnly,
                "Receipt exposes mutable timing storage.");
            Exception error = await Failure(() => { ((IList<double>)receipt.WriterElapsedMilliseconds)[0] = -1; return Task.CompletedTask; });
            Need(error is NotSupportedException && receipt.WriterElapsedMilliseconds[0] >= 0, "Receipt observations were overwritten.");
        });
        return checks;
    }

    private static async Task Observe(Task task) { try { await task; } catch { } }

    private sealed class BlockedWriters : IDisposable
    {
        private readonly ManualResetEventSlim release = new(false);
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int count;
        private int starts, active, maximum;
        internal readonly int[] Calls;
        internal readonly Action[] Actions;
        internal Task AllStarted => started.Task;
        internal int Active => Volatile.Read(ref active);
        internal int MaximumActive => Volatile.Read(ref maximum);
        internal BlockedWriters(int count, int failingIndex = -1, Exception? failure = null)
        {
            this.count = count; Calls = new int[count];
            Actions = Enumerable.Range(0, count).Select(index => (Action)(() =>
            {
                Interlocked.Increment(ref Calls[index]); int now = Interlocked.Increment(ref active);
                int previous;
                do { previous = Volatile.Read(ref maximum); if (previous >= now) break; }
                while (Interlocked.CompareExchange(ref maximum, now, previous) != previous);
                try
                {
                    if (index == failingIndex)
                    {
                        // Publish the failing writer's arrival only after it is
                        // no longer counted as active; the sibling remains held.
                        Interlocked.Decrement(ref active);
                        if (Interlocked.Increment(ref starts) == this.count) started.TrySetResult();
                        throw failure ?? new IOException("fake-writer-failed");
                    }
                    if (Interlocked.Increment(ref starts) == this.count) started.TrySetResult();
                    release.Wait();
                }
                finally { if (index != failingIndex) Interlocked.Decrement(ref active); }
            })).ToArray();
        }
        internal void Release() => release.Set();
        public void Dispose() { release.Set(); release.Dispose(); }
    }
}
