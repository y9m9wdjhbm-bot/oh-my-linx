using MemoryHub;
using System.Text.Json;

namespace LcPlayerMarket;

internal static class ForceDispatchVerification
{
    internal static int Run(string output)
    {
        var checks=ForceDispatchSelfTest.Run().Concat(ForcePatrolSelfTest.RunAsync().GetAwaiter().GetResult()).ToArray();
        var report=new OfflineCheckReport("83.0.0",checks.Length>0&&checks.All(c=>c.Passed),
            checks.Count(c=>c.Passed),checks.Count(c=>!c.Passed),false,false,false,checks);
        using var file=new FileStream(Path.GetFullPath(output),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(file,report,HubProtocol.Json);file.Flush(true);
        return report.Success?0:1;
    }
}
