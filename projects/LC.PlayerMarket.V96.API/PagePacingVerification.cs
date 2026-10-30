using MemoryHub;
using System.Text.Json;
namespace LcPlayerMarket;
internal static class PagePacingVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        OfflineCheck[] checks;
        try { checks = [.. await ShopPagePacingSelfTest.RunAsync().ConfigureAwait(false), .. await PagePacingElapsedSelfTest.RunAsync().ConfigureAwait(false)]; }
        catch (Exception error) { checks = [new("page_pacing_suite", false, error.ToString())]; }
        var report = new OfflineCheckReport("83.0.0", checks.Length > 0 && checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(Path.GetFullPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json).ConfigureAwait(false);
        file.Flush(true);
        return report.Success ? 0 : 1;
    }
}
