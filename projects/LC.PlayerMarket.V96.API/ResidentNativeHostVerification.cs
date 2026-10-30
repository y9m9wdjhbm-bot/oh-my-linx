using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;

internal static class ResidentNativeHostVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = (await ResidentNativeHostSelfTest.RunAsync()).ToArray();
        var report = new { Version = "96.0.0", IsAdministrator = admin,
            Success = checks.Length > 0 && checks.All(c => c.Passed),
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), Checks = checks,
            LcReads = 0, NativeCalls = 0, GameInputs = 0, NormalMainStarted = false };
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }
}
