using System.Text.Json;
using MemoryHub;
namespace LcPlayerMarket;
internal static class V95IntegrationSelfTest
{
    internal static async Task<int> RunAsync(string output)
    {
        bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks=OneNearbyShopBaselineSelfTest.Run().ToList();
        checks.AddRange(await PatrolStartupContinuousVerification.RunAsync());
        checks.AddRange(await LivePatrolStartupBridgeSelfTest.RunAsync());
        checks.AddRange(await PatrolNavigationPrewarmSelfTest.RunAsync());
        checks.AddRange(await PatrolStartupIdentityRecoverySelfTest.RunAsync());
        checks.AddRange(await FrozenShoppingDispatchSelfTest.RunAsync());
        checks.AddRange(await PatrolAutoResumeSelfTest.RunFrozenPointTargetsAsync());
        var report=new {Version="95.0.3",IsAdministrator=admin,Success=checks.All(c=>c.Passed),Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),LcReads=0,NativeCalls=0,GameInputs=0,Checks=checks};
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        await JsonSerializer.SerializeAsync(file,report,HubProtocol.Json);file.Flush(true);
        return report.Success?0:1;
    }
}
