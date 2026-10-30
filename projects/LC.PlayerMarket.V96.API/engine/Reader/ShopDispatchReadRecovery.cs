using System.Diagnostics;

namespace MemoryHub.Reader;

/// <summary>Only emitted after closed reads of the original source exhaust collection recovery.</summary>
public sealed class ShopDispatchCollectionChangedException(SourceIdentity source, string detail)
    : InvalidOperationException(detail)
{
    public new SourceIdentity Source { get; } = source;
}

/// <summary>
/// A completed read-only deadline, never proof that ExpectedSource was observed.
/// Only the reader may create this evidence. Attempts is zero before the shared recovery loop exhausts.
/// </summary>
public sealed class ShopDispatchReadTimeoutException : InvalidOperationException
{
    public SourceIdentity ExpectedSource { get; }
    public int Attempts { get; }
    public double ElapsedMilliseconds { get; }
    internal ShopDispatchReadTimeoutException(SourceIdentity expectedSource, int attempts, double elapsedMilliseconds, string detail)
        : base(detail)
    { ExpectedSource = expectedSource; Attempts = attempts; ElapsedMilliseconds = elapsedMilliseconds; }
}

internal sealed record ShopDispatchRetryEvent(string Phase, int Attempt, double ElapsedMs);

internal static class ShopDispatchReadRecovery
{
    internal const int MaxAttempts = 3;
    // Admission limit for another pass; each synchronous pass retains its own 2 s budget.
    internal const int RetryAdmissionMilliseconds = 4000;
    internal const int DelayMilliseconds = 100;
    private static readonly string[] ActorFields = ["nearbyPlayers", "playerShopStates"];
    private static readonly string[] OtherFields = ["selfId", "position", "personalShopListing"];
    private static readonly string[] StableFields = ["selfId", "personalShopListing"];

    internal static bool Eligible(ReadResult result, SourceIdentity expected, DateTimeOffset now)
    {
        if (result.Source is null || result.Source != expected || result.NoSourceRead ||
            result.ReadFailureCode is not null || result.ReadFailureNativeError is not null ||
            result.State is not ("Reading" or "Unknown")) return false;
        bool actorTransient=ActorFields.All(key=>Transient(result,key,"ACTOR_COLLECTION_CHANGED："));
        bool actorsFresh=ActorFields.All(key=>FreshValue(result,key,now));
        bool positionTransient=PositionTransient(result);
        return StableFields.All(key=>FreshValue(result,key,now))
            && (actorTransient || actorsFresh)
            && (positionTransient || FreshValue(result,"position",now))
            && (actorTransient || positionTransient);
    }

    // Frozen pages do not sample actors or position. Only the typed closed
    // read deadline can retry; unrelated spatial gaps never trigger another read.
    internal static bool EligibleFrozen(ReadResult result, SourceIdentity expected, DateTimeOffset now) => false;

