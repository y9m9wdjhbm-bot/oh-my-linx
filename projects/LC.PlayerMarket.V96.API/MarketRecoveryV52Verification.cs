using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class MarketRecoveryV54Verification
{
    internal static async Task<int> RunAsync(string output)
    {
        var checks = new List<OfflineCheck>();
        async Task Suite(string name, Func<Task<OfflineCheck[]>> run)
        {
            try { checks.AddRange(await run().ConfigureAwait(false)); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Suite("startup_readiness_suite", StartupReadinessSelfTest.RunAsync).ConfigureAwait(false);
        await Suite("patrol_auto_resume_suite", PatrolAutoResumeSelfTest.RunAsync).ConfigureAwait(false);
        await Suite("patrol_resume_navigation_suite", PatrolResumeNavigationSelfTest.RunAsync).ConfigureAwait(false);
        try { checks.AddRange(ShopStartupTraceSelfTest.Run()); }
        catch (Exception error) { checks.Add(new("startup_trace_suite", false, error.ToString())); }
        try { checks.AddRange(ShopOwnerTailReplaySelfTest.Run()); }
        catch (Exception error) { checks.Add(new("owner_tail_replay_suite", false, error.ToString())); }
        try { checks.AddRange(ForcePreflightReplaySelfTest.Run()); }
        catch (Exception error) { checks.Add(new("force_preflight_replay_suite", false, error.ToString())); }
        var report = new OfflineCheckReport("83.0.0", checks.Count > 0 && checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks.ToArray());
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json).ConfigureAwait(false);
        file.Flush(true);
        return report.Success ? 0 : 1;
    }
}
