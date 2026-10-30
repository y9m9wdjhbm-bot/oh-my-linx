using System.Security.Principal;

namespace MemoryHub.Reader;

internal static class AdministratorExecution
{
    internal static void Require()
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new ProbeFailure("ADMIN_REQUIRED","請以管理員權限啟動；尚未存取 LC，未嘗試低權限讀取。");
    }
}