    private static bool FreshValue(ReadResult result,string key,DateTimeOffset now)=>
        result.Fields.TryGetValue(key,out var field) && field.Fresh(now)
        && field.Value is {ValueKind:not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)};

    private static bool Transient(ReadResult result,string key,string prefix)=>
        result.RetryableFields.Contains(key,StringComparer.Ordinal)
        && result.Fields.TryGetValue(key,out var field) && field.State=="Unknown"
        && field.Value is null && field.SampledAt is null
        && field.Detail.StartsWith(prefix,StringComparison.Ordinal);

    private static bool PositionTransient(ReadResult result)=>
        Transient(result,"position","SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED：")
        || Transient(result,"position","SELF_SPATIAL_ACTOR_DISCOVERY_CONTROL：");

    internal static ReadResult Read(Func<ReadResult> capture, SourceIdentity expected, Action validate,
        CancellationToken token, Action<ShopDispatchRetryEvent> log, Func<DateTimeOffset>? utcNow = null,
        Func<double>? elapsedMilliseconds = null, Action<int, CancellationToken>? delay = null, bool frozenTargets = false)
    {
        var timer = Stopwatch.StartNew();
        utcNow ??= () => DateTimeOffset.UtcNow;
        elapsedMilliseconds ??= () => timer.Elapsed.TotalMilliseconds;
        delay ??= (milliseconds, cancellation) =>
        {
            if (cancellation.WaitHandle.WaitOne(milliseconds)) cancellation.ThrowIfCancellationRequested();
        };
        bool EligibleSample(ReadResult value) => frozenTargets ? EligibleFrozen(value, expected, utcNow()) : Eligible(value, expected, utcNow());
        int attempt = 0;
        bool sawTimeout = false;
        bool sawSelfSpatialTransient = false;
        ReadResult? last = null;
        ShopDispatchReadTimeoutException? timeout = null;
        while (true)
        {
            Check();
            attempt++;
            last = null; timeout = null;
            try { last = capture(); }
            catch (ShopDispatchReadTimeoutException error) when (error.Attempts == 0 && error.ExpectedSource == expected)
            {
                // ReadProfile has returned through its Dispose finally before
                // RequireCurrent can emit this exact typed error. No overlapping capture.
                timeout = error; sawTimeout = true;
            }
            Check();
            if(timeout is null && EligibleSample(last!) && PositionTransient(last!))
                sawSelfSpatialTransient=true;
            if (timeout is null && !EligibleSample(last!))
            {
                if (attempt > 1 && last!.Source == expected && !last.NoSourceRead &&
                    (frozenTargets ? StableFields : ActorFields.Concat(OtherFields)).All(key => last.Fields.TryGetValue(key, out var f) && f.Fresh(utcNow()) && f.Value is not null))
                    log(new(sawTimeout ? "SHOP_DISPATCH_READ_RECOVERED" : "SHOP_DISPATCH_COLLECTION_RECOVERED", attempt, Elapsed()));
                return last!; // Existing caller validation handles all other failures and freshness.
            }
            if (attempt >= MaxAttempts || Elapsed() >= RetryAdmissionMilliseconds) break;
            log(new(timeout is null ? "SHOP_DISPATCH_COLLECTION_RETRY" : "SHOP_DISPATCH_TIMEOUT_RETRY", attempt + 1, Elapsed()));
            delay(DelayMilliseconds, token);
            Check();
            // No retained field gains freshness while waiting. A deadline retains no fields at all.
            if (timeout is null && !EligibleSample(last!)) return last!;
            if (Elapsed() >= RetryAdmissionMilliseconds) break;
        }
        Check();
        if (timeout is null && !EligibleSample(last!)) return last!;
        double elapsed = Elapsed();
        if (timeout is not null)
        {
            log(new("SHOP_DISPATCH_TIMEOUT_EXHAUSTED", attempt, elapsed));
            Check();
            throw new ShopDispatchReadTimeoutException(expected, attempt, elapsed,
                $"SHOP_DISPATCH_READ_TIMEOUT：共用 {attempt} 輪完整採樣後唯讀期限仍用盡（{elapsed:F0} 毫秒）；本頁未建立原生意圖，完整原來源仍須重新確認。" + timeout.Message);
        }
        log(new("SHOP_DISPATCH_COLLECTION_EXHAUSTED", attempt, elapsed));
        Check();
        throw new ShopDispatchCollectionChangedException(expected,
            $"ACTOR_COLLECTION_CHANGED：{attempt} 輪完整採樣後集合仍在變動（{elapsed:F0} 毫秒）；本頁尚未送出，保留未確認紀錄。" +
            (sawSelfSpatialTransient?"本次共用重取包含自身短查找暫態；仍未取得完整可送出採樣。":"")+
            string.Join("；", new[]{"position"}.Concat(ActorFields)
                .Where(key=>last!.Fields.TryGetValue(key,out var field)&&field.State=="Unknown")
                .Select(key=>key+"="+last!.Fields[key].Detail)));

        void Check() { token.ThrowIfCancellationRequested(); validate(); token.ThrowIfCancellationRequested(); }
        double Elapsed()
        {
            double value = elapsedMilliseconds();
            if (!double.IsFinite(value) || value < 0) throw new InvalidOperationException("商店重新採樣計時無效；不繼續搜尋。");
            return value;
        }
    }
}
