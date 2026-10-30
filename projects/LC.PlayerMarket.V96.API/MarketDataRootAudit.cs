using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace LcPlayerMarket;
internal static class MarketDataRootAudit
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern uint GetFinalPathNameByHandle(nint h,StringBuilder text,uint length,uint flags);
    internal static int Run(string output)
    {
        string root = new LocalStore().Root;
        Directory.CreateDirectory(root);
        string marker=Path.Combine(root,"canonical-path.marker");
        byte[] expected=Encoding.UTF8.GetBytes("LC.PlayerMarket.V38 canonical data root; no game action\n");
        if(!File.Exists(marker)){using var created=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.Read);created.Write(expected);created.Flush(true);}
        using var file=File.OpenRead(marker);var path=new StringBuilder(32768);
        uint result=GetFinalPathNameByHandle(file.SafeFileHandle.DangerousGetHandle(),path,32768,0);
        string final=path.ToString();string sha=Convert.ToHexString(SHA256.HashData(file));
        bool passed=result>0&&result<32768&&string.Equals(final,@"\\?\"+Path.GetFullPath(marker),StringComparison.OrdinalIgnoreCase)&&sha==Convert.ToHexString(SHA256.HashData(expected));
        File.WriteAllText(output,JsonSerializer.Serialize(new{passed,root,marker,final,sha,processId=Environment.ProcessId,buildId=typeof(Program).Assembly.ManifestModule.ModuleVersionId,at=DateTimeOffset.UtcNow}));
        return passed?0:1;
    }
}
