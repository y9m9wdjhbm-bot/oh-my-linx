using System.Diagnostics;

namespace MemoryHub.Service;

internal sealed record ShopDurablePhaseIoReceipt(string Phase, double ElapsedMilliseconds,
    IReadOnlyList<double> WriterElapsedMilliseconds, bool CancellationObservedAfterJoin);

internal static class ShopDurablePhaseIo
{
    // This helper joins writers; it does not publish intent, invoke native work,
    // rename journals, release an owning gate, or retry an incomplete phase.
    internal static async Task<ShopDurablePhaseIoReceipt> RunAsync(string phase, Action[] writers,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(writers);
        if (writers.Length is < 2 or > 3)
            throw new ArgumentOutOfRangeException(nameof(writers), "A durable phase requires two or three independent writers.");
        var owned = (Action[])writers.Clone();
        for (int i = 0; i < owned.Length; i++)
            if (owned[i] is null) throw new ArgumentException($"Durable writer {i} is null.", nameof(writers));
        token.ThrowIfCancellationRequested();

        long begun = Stopwatch.GetTimestamp();
        var pending = new Task<WriterObservation>?[owned.Length];
        var launchErrors = new Exception?[owned.Length];
        for (int i = 0; i < owned.Length; i++)
        {
            Action writer = owned[i];
            try { pending[i] = Task.Run(() => ObserveWriter(writer), CancellationToken.None); }
            catch (Exception error) { launchErrors[i] = error; }
        }

        // Once the phase is admitted, cancellation cannot abandon an in-flight
        // Flush(true). Even scheduler failures must join all started writers.
        var started = pending.Where(task => task is not null).Select(task => task!).ToArray();
        try { await Task.WhenAll(started).ConfigureAwait(false); }
        catch { /* Inspect every completed task below; do not keep only WhenAll's first exception. */ }

        var faults = new List<Exception>();
        var elapsed = new double[owned.Length];
        for (int i = 0; i < pending.Length; i++)
        {
            if (launchErrors[i] is { } launchError) faults.Add(launchError);
            if (pending[i] is not { } task) continue;
            if (task.IsCompletedSuccessfully)
            {
                WriterObservation observed = task.Result;
                elapsed[i] = observed.ElapsedMilliseconds;
                if (observed.Fault is { } fault) faults.Add(fault);
            }
            else if (task.IsFaulted) faults.AddRange(task.Exception!.InnerExceptions);
            else if (task.IsCanceled) faults.Add(new TaskCanceledException(task));
            else throw new InvalidOperationException("Durable phase returned before all writers completed.");
        }
        if (faults.Count != 0)
            throw new AggregateException($"Durable phase '{phase}' failed; every started writer has completed and been observed.", faults);

        // Cancellation after admission is evidence for the caller's existing
        // boundary. It must not open a gap between durable intent and its one
        // owned native attempt or pretend that successful writes did not occur.
        return new(phase, Stopwatch.GetElapsedTime(begun).TotalMilliseconds,
            Array.AsReadOnly(elapsed), token.IsCancellationRequested);
    }

    private static WriterObservation ObserveWriter(Action writer)
    {
        long begun = Stopwatch.GetTimestamp();
        Exception? fault = null;
        try { writer(); }
        catch (Exception error) { fault = error; }
        // Retain the original exception, including a writer's own cancellation,
        // rather than letting Task cancellation erase its cause.
        return new(Stopwatch.GetElapsedTime(begun).TotalMilliseconds, fault);
    }

    private readonly record struct WriterObservation(double ElapsedMilliseconds, Exception? Fault);
}
