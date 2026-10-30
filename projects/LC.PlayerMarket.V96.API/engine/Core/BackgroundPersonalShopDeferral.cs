namespace MemoryHub;

// Admission is linearized with lease acquisition. A slice admitted before a
// lease may finish normally; no source read or publication is cancelled here.
internal sealed class BackgroundPersonalShopDeferral
{
    private readonly object gate = new();
    private int holders;

    internal bool IsDeferred { get { lock (gate) return holders != 0; } }

    internal IDisposable Acquire(Action resume)
    {
        ArgumentNullException.ThrowIfNull(resume);
        var lease = new Lease(this, resume);
        lock (gate) { checked { holders++; } }
        return lease;
    }

    internal IReadOnlyList<ScheduledReadSlice> Eligible(IReadOnlyList<ScheduledReadSlice> schedule)
        => IsDeferred ? schedule.Where(slice => !IsPersonalShop(slice)).ToArray() : schedule;

    internal bool TryAdmit(ScheduledReadSlice slice)
    {
        if (!IsPersonalShop(slice)) return true;
        lock (gate) return holders == 0;
    }

    private static bool IsPersonalShop(ScheduledReadSlice slice)
        => !slice.Priority && string.Equals(slice.Name, "personalShop", StringComparison.Ordinal);

    private void Release(Action resume)
    {
        bool last;
        lock (gate)
        {
            if (holders <= 0) throw new InvalidOperationException("Background shop deferral lease underflow.");
            last = --holders == 0;
        }
        if (last) resume();
    }

    private sealed class Lease(BackgroundPersonalShopDeferral owner, Action resume) : IDisposable
    {
        private BackgroundPersonalShopDeferral? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release(resume);
    }
}

public sealed partial class HubRuntime
{
    private readonly BackgroundPersonalShopDeferral backgroundPersonalShopDeferral = new();

    /// <summary>
    /// Defers only new scheduled personalShop background slice admissions.
    /// Existing reads may finish; direct reads and all other fields are unchanged.
    /// Disposing the last lease wakes the original scheduler without changing settings.
    /// </summary>
    public IDisposable DeferBackgroundPersonalShop()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return backgroundPersonalShopDeferral.Acquire(WakeScheduler);
        }
    }
}
