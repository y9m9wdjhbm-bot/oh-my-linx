using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class MarketV54FeaturesVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        var checks = new List<OfflineCheck>();
        async Task Suite(string name, Func<Task<OfflineCheck[]>> run)
        {
            try { checks.AddRange(await run().ConfigureAwait(false)); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Suite("search_reset_broker_suite", ResetSearchProgressSelfTest.RunAsync);
        await Suite("broker_startup_failure_suite", BrokerInitializationFailureSelfTest.RunAsync);
        await Suite("patrol_reset_suite", PatrolResetSelfTest.RunAsync);
        await Suite("restart_coordinator_suite", SearchRestartCoordinatorSelfTest.RunAsync);
        await Suite("page_wait_preferences_suite", PageWaitPreferencesSelfTest.RunAsync);
        await Suite("page_wait_configuration_suite", ShopPageWaitConfigurationSelfTest.RunAsync);
        await Suite("page_wait_backend_bridge_suite", BackendPageWaitSelfTest.RunAsync);
        await Suite("navigation_bundle_hash_suite", NavigationBundleHashSelfTest.RunAsync);
        await Suite("page_pacing_suite", ShopPagePacingSelfTest.RunAsync);
        await Suite("session_lifecycle_suite", MarketSessionLifecycleSelfTest.RunAsync);
        var report = new OfflineCheckReport("83.0.0", checks.Count > 0 && checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks.ToArray());
        using var file = new FileStream(Path.GetFullPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json).ConfigureAwait(false);
        file.Flush(true);
        return report.Success ? 0 : 1;
    }
}
