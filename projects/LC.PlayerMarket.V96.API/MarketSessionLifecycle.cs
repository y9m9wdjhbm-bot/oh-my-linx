namespace LcPlayerMarket;

internal sealed record MarketOwnedDirectory(string Path, string OwnerRoot, string Kind);
internal sealed record MarketSessionCleanupResult(bool Success, int RemovedDirectories, string[] Errors)
{
    public string[] DeferredErrors { get; init; } = [];
}

// Product history is shared with the web catalog. Runtime shop intents and
// checkpoints belong only to this application instance and are never restored.
internal sealed class MarketSessionLifecycle : IDisposable
{
    private static readonly SemaphoreSlim CleanupWorker = new(1, 1);
    private readonly FileStream lease;
    private readonly MarketOwnedDirectory runtime;
    private readonly string diagnosticBaseRoot;
    private Task<MarketSessionCleanupResult>? shutdownCleanup;
    internal string RuntimeRoot => runtime.Path;
    // Diagnostic files are never runtime input; retain them under this session's unique name.
    internal string DiagnosticsRoot => MarketLogPaths.RuntimeSession(diagnosticBaseRoot, Path.GetFileName(runtime.Path));
    internal DateTimeOffset StartedAt { get; }
    internal Task<MarketSessionCleanupResult> BackgroundCleanup { get; }

    private MarketSessionLifecycle(FileStream lease, MarketOwnedDirectory runtime, DateTimeOffset at, MarketOwnedDirectory[] retired, string diagnosticBaseRoot)
    {
        this.lease = lease; this.runtime = runtime; this.diagnosticBaseRoot = diagnosticBaseRoot; StartedAt = at;
        BackgroundCleanup = Task.Run(() => CleanupAsync(retired));
    }

    internal static MarketSessionLifecycle BeginFresh(LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        string root = Normalize(store.Root); RejectLinks(root); Directory.CreateDirectory(root);
        string lockPath = Path.Combine(root, "market-session-v55.lock"); RejectLinks(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            string runtimeOwner = Path.Combine(root, "sessions-v55"); RejectLinks(runtimeOwner); Directory.CreateDirectory(runtimeOwner);
            var retired = CollectOwned(runtimeOwner, "session").Concat(CollectOwned(root, "history"))
                .Concat(CollectOwned(root, "shop")).Concat(CollectOwned(root, "patrol")).ToList();
            if (store.MobileCatalogDestination is { } destination)
                retired.AddRange(CollectOwned(Path.GetDirectoryName(destination)!, "catalog-history"));
            // Validate every known directory before the first clear marker.
            RejectLinks(store.HistoryDirectory); RejectLinks(Path.Combine(root, "list-state.json"));
            RejectLinks(Path.Combine(root, "standalone", "shops")); RejectLinks(Path.Combine(root, "patrol-progress-v52"));
            DateTimeOffset at = DateTimeOffset.UtcNow;
            retired.AddRange(store.BeginFreshHistory(at));
            retired.AddRange(RetireIfPresent(Path.Combine(root, "standalone", "shops"), root, "shop"));
            retired.AddRange(RetireIfPresent(Path.Combine(root, "patrol-progress-v52"), root, "patrol"));
            string name = at.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var runtime = new MarketOwnedDirectory(Path.Combine(runtimeOwner, name), runtimeOwner, "session");
            ValidateOwned(runtime); Directory.CreateDirectory(runtime.Path);
            return new(lease, runtime, at, retired.DistinctBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray(), store.DiagnosticsRoot);
        }
        catch { lease.Dispose(); throw; }
    }

    internal Task<MarketSessionCleanupResult> CleanupAfterStopAsync(bool cleanupConfirmed)
    {
        if (!cleanupConfirmed) return Task.FromResult(new MarketSessionCleanupResult(false, 0, ["本機後端／導航尚未確認停止，商店與搜尋紀錄保留。"]));
        return shutdownCleanup ??= CleanupStoppedAsync();
    }

    private async Task<MarketSessionCleanupResult> CleanupStoppedAsync()
    {
        var previous = await BackgroundCleanup.ConfigureAwait(false);
        var current = await Task.Run(() => CleanupAsync([runtime])).ConfigureAwait(false);
        // A retired file can remain locked by a reader after this instance's
        // native work has stopped. Report that backlog separately so it cannot
        // make a successfully removed current runtime look like a failed stop.
        if (current.Success) lease.Dispose();
        return new(current.Success, previous.RemovedDirectories + current.RemovedDirectories, current.Errors)
        { DeferredErrors = previous.Errors };
    }

    public void Dispose() => lease.Dispose(); // Abnormal shutdown retains all runtime records.

