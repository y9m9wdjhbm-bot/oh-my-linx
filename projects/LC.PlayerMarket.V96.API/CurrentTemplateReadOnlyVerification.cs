using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;

namespace LcPlayerMarket;

/// <summary>Isolated current-template consumer reads. No backend, broker, helper or game input is constructed.</summary>
internal static class CurrentTemplateReadOnlyVerification
{
    internal static int Run(string templatePath,string commaSeparatedFields,string outputDirectory)
    {
        const string FrozenProfileSha="03A75CF5F97B43E842ED620B46848E3D135F12E9F5B2C52C502D3025F6C7D868";
        const string CurrentBuildSha="79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9";
        using var identity=WindowsIdentity.GetCurrent();
        bool administrator=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if(!administrator)throw new InvalidOperationException("ADMIN_REQUIRED");
        string output=Path.GetFullPath(outputDirectory);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("NEW_OUTPUT_REQUIRED");
        string[] requested=commaSeparatedFields.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        string[] admitted=["selfId","position","mapName","actorViewport","nearbyPlayers","party","playerShopStates","personalShopListing"];
        if(requested.Length==0||requested.Distinct(StringComparer.Ordinal).Count()!=requested.Length||requested.Any(key=>!admitted.Contains(key,StringComparer.Ordinal)))
            throw new InvalidDataException("READ_SELECTION_INVALID");
        if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(templatePath)))!=FrozenProfileSha)
            throw new InvalidDataException("FROZEN_TEMPLATE_CHANGED");
        Directory.CreateDirectory(output);
        string profiles=Path.Combine(output,"isolated-profiles");
        var imported=TemplateProfileImport.Import(templatePath,profiles);
        using var log=new StreamWriter(new FileStream(Path.Combine(output,"read-only.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)){AutoFlush=true};
        void Record(object value)=>log.WriteLine(JsonSerializer.Serialize(value,HubProtocol.Json));
        long auditedReads=0,auditedBytes=0;
        var rounds=new List<object>();
        SourceIdentity? bound=null;
        int passed=0;
        var errors=new List<string>();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var selection=new ReadSelection(requested);
        using(var source=new CurrentLcSource(Record,(address,length)=>{auditedReads++;auditedBytes+=length;},profiles,reuseVerifiedMetadata:false))
        {
            source.Configure(selection);
            for(int round=1;round<=3;round++)
            {
                ReadResult? sample=null;string? error=null;bool equal=false,fresh=false,observed=false;
                try
                {
                    sample=source.ReadSlice(selection,deadline.Token);
                    if(bound is null&&sample.Source is not null)bound=sample.Source;
                    DateTimeOffset at=DateTimeOffset.UtcNow;
                    equal=bound is not null&&sample.Source==bound&&bound.DiskSha256==CurrentBuildSha;
                    fresh=requested.All(key=>sample.Fields.TryGetValue(key,out var field)&&field.Fresh(at)&&field.Value is not null);
                    observed=requested.All(key=>sample.Fields.TryGetValue(key,out var field)&&field.State=="Observed");
                    if(!equal)error="Full current SourceIdentity does not close against the first sample";
                    else if(!observed||!fresh)error="Requested fields include Unknown, unobserved or stale values";
                    if(error is null)passed++;
                }
                catch(Exception failure){error=failure.ToString();}
                if(error is not null)errors.Add($"Round {round}: {error}");
                var record=new{Round=round,Success=error is null,At=DateTimeOffset.UtcNow,RequestedFields=requested,
                    FullSourceIdentityEqual=equal,RequiredFieldsObserved=observed,RequiredFieldsFresh=fresh,
                    Source=sample?.Source,State=sample?.State,Detail=sample?.Detail,Fields=sample?.Fields,
                    Reads=sample?.Reads,ReadBytes=sample?.ReadBytes,Error=error};
                rounds.Add(record);Record(record);
                if(round<3)deadline.Token.WaitHandle.WaitOne(150);
            }
        }
        var report=new{Version="85.0.0",Success=passed==3,State=passed==3?"RequestedReadCapabilitiesObserved":"RequestedReadCapabilitiesUnknownOrFailed",
            IsAdministrator=administrator,PassedRounds=passed,RequestedFields=requested,BoundSource=bound,
            TemplatePath=Path.GetFullPath(templatePath),TemplateSha256=FrozenProfileSha,Imported=imported,
            IsolatedProfiles=true,AuditedReads=auditedReads,AuditedReadBytes=auditedBytes,Rounds=rounds,Errors=errors,
            NativeRequests=0,GameInput=0,MemoryWrites=0,ConstructedBroker=false,ConstructedNativeHelper=false,
            ShopUnknownPromoted=false,LiveShopQueryVerified=false,LivePatrolVerified=false,ServerAcknowledgmentVerified=false};
        using var result=new FileStream(Path.Combine(output,"result.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(result,report,HubProtocol.Json);result.Flush(true);
        return report.Success?0:1;
    }
}
