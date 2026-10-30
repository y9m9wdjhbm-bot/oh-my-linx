using System.Diagnostics;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class FastStartupVerification
{
    internal static int RunTests(string output)
    {
        var checks = ScanStartupConcurrencySelfTest.Run().Concat(StartupHistorySelfTest.Run()).ToArray();
        int failed = checks.Count(c => !c.Passed);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = checks.Length - failed, Failed = failed, Checks = checks, GameInputs = 0 }, HubProtocol.Json));
        return failed == 0 ? 0 : 1;
    }

    internal static int MeasureStores(string root, string output)
    {
        root = Path.GetFullPath(root);
        output = Path.GetFullPath(output);
        if (!root.StartsWith(@"D:\lineage\market48-fixture-", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(root) || File.Exists(output)
            || output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var scans = new ShopScanProgressStore(Path.Combine(root, "scan-progress"));
        double scanMs = clock.Elapsed.TotalMilliseconds;
        double scanCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        cpu = process.TotalProcessorTime; clock.Restart();
        var owners = new ShopSearchedOwnerStore(Path.Combine(root, "searched-owners"), Path.Combine(root, "scan-progress"));
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            Success = scans.Error is null && owners.Error is null,
            ScanMs = scanMs, ScanCpuMs = scanCpuMs,
            OwnerMs = clock.Elapsed.TotalMilliseconds, OwnerCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            ScanError = scans.Error, OwnerError = owners.Error, OwnerSummary = owners.Summary(true, ""),
            GameInputs = 0, NativeRequests = 0, ColdFilesystemCacheNotForced = true,
            Notes = "Constructors only; isolated fixture may settle saved indexes. No LC reader/runtime/UI/recovery is started."
        }, HubProtocol.Json));
        return scans.Error is null && owners.Error is null ? 0 : 1;
    }
}
