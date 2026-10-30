using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class CaptureTerminalVerification
{
    internal static int RunReport(string output)
    {
        var checks = PatrolNavigationRecoverySelfTest.RunAsync().GetAwaiter().GetResult().ToArray();
        var report = new OfflineCheckReport("83.0.0", checks.Length > 0 && checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }
}