    internal static MarketOwnedDirectory[] RetireIfPresent(string path, string ownerRoot, string kind)
    {
        path = Normalize(path); ownerRoot = Normalize(ownerRoot); RejectLinks(path); RejectLinks(ownerRoot);
        if (!path.StartsWith(ownerRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("拒絕退役所屬根目錄之外的路徑：" + path);
        if (!Directory.Exists(path)) { if (File.Exists(path)) throw new InvalidDataException("預期目錄卻發現檔案：" + path); return []; }
        string target = Path.Combine(ownerRoot, ".retired-" + kind + "-v55-" + Guid.NewGuid().ToString("N"));
        var retired = new MarketOwnedDirectory(target, ownerRoot, kind); ValidateOwned(retired);
        Directory.Move(path, target); return [retired];
    }

    private static IEnumerable<MarketOwnedDirectory> CollectOwned(string ownerRoot, string kind)
    {
        RejectLinks(ownerRoot); if (!Directory.Exists(ownerRoot)) yield break;
        foreach (string path in Directory.EnumerateDirectories(ownerRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!IsOwnedName(Path.GetFileName(path), kind)) continue;
            var item = new MarketOwnedDirectory(path, ownerRoot, kind); ValidateOwned(item); yield return item;
        }
    }

    private static async Task<MarketSessionCleanupResult> CleanupAsync(IEnumerable<MarketOwnedDirectory> directories)
    {
        await CleanupWorker.WaitAsync().ConfigureAwait(false);
        try
        {
            int removed = 0; var errors = new List<string>();
            foreach (var directory in directories)
            {
                try { ValidateOwned(directory); if (Directory.Exists(directory.Path)) { DeleteTree(directory.Path); removed++; } }
                catch (Exception error) { errors.Add(directory.Path + "：" + error.Message); }
                await Task.Yield();
            }
            return new(errors.Count == 0, removed, errors.ToArray());
        }
        finally { CleanupWorker.Release(); }
    }

    private static void ValidateOwned(MarketOwnedDirectory directory)
    {
        string path = Normalize(directory.Path), owner = Normalize(directory.OwnerRoot);
        if (!string.Equals(Path.GetDirectoryName(path), owner, StringComparison.OrdinalIgnoreCase) || !IsOwnedName(Path.GetFileName(path), directory.Kind))
            throw new InvalidDataException("拒絕清理所屬目錄之外的路徑：" + path);
        RejectLinks(owner); RejectLinks(path);
    }

    private static bool IsOwnedName(string name, string kind)
    {
        if (kind == "session") return name.Length == 51 && name[8] == '-' && name[18] == '-'
            && name[..8].All(char.IsAsciiDigit) && name[9..18].All(char.IsAsciiDigit) && Guid.TryParseExact(name[19..], "N", out _);
        string prefix = ".retired-" + kind + "-v55-";
        return kind is "history" or "catalog-history" or "shop" or "patrol" && name.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    // Non-recursive Windows operations never traverse a junction or symlink.
    // Bounds keep a pathological orphan from occupying the only I/O worker.
    private static void DeleteTree(string root)
    {
        root = Normalize(root); RejectLinks(root);
        var pending = new Stack<(string Path, bool Delete, int Depth)>(); pending.Push((root, false, 0)); int entries = 0;
        while (pending.TryPop(out var next))
        {
            if (next.Depth > 64 || ++entries > 250000) throw new IOException("清理達到本次檔案／層數上限；剩餘孤兒資料待下次啟動續清。");
            if ((File.GetAttributes(next.Path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不接受連結清理目錄：" + next.Path);
            if (next.Delete) { Directory.Delete(next.Path, false); continue; }
            pending.Push((next.Path, true, next.Depth));
            foreach (string child in Directory.EnumerateFileSystemEntries(next.Path, "*", SearchOption.TopDirectoryOnly))
            {
                string full = Normalize(child);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("清理項目離開所屬根目錄。");
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不接受連結清理項目：" + full);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((full, false, next.Depth + 1));
                else { if (++entries > 250000) throw new IOException("清理達到本次檔案上限。"); File.Delete(full); }
            }
        }
    }

    internal static void RejectLinks(string path)
    {
        for (string? current = Normalize(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不接受連結資料路徑：" + current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static void DeleteFixtureDirectory(string path, string ownerRoot, string prefix)
    {
        string full = Normalize(path), owner = Normalize(ownerRoot), name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), owner, StringComparison.OrdinalIgnoreCase) || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidDataException("拒絕清理非隔離 fixture。");
        if (Directory.Exists(full)) DeleteTree(full);
    }
}
