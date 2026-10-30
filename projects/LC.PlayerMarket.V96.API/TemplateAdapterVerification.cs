using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;

namespace LcPlayerMarket;
internal static class TemplateAdapterVerification
{
    internal static int RunCurrent(string currentProfile,string output)
    {
        var checks=TemplateReaderProfileSelfTest.Run(output+".fixtures",currentProfile);
        var report=new{Version="83.0.0",Success=checks.All(c=>c.Passed),Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),
            FrozenProfile=currentProfile,TemplateImportIsCandidate=true,RuntimeReportFabricated=false,GameMemoryReads=0,NativeRequests=0,GameInputs=0,Checks=checks};
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(file,report,HubProtocol.Json);file.Flush(true);return report.Success?0:1;
    }

    internal static int Run(string output)
    {
        var checks=TemplateReaderProfileSelfTest.Run(output+".fixtures");
        var report=new{Version="83.0.0",Success=checks.All(c=>c.Passed),Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),
            TemplateImportIsCandidate=true,RuntimeReportFabricated=false,GameMemoryReads=0,NativeRequests=0,GameInputs=0,Checks=checks};
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(file,report,HubProtocol.Json);file.Flush(true);return report.Success?0:1;
    }
}
