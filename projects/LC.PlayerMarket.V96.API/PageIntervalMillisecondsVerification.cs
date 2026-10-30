using System.Text.Json;
using MemoryHub;
namespace LcPlayerMarket;

internal static class PageIntervalMillisecondsVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = new List<OfflineCheck>();
        checks.AddRange(await ShopPageWaitConfigurationSelfTest.RunAsync());
        checks.AddRange(await PageWaitPreferencesSelfTest.RunAsync());
        checks.AddRange(await BackendPageWaitSelfTest.RunAsync());
        checks.AddRange(LiveAutomaticPageOutcomePolicy.RunSelfTest());
        var report = new { Version = "95.0.3", Success = checks.Count > 0 && checks.All(c => c.Passed), IsAdministrator = admin,
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), Checks = checks,
            AlreadyInPagePacingSuite = "PagePacingElapsedSelfTest is intentionally not rerun or counted here.",
            LcReads = 0, NativeCalls = 0, GameInputs = 0, ProductStarted = false };
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }
}
