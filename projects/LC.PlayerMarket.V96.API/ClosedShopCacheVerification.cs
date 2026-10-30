using System.Security.Principal;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;
namespace LcPlayerMarket;

internal static class ClosedShopCacheVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = ClosedShopReadCacheSelfTest.Run((sample, selected, self, now) =>
        {
            var owner = new MarketOwner { Id = 22, Name = "fixture owner", LocalIdentity = "fixture-owner-22", Position = new(100, 101),
                ObservedAt = now.AddHours(-1), ShopStateSampledAt = now.AddHours(-1), ShopState = ShopEligibilityPolicy.ShopActionObserved,
                ActionType = ".?AVShopAction@lineage@@", ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            var targets = new ShopTargetSnapshot { SnapshotId = "closed-full-cache-point", SourceSessionKey = sample.Binding.Source.SessionKey,
                SelfId = self, SelfPosition = new(100, 100), RefreshedAt = now.AddHours(-1), CanDispatch = true, Owners = [owner] };
            var prepared = ShopForceDispatchPolicy.PrepareFrozenShoppingDispatch(() => sample, sample.Binding.Source, selected, targets, owner, () => now);
            prepared.RequireFresh(now);
            if (prepared.Context is not null || prepared.Binding.BindingOnly || prepared.Binding.ClosedReadReuse?.Scope != "ClosedFullShopCache") return false;
            bool rejected = false;
            try { prepared.RequireFresh(now.AddSeconds(3)); } catch (InvalidOperationException) { rejected = true; }
            return rejected;
        }).Select(c => new OfflineCheck(c.Name, c.Passed, c.Detail)).ToList();
        checks.AddRange(await BackgroundShopDeferralSelfTest.RunAsync());
        var report = new { Version = "96.0.1", Success = checks.Count > 0 && checks.All(c => c.Passed),
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), IsAdministrator = admin,
            LcReads = 0, NativeCalls = 0, GameInputs = 0, OpenedGameProcess = false, CreatedWindow = false, ConnectedToService = false, Checks = checks };
        using var file = new FileStream(Path.GetFullPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json); file.Flush(true); return report.Success ? 0 : 1;
    }
}
