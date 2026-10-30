using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record Preferences
{
    public bool LogEnabled { get; init; } = true;
    public bool ShowHistory { get; init; } = true;
    public bool SynchronizeItemFilters { get; init; }
    public bool ShowSearchOverlay { get; init; } = true;
    public bool ShowNavigation { get; init; } = true;
    public bool ResearchProjection { get; init; }
    public bool BottomOrigin { get; init; } = true;
    public int PageWaitSeconds { get; init; } = 3;

    public int PageOpenIntervalMilliseconds { get; init; } = 3000;
    public bool NoAdditionalPageWait { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    internal int EffectivePageOpenIntervalMilliseconds => NoAdditionalPageWait ? 0 : Math.Clamp(PageOpenIntervalMilliseconds, 100, 5000);
    internal static bool ReadNoAdditionalPageWait(JsonElement root) =>
        !root.TryGetProperty("noAdditionalPageWait", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean();
    internal Preferences WithValidPageWait()
    {
        int milliseconds = Math.Clamp(PageOpenIntervalMilliseconds, 100, 5000);
        return this with { PageOpenIntervalMilliseconds = milliseconds, PageWaitSeconds = (milliseconds + 999) / 1000 };
    }
    internal static int ReadPageWaitMilliseconds(JsonElement root)
    {
        if (root.TryGetProperty("pageOpenIntervalMilliseconds", out var precise))
            return precise.ValueKind == JsonValueKind.Number && precise.TryGetInt32(out int ms) ? Math.Clamp(ms, 100, 5000) : 3000;
        if (root.TryGetProperty("pageWaitSeconds", out var legacy) && legacy.ValueKind == JsonValueKind.Number && legacy.TryGetInt32(out int seconds) && seconds > 0)
            return (int)Math.Clamp((long)seconds * 1000, 100, 5000);
        return 3000;
    }
}

internal sealed class LocalStore
{
    public const int ViewHistoryLimit = 2000;
    public string Root { get; }
    public string DiagnosticsRoot { get; }
    private readonly MarketMobileCatalogPublisher? catalog;
    public LocalStore(string? root = null, string? catalogDestination = null, string? diagnosticsRoot = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? Path.Combine(@"D:\lineage\LC_PlayerMarket_UserData", "V38")));
        DiagnosticsRoot = MarketLogPaths.Resolve(root is null ? null : Root, diagnosticsRoot);
        catalog = catalogDestination is null ? MarketMobileCatalogPublisher.ForSource(Root) : new(Root, catalogDestination);
    }
    internal string? MobileCatalogDestination => catalog?.Destination;
    internal bool FreshSessionStarted { get; private set; }
    internal CatalogSyncResult? SynchronizeMobileCatalog(CancellationToken token)=>catalog?.Synchronize(token);
    public event Action<string>? Error;
    public int StoredFileCount { get; private set; }
    public int LastHistoryParsedFiles { get; private set; }
    public int LastHistoryReusedFiles { get; private set; }
    public string HistoryDirectory => Path.Combine(Root, "history");
    private readonly HashSet<string> stored = new(StringComparer.Ordinal);
    private sealed record ListState(int Version, DateTimeOffset ClearedAt, DateTimeOffset? ResumeAfter);
    private ListState? listState;
    private bool listStateLoaded, listStateInvalid;
    private string ListStatePath => Path.Combine(Root, "list-state.json");

    internal MarketOwnedDirectory[] BeginFreshHistory(DateTimeOffset at)
    {
        var clear = new ListState(1, at, null); var resume = new ListState(1, at, at);
        byte[] clearBytes = JsonSerializer.SerializeToUtf8Bytes(clear, HubProtocol.Json);
        byte[] resumeBytes = JsonSerializer.SerializeToUtf8Bytes(resume, HubProtocol.Json);
        MarketOwnedDirectory[] Retire()
        {
            var retired = MarketSessionLifecycle.RetireIfPresent(HistoryDirectory, Root, "history");
            Directory.CreateDirectory(HistoryDirectory); return retired;
        }
        void Commit(byte[] bytes, ListState state)
        {
            MarketSessionLifecycle.RejectLinks(Root); MarketSessionLifecycle.RejectLinks(ListStatePath);
            Directory.CreateDirectory(Root); string temp = Path.Combine(Root, "list-state." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
                File.Move(temp, ListStatePath, true); listState = state; listStateLoaded = true; listStateInvalid = false;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        MarketOwnedDirectory[] removed;
        if (catalog is null) { Commit(clearBytes, clear); removed = Retire(); Commit(resumeBytes, resume); }
        else removed = catalog.BeginFreshHistory(clearBytes, resumeBytes, () => Commit(clearBytes, clear), Retire, () => Commit(resumeBytes, resume));
        stored.Clear(); StoredFileCount = LastHistoryParsedFiles = LastHistoryReusedFiles = 0; FreshSessionStarted = true;
        return removed;
    }

    private void LoadListState()
    {
        if (listStateLoaded) return;
        listStateLoaded = true;
        try
        {
            if (!File.Exists(ListStatePath)) return;
            if (new FileInfo(ListStatePath).Length > 4096) throw new InvalidDataException("清除紀錄大小異常");
            listState = JsonSerializer.Deserialize<ListState>(File.ReadAllText(ListStatePath), HubProtocol.Json);
            if (listState is null || listState.Version != 1 || listState.ClearedAt == default
                || listState.ResumeAfter is { } resume && resume < listState.ClearedAt)
                throw new InvalidDataException("清除紀錄格式無效");
        }
        catch (Exception ex) { listStateInvalid = true; Error?.Invoke("清單清除紀錄無法讀取，暫不還原舊清單；可再次清除以重設：" + ex.Message); }
    }

    public bool IsListingVisible(MarketListing value)
    {
        LoadListState();
        if (listStateInvalid) return false;
        if (listState is null) return true;
        return listState.ResumeAfter is { } after && value.CapturedAt > after
            && value.RequestedAt is { } requested && requested > after;
    }

    public bool ClearListings(DateTimeOffset at) => SaveListState(new(1, at, null));
    public bool ResumeListings(DateTimeOffset at)
    {
        LoadListState();
        if (listStateInvalid) { Error?.Invoke("請先清除清單以重設無效的清除紀錄。"); return false; }
        if (listState is null || listState.ResumeAfter is not null) return true;
        return SaveListState(listState with { ResumeAfter = at < listState.ClearedAt ? listState.ClearedAt : at });
    }

    private bool SaveListState(ListState value)
    {
        try
        {
            Directory.CreateDirectory(Root);
            string temp = Path.Combine(Root, "list-state." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { JsonSerializer.Serialize(file, value, HubProtocol.Json); file.Flush(true); }
            if(catalog is null)File.Move(temp,ListStatePath,true);
            else catalog.CommitMarker(File.ReadAllBytes(temp),value.ResumeAfter is null,()=>File.Move(temp,ListStatePath,true));
            listState = value; listStateLoaded = true; listStateInvalid = false;
            return true;
        }
        catch (Exception ex) { Error?.Invoke("清單狀態無法儲存，未套用清除／重新收錄：" + ex.Message); return false; }
    }

    public Preferences LoadPreferences()
    {
        try
        {
            string path = Path.Combine(Root, "settings.json");
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("設定檔超過大小限制");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            int milliseconds = Preferences.ReadPageWaitMilliseconds(document.RootElement);
            var normalized = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            normalized["pageOpenIntervalMilliseconds"] = milliseconds;
            normalized["pageWaitSeconds"] = (milliseconds + 999) / 1000;
            normalized["noAdditionalPageWait"] = Preferences.ReadNoAdditionalPageWait(document.RootElement);
            return (normalized.Deserialize<Preferences>(HubProtocol.Json) ?? new()).WithValidPageWait();
        }
        catch (Exception ex) { Error?.Invoke("設定讀取失敗：" + ex.Message); return new(); }
    }

    public void SavePreferences(Preferences value)
    {
        value = value.WithValidPageWait();
        try
        {
            Directory.CreateDirectory(Root);
            string temp = Path.Combine(Root, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(value, HubProtocol.Json), new UTF8Encoding(false));
            File.Move(temp, Path.Combine(Root, "settings.json"), true);
        }
        catch (Exception ex) { Error?.Invoke("設定儲存失敗：" + ex.Message); }
    }

    public MarketListing[] LoadHistory(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A normal application start creates an empty product epoch. Even
            // late background history callers must never parse an older file.
            if (FreshSessionStarted) { LastHistoryParsedFiles = LastHistoryReusedFiles = 0; return []; }
            if (!Directory.Exists(HistoryDirectory)) return [];
            var files = new DirectoryInfo(HistoryDirectory).EnumerateFiles("*.json").ToArray();
            StoredFileCount = files.Length;
            LoadListState();
            // A clear with no resume excludes every immutable old page.
            if (listStateInvalid || listState is { ResumeAfter: null }) return [];
            var cached = HistoryReadCache.Load(HistoryDirectory, cancellationToken, message => Error?.Invoke(message));
            LastHistoryParsedFiles = cached.ParsedFileCount; LastHistoryReusedFiles = cached.ReusedFileCount;
            StoredFileCount = cached.FileCount;
            var values = new MarketListingIndex();
            foreach (var value in cached.Listings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stored.Add(Key(value));
                if (IsListingVisible(value)) values.Upsert(value);
            }
            // Apply the view limit after grouping, so repeat revisions cannot evict other stores.
            values.Trim(ViewHistoryLimit);
            return values.Values.OrderByDescending(v => v.CapturedAt).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Error?.Invoke("歷史讀取失敗：" + ex.Message); return []; }
    }

    internal void AdoptHistoryFileCount(int count) => StoredFileCount = Math.Max(StoredFileCount, count);

    public bool SaveListing(MarketListing value)
    {
        if (!Valid(value)) { Error?.Invoke("略過格式不完整的商店歷史"); return false; }
        string key = Key(value);
        if (stored.Contains(key)) return false;
        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            string path = Path.Combine(HistoryDirectory, hash + ".json");
            // An immutable file keeps the original acquisition time and source, independently of optional LOG.
            if (!File.Exists(path))
            {
                string temp = Path.Combine(HistoryDirectory, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { JsonSerializer.Serialize(file, value, HubProtocol.Json); file.Flush(true); }
                try { File.Move(temp, path, false); StoredFileCount++; }
                catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
            }
            stored.Add(key);
            try{catalog?.PublishHistory(path);}catch(Exception error){Error?.Invoke("商品已保存在本機，但手機副本更新失敗："+error.Message+"；下次啟動會核對補齊。");}
            return true;
        }
        catch (Exception ex) { Error?.Invoke("商店歷史儲存失敗：" + ex.Message); return false; }
    }

    internal static bool Valid(MarketListing? v) => v is not null && !string.IsNullOrWhiteSpace(v.Revision)
        && !string.IsNullOrWhiteSpace(v.SourceSessionKey) && v.Owner is not null && v.Owner.Id != 0
        && v.Owner.Name is not null && v.Owner.LocalIdentity is not null
        && v.Rows is not null && v.Rows.Length <= 256 && v.Rows.All(r => r is not null)
        && v.ViewerDirection is ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell && v.CapturedAt != default;
    internal static string Key(MarketListing v) => v.SourceSessionKey + "|" + v.Revision;
}

internal sealed class AppLog : IDisposable
{
    private readonly object gate = new();
    private StreamWriter? writer;
    private readonly string folder;
    private const long MaximumPartBytes = 8 * 1024 * 1024;
    private const int RotationReserveBytes = 2048;
    private string sessionStem = "";
    private int part;
    private long partBytes;
    public bool Enabled { get { lock (gate) return writer is not null; } }
    public event Action<string>? Failure;
    public string? CurrentPath { get; private set; }
    internal string DirectoryPath => folder;
    public AppLog(string? root = null, string? diagnosticsRoot = null)
    { folder = MarketLogPaths.Application(MarketLogPaths.Resolve(root, diagnosticsRoot)); }
    public void SetEnabled(bool enabled)
    {
        lock (gate)
        {
            if (enabled == (writer is not null)) return;
            if (!enabled) { Write("LOG_DISABLED", "使用者停用 LOG"); writer?.Dispose(); writer = null; return; }
            try
            {
                Directory.CreateDirectory(folder);
                sessionStem = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N");
                part = 0;
                OpenPart(null);
                Write("LOG_ENABLED", "使用者啟用 LOG", new { version = typeof(Program).Assembly.GetName().Version?.ToString() });
            }
            catch (Exception ex) { writer?.Dispose(); writer = null; Failure?.Invoke("LOG 開啟失敗：" + ex.Message); }
        }
    }
    public void Write(string kind, string message, object? data = null)
    {
        lock (gate)
        {
            if (writer is null) return;
            try
            {
                string line = Serialize(kind, message, data);
                int bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (partBytes + bytes + RotationReserveBytes > MaximumPartBytes)
                {
                    string? previous = CurrentPath;
                    WriteLine(Serialize("LOG_PART_END", "LOG 達分檔大小，接續記錄於下一檔。", new { part, nextPart = part + 1 }));
                    writer.Dispose(); writer = null;
                    OpenPart(previous);
                }
                // Never split or discard an individual record, even when a single record exceeds the target part size.
                WriteLine(line);
            }
            catch (Exception ex) { writer?.Dispose(); writer = null; Failure?.Invoke("LOG 寫入失敗，已停用：" + ex.Message); }
        }
    }
    private static string Serialize(string kind, string message, object? data) =>
        JsonSerializer.Serialize(new { at = DateTimeOffset.Now, kind, message, data }, HubProtocol.Json);
    private void OpenPart(string? previousPath)
    {
        string path = Path.Combine(folder, sessionStem + "-" + (++part).ToString("D4") + ".jsonl");
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            { AutoFlush = true, NewLine = "\n" };
        CurrentPath = path; partBytes = 0;
        WriteLine(Serialize("LOG_PART_START", "開始新的 LOG 分檔；原有紀錄保留。",
            new { version = typeof(Program).Assembly.GetName().Version?.ToString(), session = sessionStem, part, previousPath, maximumPartBytes = MaximumPartBytes }));
    }
    private void WriteLine(string line)
    {
        writer!.WriteLine(line);
        partBytes += Encoding.UTF8.GetByteCount(line) + 1;
    }
    public void Dispose() { lock (gate) { Write("END", "查詢程式關閉"); writer?.Dispose(); writer = null; } }
}

