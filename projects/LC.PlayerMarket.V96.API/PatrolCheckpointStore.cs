using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryHub;

namespace LcPlayerMarket;

internal enum PatrolCheckpointPhase { Navigate, ConfirmArrival, Search, VerifyPosition, SettleNavigation, PointCompleted, Completed }

internal sealed record PatrolCheckpoint
{
    public int Schema { get; init; } = 1;
    public long Sequence { get; init; }
    public DateTimeOffset At { get; init; }
    public string RunId { get; init; } = "";
    public string RouteRevision { get; init; } = "";
    public string RouteFingerprint { get; init; } = "";
    public int CurrentRound { get; init; } = 1;
    public int PointIndex { get; init; }
    public int CompletedRounds { get; init; }
    public int TotalRounds { get; init; }
    public int TotalPoints { get; init; }
    public PatrolCheckpointPhase Phase { get; init; }
    public PatrolObservation? Origin { get; init; }
    public SourceIdentity? ImportedSource { get; init; }
    public ulong ImportedSelfId { get; init; }
    public string? PlanId { get; init; }
    public PatrolNavigationRequest? NavigationRequest { get; init; }
    public bool NavigationSettled { get; init; }
    public ShopCommandRequest? ScanRequest { get; init; }
    public bool ScanSettled { get; init; }
    public int RetriesUsed { get; init; }
    public int RecoveryCycles { get; init; }
    public long ResetGeneration { get; init; }
    public string Status { get; init; } = "Saved";
    public string Detail { get; init; } = "";
    public Dictionary<string, string> ImportedLogSha256 { get; init; } = [];
}

internal sealed record PatrolCheckpointReset
{
    public int Schema { get; init; } = 1;
    public long Sequence { get; init; }
    public DateTimeOffset At { get; init; }
    public string ResetId { get; init; } = "";
    public long Generation { get; init; }
    public string? PreviousRunId { get; init; }
    public long PreviousSequence { get; init; }
    public bool CleanupConfirmed { get; init; }
}

