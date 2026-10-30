using System.Security.Principal;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class NativeGuardRejectedReconcile
{
    internal static int Run(string inputManifest,string output)
    {
        bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if(!admin)throw new UnauthorizedAccessException("ADMIN_REQUIRED");
        if(File.Exists(output))throw new IOException("Reconciliation output already exists.");
        using var lease=new FileStream(@"D:\lineage\LC_PlayerMarket_UserData\V38\market-session-v55.lock",
            FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        GuardReconcileResult? result=null;string? error=null;
        try{result=ShopNativeGuardRejectedReconciliation.Run(inputManifest);}catch(Exception ex){error=ex.ToString();}
        var report=new{Schema="V82NativeGuardRejectedReconciliationV1",Success=result is not null,IsAdministrator=admin,
            InputManifest=Path.GetFullPath(inputManifest),InputManifestSha256=ShopNativeGuardRejectedReconciliation.Hash(inputManifest),
            Result=result,Error=error,LCRead=0,GameInput=0,NativeCalls=0,AutomaticRetransmission=false};
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(file,report,HubProtocol.Json);file.Flush(true);return result is not null?0:1;
    }
}
