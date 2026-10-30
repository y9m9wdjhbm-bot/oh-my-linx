using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LcPlayerMarket;

/// <summary>Immutable route revisions; loading cannot start movement or replace a damaged latest revision.</summary>
internal sealed class PatrolRouteStore
{
    private const int MaximumFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object gate = new();
    internal string DirectoryPath { get; }

    internal PatrolRouteStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        DirectoryPath = Path.Combine(Path.GetFullPath(stateRoot), "patrol-routes");
    }

    internal PatrolRouteLoadResult Load()
    {
        lock (gate)
        {
            try
            {
                // Existing revisions, including an explicitly saved empty route,
                // are authoritative. Never hide a broken current revision.
                string? current = LatestPath(DirectoryPath);
                if (current is not null) return new(ReadVerified(current).Snapshot, null);
                if (FindPreviousRevision() is null) return new(new(), null);
                Directory.CreateDirectory(DirectoryPath);
                using var writerLock = OpenWriterLock();
                return new(ReadCurrentOrImport(), null);
            }
            catch (Exception error) { return new(null, "巡迴清單無法載入；原檔保留，未重設清單：" + error.Message); }
        }
    }

    internal bool TrySave(IReadOnlyList<PatrolWaypoint> points, int retryLimit, int arrivalRadius,
        out PatrolRouteSnapshot? saved, out string error, string? expectedRevision = null, int? roundCount = null)
    {
        saved = null; error = "";
        lock (gate)
        {
            try
            {
                PatrolRouteRules.ValidatePoints(points, retryLimit);
                PatrolRouteRules.ValidateArrivalRadius(arrivalRadius);
                if (roundCount is { } requestedRounds) PatrolRouteRules.ValidateRoundCount(requestedRounds);
                // Preserve caller order but never retain the caller's mutable array.
                var copy = points.Select(point => point with { }).ToArray();
                Directory.CreateDirectory(DirectoryPath);
                using var writerLock = OpenWriterLock();
                // Recheck the newest revision under the writer lock. A broken
                // file must not be hidden by silently saving a replacement list.
                var previous = ReadCurrentOrImport();
                // Empty means the editor observed no saved revision. Null keeps
                // compatibility with callers that do not edit a loaded snapshot.
                if (expectedRevision is not null && !string.Equals(expectedRevision, previous.Revision, StringComparison.Ordinal))
                    throw new InvalidOperationException("巡迴清單已由其他視窗或版本更新；請重新載入後再修改。");
                long ticks = Math.Max(DateTimeOffset.UtcNow.UtcTicks, checked(previous.SavedAt.UtcTicks + 1));
                var next = new PatrolRouteSnapshot
                {
                    Revision = Guid.NewGuid().ToString("N"), SavedAt = new(ticks, TimeSpan.Zero),
                    RetryLimit = retryLimit, ArrivalRadius = arrivalRadius, RoundCount = roundCount ?? previous.RoundCount, Points = copy
                };
                PatrolRouteRules.Validate(next);
                string final = Path.Combine(DirectoryPath, FileName(next));
                PatrolRoundSettings.Commit(final, next, JsonSerializer.SerializeToUtf8Bytes(next, Json));
                saved = PatrolRouteRules.Copy(next);
                return true;
            }
            catch (Exception failure)
            {
                error = "巡迴清單未保存，沒有套用變更；既有版本保留：" + failure.Message;
                return false;
            }
        }
    }

    private FileStream OpenWriterLock() => new(Path.Combine(DirectoryPath, "writer.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    // Caller owns writer.lock; recheck after acquiring it before importing or
    // saving. Import only the latest definition, preserving its exact bytes.
    private PatrolRouteSnapshot ReadCurrentOrImport()
    {
        string? current = LatestPath(DirectoryPath);
        if (current is not null) return ReadVerified(current).Snapshot;
        string? previous = FindPreviousRevision();
        if (previous is null) return new();
        var verified = ReadVerified(previous);
        string final = Path.Combine(DirectoryPath, Path.GetFileName(previous));
        PatrolRoundSettings.Commit(final, verified.Snapshot, verified.Bytes);
        return verified.Snapshot;
    }

    private string? FindPreviousRevision()
    {
        var state = Directory.GetParent(DirectoryPath)!;
        var parent = state.Parent;
        if (parent is null || !string.Equals(parent.Name, "LC.PlayerMarket", StringComparison.OrdinalIgnoreCase)
            || !TryVersion(state.Name, out int currentVersion) || !parent.Exists) return null;
        // Compare integer versions, not folder names or revision timestamps.
        // The closest prior version containing a saved route is authoritative,
        // even when its newest revision is empty or invalid.
        var candidates = parent.EnumerateDirectories().Select(directory =>
            (Directory: directory, Version: TryVersion(directory.Name, out int version) ? version : 0))
            .Where(item => item.Version > 0 && item.Version < currentVersion)
            .OrderByDescending(item => item.Version);
        foreach (var candidate in candidates)
        {
            string? previous = LatestPath(Path.Combine(candidate.Directory.FullName, "patrol-routes"));
            if (previous is not null) return previous;
        }
        return null;
    }

    private static bool TryVersion(string name, out int version)
    {
        version = 0;
        return name.Length > 1 && name[0] == 'V'
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version > 0 && name == "V" + version.ToString(CultureInfo.InvariantCulture);
    }

    private static string? LatestPath(string directory) => !Directory.Exists(directory) ? null
        : Directory.EnumerateFiles(directory, "*.route.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();

    private static (PatrolRouteSnapshot Snapshot, byte[] Bytes) ReadVerified(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("最新巡迴清單大小不合法。");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("最新巡迴清單大小不合法。");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        var value = JsonSerializer.Deserialize<PatrolRouteSnapshot>(bytes, Json)
            ?? throw new InvalidDataException("最新巡迴清單為空值。");
        PatrolRouteRules.Validate(value);
        if (!string.Equals(Path.GetFileName(path), FileName(value), StringComparison.Ordinal))
            throw new InvalidDataException("最新巡迴清單的檔名與修訂識別不符。");
        value = PatrolRoundSettings.Read(path, value, bytes);
        PatrolRouteRules.Validate(value);
        return (PatrolRouteRules.Copy(value), bytes);
    }

    private static string FileName(PatrolRouteSnapshot value) => value.SavedAt.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)
        + "-" + value.Revision + ".route.json";
}
