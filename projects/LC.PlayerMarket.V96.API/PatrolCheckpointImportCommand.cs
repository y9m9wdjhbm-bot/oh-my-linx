using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class PatrolCheckpointImportCommand
{
    internal static int Run(string stateRoot, string snapshots, string seedFile, string reportFile)
    {
        if (File.Exists(reportFile)) return 2;
        object result;
        int exit = 0;
        try
        {
            stateRoot = Path.GetFullPath(stateRoot).TrimEnd(Path.DirectorySeparatorChar);
            snapshots = Path.GetFullPath(snapshots);
            const string expectedRoot = @"D:\lineage\LC_PlayerMarket_UserData\V38";
            Need(string.Equals(stateRoot, expectedRoot, StringComparison.OrdinalIgnoreCase), "Import is limited to the verified local data root.");
            var store = new PatrolCheckpointStore(stateRoot);
            var previous = store.LoadLatest();
            if (previous is not null)
            {
                result = new { Success = true, Imported = false, Reason = "Existing V52 checkpoint preserved", previous.PointIndex, previous.CurrentRound,
                    GameInputs = 0, GameMemoryReads = 0, Started = false };
            }
            else
            {
                Need(new FileInfo(seedFile).Length <= 1024 * 1024, "Seed evidence exceeds bounds.");
                using var seed = JsonDocument.Parse(File.ReadAllBytes(seedFile));
                var latest = seed.RootElement.GetProperty("LatestRun");
                var source = JsonSerializer.Deserialize<SourceIdentity>(latest.GetProperty("OriginalSource"), HubProtocol.Json)
                    ?? throw new InvalidDataException("Missing original source identity.");
                ulong selfId = latest.GetProperty("SavedPlan").GetProperty("SelfId").GetUInt64();
                string runId = latest.GetProperty("RunId").GetString() ?? "";
                Need(latest.GetProperty("StopOrigin").GetString() == "UserF9"
                    && !latest.GetProperty("AutoResumeAllowed").GetBoolean(), "The seed must preserve an explicit manual stop.");
                string[] captured = Directory.GetFiles(snapshots, "*.jsonl").Order(StringComparer.Ordinal).ToArray();
                Need(captured.Length is > 0 and <= 1024, "No bounded snapshot session.");
                string liveLogs = Path.Combine(stateRoot, "logs");
                string newest = Directory.EnumerateFiles(liveLogs, "*.jsonl").OrderDescending(StringComparer.Ordinal).First();
                Need(Path.GetFileName(newest) == Path.GetFileName(captured[^1]), "A newer application session exists; older progress was not imported.");
                foreach (string snapshot in captured)
                {
                    string original = Path.Combine(liveLogs, Path.GetFileName(snapshot));
                    using var a = File.OpenRead(snapshot);
                    using var b = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    Need(SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b)), "The live LOG advanced after the snapshot; earlier progress was not imported.");
                }
                var route = new PatrolRouteStore(stateRoot).Load();
                Need(route.Success && route.Snapshot is not null, route.Error ?? "Missing saved route.");
                string planDirectory = Path.Combine(stateRoot, "standalone", "shops", "scan-progress");
                string planFile = Path.GetFullPath(latest.GetProperty("SavedPlanFile").GetString() ?? "");
                Need(string.Equals(Path.GetDirectoryName(planFile), planDirectory, StringComparison.OrdinalIgnoreCase), "Saved plan is outside the exact data directory.");
                using var plan = JsonDocument.Parse(File.ReadAllBytes(planFile));
                var planSource = JsonSerializer.Deserialize<SourceIdentity>(plan.RootElement.GetProperty("source"), HubProtocol.Json);
                Need(planSource == source && plan.RootElement.GetProperty("selfId").GetUInt64() == selfId,
                    "Saved plan does not corroborate the logged source and local character.");
                string? planId = null;
                if (Directory.EnumerateFiles(planDirectory, "*.plan.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault() == planFile)
                    planId = plan.RootElement.GetProperty("planId").GetString();
                var imported = store.ImportLatestStoppedRun(route.Snapshot!, source, selfId, snapshots, runId, planId);
                result = new { Success = true, Imported = true, imported.RunId, imported.PointIndex, imported.CurrentRound,
                    imported.CompletedRounds, imported.PlanId, OriginPoseVerified = false, FreshCaptureRequiredOnStart = true,
                    AutoStarted = false, GameInputs = 0, GameMemoryReads = 0, Directory = store.DirectoryPath };
            }
        }
        catch (Exception error)
        {
            exit = 1;
            result = new { Success = false, Imported = false, Error = error.ToString(), GameInputs = 0, GameMemoryReads = 0, Started = false };
        }
        using var output = new FileStream(reportFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, result, HubProtocol.Json);
        output.Flush(true);
        return exit;
    }

    private static void Need(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
