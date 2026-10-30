using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryHub.Service;

internal sealed record SearchProgressResetTransaction(
    [property: JsonRequired] int Schema,
    [property: JsonRequired] SavedScanResetMarker Marker,
    [property: JsonRequired] string ExpectedHistoryRevision);

/// <summary>Immutable prepare/commit evidence for a recoverable two-store reset.</summary>
internal sealed class ShopSearchProgressResetStore
{
    private readonly string root;
    internal ShopSearchProgressResetStore(string directory)
    { root = Path.GetFullPath(directory); Directory.CreateDirectory(root); }

    internal SearchProgressResetTransaction Prepare(ShopSearchedOwnerStore owners, ShopScanProgressStore scans, string requestId)
    {
        if (owners.Error is { } failure) throw new InvalidDataException(failure);
        var value = new SearchProgressResetTransaction(1, scans.CreateResetMarker(Guid.NewGuid().ToString("N"), requestId), owners.Revision);
        Validate(value); WriteNew(PathFor(value, "prepared"), value); return value;
    }

    internal void Complete(SearchProgressResetTransaction value, ShopSearchedOwnerStore owners, ShopScanProgressStore scans,
        Action<string>? interruption = null)
    {
        Validate(value);
        string prepared = PathFor(value, "prepared"), committed = PathFor(value, "committed");
        if (!File.Exists(prepared) || Read(prepared) != value) throw new InvalidDataException("搜尋重設缺少相符持久意圖。");
        if (File.Exists(committed))
        {
            if (Read(committed) != value) throw new InvalidDataException("搜尋重設提交與原意圖不同。");
            return;
        }
        owners.ClearForReset(value.ExpectedHistoryRevision, value.Marker.ResetId);
        interruption?.Invoke("AfterOwnerClear");
        scans.ResetActivePlan(value.Marker);
        interruption?.Invoke("AfterScanReset");
        WriteNew(committed, value);
    }

    internal int RecoverPending(ShopSearchedOwnerStore owners, ShopScanProgressStore scans)
    {
        var prepared = Directory.EnumerateFiles(root, "*.prepared.json").Select(path =>
        {
            var value = Read(path); Validate(value);
            if (Path.GetFileName(path) != Path.GetFileName(PathFor(value, "prepared"))) throw new InvalidDataException("搜尋重設意圖檔名不符。");
            return value;
        }).OrderBy(value => value.Marker.At).ThenBy(value => value.Marker.ResetId, StringComparer.Ordinal).ToArray();
        int recovered = 0;
        foreach (var value in prepared)
        {
            bool incomplete = !File.Exists(PathFor(value, "committed"));
            Complete(value, owners, scans);
            if (incomplete) recovered++;
        }
        return recovered;
    }

    private string PathFor(SearchProgressResetTransaction value, string stage) => Path.Combine(root,
        value.Marker.At.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + value.Marker.ResetId + "." + stage + ".json");
    private static void Validate(SearchProgressResetTransaction value)
    {
        if (value.Schema != 1 || value.Marker is null) throw new InvalidDataException("搜尋重設意圖版本無效。");
        ShopScanProgressStore.ValidateResetMarker(value.Marker);
        string[] parts = value.ExpectedHistoryRevision?.Split(':') ?? [];
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _) || !int.TryParse(parts[1], out int sequence) || sequence < 2)
            throw new InvalidDataException("搜尋重設原期別／尾端版本無效。");
    }
    private static SearchProgressResetTransaction Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 16 * 1024) throw new InvalidDataException("搜尋重設意圖大小無效。");
        return JsonSerializer.Deserialize<SearchProgressResetTransaction>(file, HubProtocol.Json)
            ?? throw new InvalidDataException("搜尋重設意圖為空。");
    }
    private static void WriteNew(string path, SearchProgressResetTransaction value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(file, value, HubProtocol.Json); file.Flush(true); }
        File.Move(temporary, path, false);
    }
}

public sealed partial class ShopBroker
{
    private readonly ShopSearchProgressResetStore searchResets;
    private CancellationTokenSource searchGenerationStop = new();
    private readonly List<CancellationTokenSource> retiredSearchGenerations = [];
    private Task<ShopCommandResponse>? searchResetBarrier;
    private string? searchResetFailure;
    private int pageWaitMilliseconds;

    public int PageWaitMilliseconds => Volatile.Read(ref pageWaitMilliseconds);
    public void ConfigurePageWaitMilliseconds(int milliseconds) => Volatile.Write(ref pageWaitMilliseconds, ShopPagePacing.ValidateMilliseconds(milliseconds));
    public int PageWaitSeconds => (PageWaitMilliseconds + 999) / 1000;
    public void ConfigurePageWaitSeconds(int seconds) => ConfigurePageWaitMilliseconds(checked(ShopPagePacing.ValidateSeconds(seconds) * 1000));

    private async Task<ShopCommandResponse> ResetSearchProgressAsync(ShopCommandRequest req, Task[] priorRequests, CancellationToken token)
    {
        await Task.WhenAll(priorRequests).WaitAsync(token).ConfigureAwait(false);
        await AcquireForcePreparationGateAsync(token).ConfigureAwait(false);
        string resetDetail;
        try
        {
            token.ThrowIfCancellationRequested();
            // Page cleanup can release nativeGate while a timed-out helper is
            // still alive. Reject before even acquiring the mutable store lease.
            RequireNativeHelperAvailable();
            using var lease = savedScans.AcquireRunLease();
            lock (gate)
            {
                if (activeNativeSource is not null || scanTask is { IsCompleted: false } || scan.Running)
                    throw new InvalidOperationException("搜尋工作尚未完成本機清理；未重設紀錄。");
                RequireNativeHelperAvailable();
                try
                {
                    searchResets.RecoverPending(searchedOwners, savedScans);
                    token.ThrowIfCancellationRequested();
                    var transaction = searchResets.Prepare(searchedOwners, savedScans, req.RequestId);
                    // Durable local intent finishes despite caller cancellation.
                    // It never submits a game request or resolves native pending.
                    searchResets.Complete(transaction, searchedOwners, savedScans);
                    searchResetFailure = null;
                    scan = new() { Detail = "已清除搜尋紀錄；下一次搜尋會建立新計畫。" };
                    scanForceDispatch = false;
                    Audit("SHOP_SEARCH_PROGRESS_RESET", "已建立新搜尋期別並停用舊保存計畫；原生未確認意圖與歷史證據保留。",
                        new { req.RequestId, req.ExpectedSourceSessionKey, transaction.Marker.ResetId,
                            transaction.Marker.PreviousPlanId, Revision = searchedOwners.Revision,
                            NativeGateOwned = true, PriorRequestsSettled = true, GameRequests = 0 });
                    resetDetail = scan.Detail;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception failure) { searchResetFailure = failure.Message; throw; }
            }
        }
        finally { nativeGate.Release(); }
        // Compute WorkSettled and HistoryCanClear from the actual transport and
        // unresolved-source state after releasing this method's gate ownership.
        return State(req.RequestId, "SearchProgressReset", resetDetail, false) with
        {
            SavedScan = null, SearchedOwners = [], HistoryTruncated = false, NextHistoryOwnerId = null,
            Listings = [], ListingsTruncated = false
        };
    }

    private void RequireSearchResetCompleted()
    {
        lock (gate) if (searchResetFailure is { } failure)
            throw new ShopRequestFailure(ShopFailure.Global("SearchProgressResetIncomplete",
                "本機搜尋重設尚未完整提交；保留證據，未開始新搜尋。" + failure));
    }
}