/// <summary>Each transition is a new immutable file. Loading never authorizes game input.</summary>
internal sealed class PatrolCheckpointStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object gate = new();
    internal string DirectoryPath { get; }
    internal PatrolCheckpointStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        DirectoryPath = Path.Combine(Path.GetFullPath(stateRoot), "patrol-progress-v52");
    }

    internal PatrolCheckpoint? LoadLatest()
    {
        lock (gate) return LoadLatestCore();
    }

    internal long CurrentResetGeneration { get { lock (gate) return LatestResetCore()?.Generation ?? 0; } }

    private PatrolCheckpoint? LoadLatestCore()
    {
        if (!Directory.Exists(DirectoryPath)) return null;
        string? path = Directory.EnumerateFiles(DirectoryPath, "*.checkpoint.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        var reset = LatestResetCore();
        if (path is null || reset is not null && StringComparer.Ordinal.Compare(Path.GetFileName(path), ResetFileName(reset)) < 0) return null;
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("巡迴恢復紀錄超出大小界線；原檔保留。");
        var value = JsonSerializer.Deserialize<PatrolCheckpoint>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("巡迴恢復紀錄是空值；原檔保留。");
        Validate(value);
        if (value.ResetGeneration != (reset?.Generation ?? 0))
            throw new InvalidDataException("巡迴恢復紀錄屬於已重置的舊世代；不接續舊流程。");
        if (Path.GetFileName(path) != FileName(value)) throw new InvalidDataException("巡迴恢復紀錄檔名與內容不一致。");
        return value;
    }

    internal PatrolCheckpoint Save(PatrolCheckpoint checkpoint)
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            using var lease = new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var previous = LoadLatestCore();
            var reset = LatestResetCore();
            if (checkpoint.ResetGeneration != (reset?.Generation ?? 0))
                throw new InvalidOperationException("本巡迴的進度世代已被重置；舊流程不能晚寫回恢復點。");
            long previousSequence = Math.Max(previous?.Sequence ?? 0, reset?.Sequence ?? 0);
            long previousTicks = Math.Max(previous?.At.UtcTicks ?? 0, reset?.At.UtcTicks ?? 0);
            var next = checkpoint with
            {
                Sequence = checked(previousSequence + 1),
                At = new(Math.Max(DateTimeOffset.UtcNow.UtcTicks, checked(previousTicks + 1)), TimeSpan.Zero)
            };
            Validate(next);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            if (bytes.Length > 256 * 1024) throw new InvalidDataException("巡迴恢復紀錄超出大小界線。");
            string final = Path.Combine(DirectoryPath, FileName(next));
            string prepared = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(flushToDisk: true); }
                File.Move(prepared, final, overwrite: false);
            }
            finally { if (File.Exists(prepared)) File.Delete(prepared); }
            return next;
        }
    }

    /// <summary>Append a tombstone only after every owned native job is settled.</summary>
    internal PatrolCheckpointReset Reset()
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            using var lease = new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var previous = LoadLatestCore(); var priorReset = LatestResetCore();
            if (previous is not null && (previous.NavigationRequest is not null && !previous.NavigationSettled
                || previous.ScanRequest is not null && !previous.ScanSettled))
                throw new InvalidOperationException("原巡迴工作尚未完整釋放；保留原請求及恢復點，不能重置。");
            var reset = new PatrolCheckpointReset
            {
                ResetId = Guid.NewGuid().ToString("N"), Generation = checked((priorReset?.Generation ?? 0) + 1),
                Sequence = checked(Math.Max(previous?.Sequence ?? 0, priorReset?.Sequence ?? 0) + 1),
                At = new(Math.Max(DateTimeOffset.UtcNow.UtcTicks, checked(Math.Max(previous?.At.UtcTicks ?? 0, priorReset?.At.UtcTicks ?? 0) + 1)), TimeSpan.Zero),
                PreviousRunId = previous?.RunId, PreviousSequence = Math.Max(previous?.Sequence ?? 0, priorReset?.Sequence ?? 0), CleanupConfirmed = true
            };
            ValidateReset(reset);
            string final = Path.Combine(DirectoryPath, ResetFileName(reset));
            string prepared = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(JsonSerializer.SerializeToUtf8Bytes(reset, Json)); output.Flush(flushToDisk: true); }
                File.Move(prepared, final, overwrite: false);
            }
            finally { if (File.Exists(prepared)) File.Delete(prepared); }
            return reset;
        }
    }

    private PatrolCheckpointReset? LatestResetCore()
    {
        if (!Directory.Exists(DirectoryPath)) return null;
        string? path = Directory.EnumerateFiles(DirectoryPath, "*.reset.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        if (path is null) return null;
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("巡迴重置紀錄超出大小界線；原檔保留。");
        var reset = JsonSerializer.Deserialize<PatrolCheckpointReset>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("巡迴重置紀錄是空值；不使用舊恢復點。");
        ValidateReset(reset);
        if (Path.GetFileName(path) != ResetFileName(reset)) throw new InvalidDataException("巡迴重置紀錄檔名與內容不符。");
        return reset;
    }

    private static string ResetFileName(PatrolCheckpointReset reset) =>
        reset.At.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + reset.Sequence.ToString("D12", CultureInfo.InvariantCulture)
        + "-" + reset.ResetId + ".reset.json";

    private static void ValidateReset(PatrolCheckpointReset reset)
    {
        if (reset.Schema != 1 || reset.Sequence <= 0 || reset.Generation <= 0 || reset.At == default || reset.At.Offset != TimeSpan.Zero
            || !Guid.TryParseExact(reset.ResetId, "N", out var id) || id == Guid.Empty || !reset.CleanupConfirmed
            || reset.PreviousSequence < 0 || reset.PreviousSequence >= reset.Sequence
            || reset.PreviousRunId is not null && (!Guid.TryParseExact(reset.PreviousRunId, "N", out var run) || run == Guid.Empty))
            throw new InvalidDataException("巡迴重置世代／清理證據無效；不使用較舊恢復點。");
    }

    internal static string Fingerprint(PatrolRouteSnapshot route) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { route.Schema, route.Revision, route.SavedAt, route.RetryLimit,
            route.ArrivalRadius, route.RoundCount, route.Points }, Json)));

    internal static bool Matches(PatrolCheckpoint checkpoint, PatrolRouteSnapshot route) =>
        checkpoint.RouteRevision == route.Revision && checkpoint.RouteFingerprint == Fingerprint(route)
        && checkpoint.TotalRounds == route.RoundCount && checkpoint.TotalPoints == route.Points.Length;

    /// <summary>Explicit migration only: latest stopped LOG, matching route, no prior V52 checkpoint.</summary>
    internal PatrolCheckpoint ImportLatestStoppedRun(PatrolRouteSnapshot route, PatrolObservation verifiedOrigin,
        string logDirectory, string? expectedRunId = null, string? verifiedPlanId = null)
        => ImportLatestStoppedRunCore(route, verifiedOrigin, null, 0, logDirectory, expectedRunId, verifiedPlanId);

    /// <summary>The historical LOG has a full identity fence but no original pose.</summary>
    internal PatrolCheckpoint ImportLatestStoppedRun(PatrolRouteSnapshot route, SourceIdentity importedSource,
        ulong importedSelfId, string logDirectory, string? expectedRunId = null, string? verifiedPlanId = null)
        => ImportLatestStoppedRunCore(route, null, importedSource, importedSelfId, logDirectory, expectedRunId, verifiedPlanId);

    private PatrolCheckpoint ImportLatestStoppedRunCore(PatrolRouteSnapshot route, PatrolObservation? verifiedOrigin,
        SourceIdentity? importedSource, ulong importedSelfId, string logDirectory, string? expectedRunId, string? verifiedPlanId)
    {
        PatrolRouteRules.Validate(route);
        if (LoadLatest() is not null || CurrentResetGeneration != 0) throw new InvalidOperationException("已有新版巡迴恢復或重置紀錄，不會以舊 LOG 覆蓋。");
        string[] files = Directory.EnumerateFiles(Path.GetFullPath(logDirectory), "*.jsonl").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0 || files.Length > 1024) throw new InvalidDataException("舊 LOG 清單缺少或超出界線。");
        JsonElement? latestStart = null, finishStart = null, latestFinish = null;
        long order = 0, startOrder = 0, finishOrder = 0;
        var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (new FileInfo(file).Length > 128 * 1024 * 1024) throw new InvalidDataException("舊 LOG 超出大小界線。");
            foreach (string line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line); var entry = document.RootElement; order++;
                if (!entry.TryGetProperty("kind", out var kind)) continue;
                if (kind.GetString() == "PATROL_START") { latestStart = entry.Clone(); startOrder = order; }
                if (kind.GetString() == "PATROL_FINISHED")
                { latestFinish = entry.Clone(); finishStart = latestStart; finishOrder = order; }
            }
            using var stream = File.OpenRead(file);
            evidence.Add(Path.GetFullPath(file), Convert.ToHexString(SHA256.HashData(stream)));
        }
        if (latestFinish is not { } finished || finishStart is not { } started || startOrder > finishOrder)
            throw new InvalidDataException("最新巡迴沒有完整結束紀錄；沒有使用較舊失敗或停止紀錄。");
        var result = finished.GetProperty("data"); var definition = started.GetProperty("data");
        string runId = result.GetProperty("runId").GetString() ?? "";
        if (result.GetProperty("state").GetString() != "Stopped" || !result.GetProperty("cleanupConfirmed").GetBoolean()
            || expectedRunId is not null && runId != expectedRunId)
            throw new InvalidDataException("最新巡迴並非已清理的人工停止；沒有套用較舊恢復點。");
        int index = result.GetProperty("completedPoints").GetInt32(), round = result.GetProperty("currentRound").GetInt32(),
            completedRounds = result.GetProperty("completedRounds").GetInt32();
        var points = JsonSerializer.Deserialize<PatrolWaypoint[]>(definition.GetProperty("points").GetRawText(), Json);
        if (definition.GetProperty("revision").GetString() != route.Revision
            || definition.GetProperty("roundCount").GetInt32() != route.RoundCount
            || definition.GetProperty("retryLimit").GetInt32() != route.RetryLimit
            || definition.GetProperty("arrivalRadius").GetInt32() != route.ArrivalRadius
            || points is null || !points.SequenceEqual(route.Points)
            || result.GetProperty("totalPoints").GetInt32() != route.Points.Length
            || result.GetProperty("totalRounds").GetInt32() != route.RoundCount
            || completedRounds >= route.RoundCount)
            throw new InvalidDataException("最新停止 LOG 與目前路線不符，或設定輪數已完成。");
        return Save(new()
        {
            RunId = runId, RouteRevision = route.Revision, RouteFingerprint = Fingerprint(route),
            CurrentRound = round, CompletedRounds = completedRounds, PointIndex = index,
            TotalRounds = route.RoundCount, TotalPoints = route.Points.Length,
            Phase = index == route.Points.Length ? PatrolCheckpointPhase.PointCompleted : PatrolCheckpointPhase.Navigate,
            Origin = verifiedOrigin, ImportedSource = importedSource, ImportedSelfId = importedSelfId,
            PlanId = verifiedPlanId, NavigationSettled = true, ScanSettled = true,
            RetriesUsed = result.GetProperty("retriesUsed").GetInt32(), Status = "Stopped",
            Detail = "由最新已清理的人工停止 LOG 匯入；等待使用者按開始，先重新核對來源與位置。",
            ImportedLogSha256 = evidence
        });
    }

    private static string FileName(PatrolCheckpoint checkpoint) =>
        checkpoint.At.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + checkpoint.Sequence.ToString("D12", CultureInfo.InvariantCulture)
        + "-" + checkpoint.RunId + ".checkpoint.json";

    private static void Validate(PatrolCheckpoint checkpoint)
    {
        if (checkpoint.Schema != 1 || checkpoint.Sequence <= 0 || checkpoint.At == default || checkpoint.At.Offset != TimeSpan.Zero
            || !Guid.TryParseExact(checkpoint.RunId, "N", out var run) || run == Guid.Empty
            || !Guid.TryParseExact(checkpoint.RouteRevision, "N", out var revision) || revision == Guid.Empty
            || checkpoint.RouteFingerprint.Length != 64 || !checkpoint.RouteFingerprint.All(Uri.IsHexDigit)
            || checkpoint.CurrentRound < 1 || checkpoint.CurrentRound > checkpoint.TotalRounds
            || checkpoint.TotalRounds is < 1 or > 100 || checkpoint.TotalPoints is < 1 or > 128
            || checkpoint.PointIndex < 0 || checkpoint.PointIndex > checkpoint.TotalPoints
            || checkpoint.CompletedRounds < 0 || checkpoint.CompletedRounds > checkpoint.CurrentRound
            || checkpoint.CompletedRounds < checkpoint.CurrentRound - 1
            || checkpoint.CompletedRounds == checkpoint.CurrentRound && checkpoint.PointIndex != checkpoint.TotalPoints
            || checkpoint.Phase == PatrolCheckpointPhase.Completed && (checkpoint.CompletedRounds != checkpoint.TotalRounds
                || checkpoint.PointIndex != checkpoint.TotalPoints)
            || checkpoint.RetriesUsed < 0 || checkpoint.RecoveryCycles < 0 || checkpoint.ResetGeneration < 0 || !Enum.IsDefined(checkpoint.Phase)
            || checkpoint.Detail.Length > 16000 || checkpoint.Status.Length > 80)
            throw new InvalidDataException("巡迴恢復紀錄的路線、索引、階段或完整來源無效；原檔保留。");
        var source = checkpoint.Origin?.Source ?? checkpoint.ImportedSource;
        ulong self = checkpoint.Origin?.SelfId ?? checkpoint.ImportedSelfId;
        if (source is null || source.ProcessId <= 0 || source.CreationFileTime <= 0 || string.IsNullOrWhiteSpace(source.SessionKey)
            || string.IsNullOrWhiteSpace(source.ImagePath) || string.IsNullOrWhiteSpace(source.ImageBase)
            || source.ImageSize == 0 || source.DiskSha256.Length != 64 || !source.DiskSha256.All(Uri.IsHexDigit) || self == 0
            || checkpoint.Origin is { } origin && (origin.MapId < 0 || origin.X is < 0 or > 65535 || origin.Y is < 0 or > 65535)
            || checkpoint.Origin is null && (checkpoint.ImportedLogSha256.Count == 0
                || checkpoint.NavigationRequest is not null || checkpoint.ScanRequest is not null))
            throw new InvalidDataException("巡迴恢復紀錄缺少完整來源界線，或匯入紀錄混入了未核對的位置／請求。");
        if (checkpoint.ScanRequest is { } scan && (string.IsNullOrWhiteSpace(scan.RequestId)
            || scan.ExpectedSourceSessionKey != source.SessionKey)) throw new InvalidDataException("巡迴恢復搜尋請求的來源不符。");
        if (checkpoint.NavigationRequest is { } navigation && navigation.Expected.Source != source)
            throw new InvalidDataException("巡迴恢復導航請求的來源不符。");
    }
}
