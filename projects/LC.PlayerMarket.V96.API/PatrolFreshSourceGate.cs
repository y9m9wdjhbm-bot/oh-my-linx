using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record PatrolSourceWaitNotice(string Code, string Detail, int Attempts, TimeSpan Waited);
internal sealed record PatrolFreshSourceRead(LiveContext Context, bool Recovered);
internal sealed class PatrolSourceReadException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}

/// <summary>Wait only for newly sampled identity/position; a changed non-null identity is never a transient gap.</summary>
internal sealed class PatrolFreshSourceGate
{
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly Func<HubSnapshot?> snapshot;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<PatrolSourceWaitNotice>? report;

    internal PatrolFreshSourceGate(Func<HubSnapshot?> snapshot, Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<PatrolSourceWaitNotice>? report = null)
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? Task.Delay;
        this.report = report;
    }

    internal async Task<LiveContext> WaitAsync(SourceIdentity? expectedSource, ulong? expectedSelfId,
        CancellationToken token, DateTimeOffset? deadline = null)
        => (await WaitWithEvidenceAsync(expectedSource, expectedSelfId, token, deadline).ConfigureAwait(false)).Context;

    internal async Task<PatrolFreshSourceRead> WaitWithEvidenceAsync(SourceIdentity? expectedSource, ulong? expectedSelfId,
        CancellationToken token, DateTimeOffset? deadline = null)
    {
        token.ThrowIfCancellationRequested();
        var began = utcNow();
        var end = deadline is { } supplied && supplied < began + MaximumWait ? supplied : began + MaximumWait;
        bool waiting = false;
        int attempts = 0, pauses = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var now = utcNow();
            if (now >= end || pauses >= 40)
                throw Failure("SourceUnavailableTimeout", "目前來源暫時不可用；等待全新同來源讀值已達上限，沒有採用舊資料。", attempts, began);
            var current = snapshot(); attempts++;
            // Inspect the complete raw record before freshness can clear any fields.
            if (current?.Source is { } source)
            {
                if (expectedSource is not null && source != expectedSource)
                    throw Failure("SourceMismatch", "LC 完整來源已改變，停止等待並拒絕接續。", attempts, began);
                expectedSource ??= source;
            }
            var rawSelf = current?.Fields.GetValueOrDefault("selfId");
            if (rawSelf is { State: "Observed", Value: { ValueKind: JsonValueKind.Number } value }
                && value.TryGetUInt64(out ulong selfId) && selfId != 0)
            {
                if (expectedSelfId is not null && selfId != expectedSelfId)
                    throw Failure("SourceMismatch", "目前角色 ID 已改變，停止等待並拒絕接續。", attempts, began);
                expectedSelfId ??= selfId;
            }
            if (current is not null && current.State is not ("Reading" or "Waiting" or "Unknown" or "Stale"))
                throw Failure("SourceUnavailable", "目前來源狀態不允許暫態等待：" + current.State, attempts, began);
            foreach (string key in new[] { "selfId", "position" })
            {
                if (current?.Fields.GetValueOrDefault(key)?.State is "Disabled" or "Unsupported")
                    throw Failure("SourceUnavailable", "必要欄位已停用或不支援：" + key, attempts, began);
                if (current?.Fields.GetValueOrDefault(key) is { } field && field.Fresh(now))
                {
                    bool valid = field.Value is { } observed && (key == "selfId"
                        ? observed.ValueKind == JsonValueKind.Number && observed.TryGetUInt64(out ulong id) && id != 0
                        : LiveContext.Point(observed) is not null);
                    if (!valid) throw Failure("SourceUnavailable", "必要欄位格式無效，未將它當成可等待的來源空窗：" + key, attempts, began);
                }
            }

            var live = new LiveContext(current, now);
            if (live.Valid)
            {
                bool newlySampled = !waiting || current!.PublishedAt >= began
                    && current.Fields.GetValueOrDefault("selfId")?.SampledAt >= began
                    && current.Fields.GetValueOrDefault("position")?.SampledAt >= began;
                if (newlySampled)
                {
                    token.ThrowIfCancellationRequested();
                    if (waiting) Notice("SOURCE_RECOVERED", "已取得全新、完整相同來源與角色的有效讀值。", attempts, began);
                    return new(live, waiting);
                }
            }
            else if (current?.Source is not null && current.PublishedAt <= now
                && now - current.PublishedAt <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            {
                var effective = current.AtTime(now);
                bool temporary = new[] { "selfId", "position" }.Any(key =>
                    effective.Fields.GetValueOrDefault(key)?.State is null or "Unknown" or "Stale");
                if (!temporary)
                    throw Failure("SourceUnavailable", "必要欄位格式無效，未將它當成可等待的來源空窗。", attempts, began);
            }
            if (!waiting)
            {
                waiting = true;
                Notice("SOURCE_WAIT", "來源讀值暫時不可用；只等待全新同來源採樣，不重新送出動作。", attempts, began);
            }
            var remaining = end - utcNow();
            if (remaining <= TimeSpan.Zero) continue;
            await delay(remaining < PollInterval ? remaining : PollInterval, token).ConfigureAwait(false);
            pauses++;
        }
    }

    private PatrolSourceReadException Failure(string code, string detail, int attempts, DateTimeOffset began)
    { Notice(code, detail, attempts, began); return new(code, detail); }
    private void Notice(string code, string detail, int attempts, DateTimeOffset began)
    {
        try { report?.Invoke(new(code, detail, attempts, utcNow() - began)); }
        catch { /* Diagnostics never authorize work or prevent cleanup. */ }
    }
}
