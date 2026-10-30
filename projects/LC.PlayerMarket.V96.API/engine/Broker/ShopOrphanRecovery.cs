using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MemoryHub.Service;

// A resolved filename is not settlement evidence. This reader closes the full
// original request chain before the existing startup recovery may mutate it.
internal sealed record OrphanRecoveryBinding(string SessionFile,string RequestId,SourceIdentity Source,
    MarketOwner Owner,string Direction,DateTimeOffset At,string[] EvidenceFiles,string Fingerprint);
internal static class ShopOrphanRecovery
{
    // The recorded source already has 337 descriptors (484 KiB). Bound the
    // complete history by both records and bytes, rather than truncating it.
    private const int MaximumResolvedRecords=16384;
    private const long MaximumResolvedBytes=32L*1024*1024;
    internal static bool TryBind(string root,string requestId,ShopScanProgressStore scans,
        out OrphanRecoveryBinding? binding,out string reason)
    {
        binding=null;
        try
        {
            root=Path.GetFullPath(root);
            if(requestId.Length is 0 or >128||requestId.Any(char.IsControl))throw new InvalidDataException("Invalid request identity.");
            string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
            string intentPath=Path.Combine(root,"scan-progress","source-intents",key+".intent.json");
            var intent=Read<ScanIntentEvidence>(intentPath,32768);
            if(intent.Source is null||intent.RequestId!=requestId||intent.Settled||intent.Entered||intent.Returned
                ||scans.Error is not null||scans.Source!=intent.Source||scans.PlanId!=intent.PlanId)
                throw new InvalidDataException("Orphan source or current plan differs.");
            string prefix=$"{intent.Source.ProcessId}-{intent.Source.CreationFileTime}";
            if(File.Exists(Path.Combine(root,"sessions",prefix+".pending.json")))throw new InvalidDataException("A live session pending exists.");
            CheckShared(intent.Source);
            string[] matches=FindResolved(Path.Combine(root,"sessions"),intent.Source,requestId);
            if(matches.Length!=1)throw new InvalidDataException("Resolved request binding is absent or ambiguous.");
            string session=matches[0];using var sessionDoc=Document(session,32768);var p=sessionDoc.RootElement;
            var source=p.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json)!;
            var owner=p.GetProperty("owner").Deserialize<MarketOwner>(HubProtocol.Json)!;
            string direction=p.GetProperty("viewerDirection").GetString()!;
            if(source is null||source!=intent.Source||!ShopNativePackage.SupportsBuild(source)||owner is null||owner.Id==0||string.IsNullOrEmpty(owner.LocalIdentity)
                ||p.GetProperty("state").GetString()!="NativeLaunchIntent_NoAutomaticRetry"
                ||p.GetProperty("targetOwner").GetUInt64()!=owner.Id||direction is not("ViewerBuy" or "ViewerSell"))
                throw new InvalidDataException("Resolved source or owner differs.");
            string native=p.GetProperty("nativeLog").GetString()!;
            string nativeName=Path.GetFileName(native);
            if(nativeName.Length!=38||!Guid.TryParseExact(nativeName[..32],"N",out _)
                ||!nativeName.EndsWith(".jsonl",StringComparison.Ordinal)
                ||!string.Equals(Path.GetFullPath(native),Path.Combine(root,"native",nativeName),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native path outside this journal.");
            string requestPath=Path.Combine(root,"requests",key+".request.json"),responsePath=Path.Combine(root,"requests",key+".response.json");
            using var requestDoc=Document(requestPath,128*1024);var accepted=requestDoc.RootElement;
            var request=accepted.GetProperty("request").Deserialize<ShopCommandRequest>(HubProtocol.Json)!;
            string signature=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,HubProtocol.Json))));
            if(request is null||request.RequestId!=requestId||request.Command!=ShopCommands.OpenShop
                ||request.ExpectedSourceSessionKey!=source.SessionKey||request.OwnerId!=owner.Id
                ||request.ExpectedOwnerIdentity!=owner.LocalIdentity||request.ViewerDirection!=direction
                ||accepted.GetProperty("signature").GetString()!=signature
                ||accepted.GetProperty("state").GetString()!="ScanChild_NoAutomaticReplay")
                throw new InvalidDataException("Archived request identity or signature differs.");
            using var responseDoc=Document(responsePath,16*1024*1024);var response=responseDoc.RootElement;
            if(response.GetProperty("requestId").GetString()!=requestId||response.GetProperty("currentSourceSessionKey").GetString()!=source.SessionKey
                ||response.GetProperty("state").GetString()!="NativeRejected"||response.GetProperty("nativeEntered").GetBoolean()
                ||response.GetProperty("nativeReturned").GetBoolean()||response.GetProperty("responseVerified").GetBoolean()
                ||!response.GetProperty("sessionBlocked").GetBoolean()||response.GetProperty("workSettled").GetBoolean()
                ||response.GetProperty("failurePhase").GetString()!="Preflight")
                throw new InvalidDataException("Archived response is not the exact blocked preflight failure.");
            string code=response.GetProperty("failureCode").GetString()!;
            if(code is not("NativeCodeUnavailable" or "NativePreflightReadUnavailable"))throw new InvalidDataException("Unsupported preflight failure.");
            ulong image=ulong.Parse(source.ImageBase.Replace("0x","",StringComparison.OrdinalIgnoreCase),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            var proof=ShopNativeOutcome.ParseFile(native,source,image,owner.Id,1);
            if(!proof.SafeToSettle||!proof.NoEntryConfirmed||proof.Entered||proof.Returned
                ||proof.Failure is not{Phase:"Preflight"}||proof.Failure.Code!=code
                ||proof.Failure.Rva!=response.GetProperty("failureRva").GetString())
                throw new InvalidDataException("Full helper no-dispatch and cleanup proof is missing.");
            string[] plans=Directory.GetFiles(Path.Combine(root,"scan-progress"),"*-"+intent.PlanId+".plan.json");
            if(plans.Length!=1)throw new InvalidDataException("Plan binding is ambiguous.");
            var plan=Read<SavedScanPlan>(plans[0],4*1024*1024);
            if(plan.Source!=source||plan.PlanId!=intent.PlanId||plan.OriginalSnapshotId!=request.TargetSnapshotId
                ||plan.Owners.Count(x=>x.Id==owner.Id&&x.LocalIdentity==owner.LocalIdentity)!=1)
                throw new InvalidDataException("Original plan owner or snapshot differs.");
            string eventDir=Path.Combine(root,"scan-progress",intent.PlanId+".events");
            string[] events=Directory.GetFiles(eventDir,"*.json").Order(StringComparer.Ordinal).ToArray();
            if(events.Length is 0 or >8192)throw new InvalidDataException("Terminal events exceed bounds.");
            var last=Read<ScanProgressEvent>(events[^1],32768);
            if(last.PlanId!=intent.PlanId||last.RequestId!=requestId||last.OwnerId!=owner.Id||last.OwnerIdentity!=owner.LocalIdentity
                ||last.Direction!=direction||last.NativeEntered||last.NativeReturned||last.ResponseVerified
                ||last.Kind is not("Result" or "NativeSettled"))throw new InvalidDataException("Terminal event differs or dispatch is possible.");
            // An interrupted recovery can have replaced the blocked tail, but
            // only its immutable original receipt permits that continuation.
            string original=Path.Combine(root,"recovery",key,"original-terminal-result.json");
            string receipt=Path.Combine(root,"recovery",key,"proof.json");
            if(last.Kind!="Result"||!last.SessionBlocked)
            {
                if(!File.Exists(receipt)||!File.Exists(original))throw new InvalidDataException("No original blocked tail for recovery continuation.");
                var old=Read<ScanProgressEvent>(original,32768);
                if(old.RequestId!=requestId||old.PlanId!=intent.PlanId||old.OwnerId!=owner.Id||old.OwnerIdentity!=owner.LocalIdentity
                    ||old.Direction!=direction||old.Kind!="Result"||!old.SessionBlocked||old.NativeEntered||old.NativeReturned||old.ResponseVerified
                    ||last.Sequence!=(last.Kind=="NativeSettled"?old.Sequence:old.Sequence+1))throw new InvalidDataException("Recovery checkpoint differs.");
            }
            string[] inputs=[session,intentPath,requestPath,responsePath,native,plans[0]];
            string fingerprint=Fingerprint(inputs);
            string savedBinding=Path.Combine(root,"recovery",key,"orphan-binding.json");
            if(File.Exists(receipt))
            {
                var saved=Read<OrphanRecoveryBinding>(savedBinding,32768);
                if(saved.RequestId!=requestId||saved.Source!=source||saved.Fingerprint!=fingerprint)
                    throw new InvalidDataException("Original orphan recovery binding changed.");
                foreach(string input in inputs)
                    if(ManualLockReleaseStore.Hash(input)!=ManualLockReleaseStore.Hash(Path.Combine(root,"recovery",key,"orphan-"+Path.GetFileName(input))))
                        throw new InvalidDataException("Original orphan backup changed.");
            }
            binding=new(session,requestId,source,owner,direction,p.GetProperty("at").GetDateTimeOffset(),inputs,fingerprint);
            reason="完整原始請求與 helper 證據證實未派送；可一致結清孤兒意圖並保持停止。";
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        {reason=ex.Message;return false;}
    }
    internal static void CheckUnchanged(OrphanRecoveryBinding binding)
    {
        CheckShared(binding.Source);
        string pending=Path.Combine(Path.GetDirectoryName(binding.SessionFile)!,$"{binding.Source.ProcessId}-{binding.Source.CreationFileTime}.pending.json");
        if(File.Exists(pending)||Fingerprint(binding.EvidenceFiles)!=binding.Fingerprint)throw new InvalidDataException("Orphan evidence changed during recovery.");
        string[] matches=FindResolved(Path.GetDirectoryName(binding.SessionFile)!,binding.Source,binding.RequestId);
        if(matches.Length!=1||!string.Equals(matches[0],binding.SessionFile,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Resolved binding changed during recovery.");
    }
    private static string[] FindResolved(string sessions,SourceIdentity source,string requestId)
    {
        var files=Directory.EnumerateFiles(sessions,$"{source.ProcessId}-{source.CreationFileTime}.resolved-*.json")
            .Take(MaximumResolvedRecords+1).ToArray();
        if(files.Length>MaximumResolvedRecords)throw new InvalidDataException("Resolved session search exceeds record bounds.");
        var matches=new List<string>();long total=0;
        foreach(string path in files)
        {
            total=checked(total+new FileInfo(path).Length);
            if(total>MaximumResolvedBytes)throw new InvalidDataException("Resolved session search exceeds byte bounds.");
            using var doc=Document(path,32768);
            if(doc.RootElement.GetProperty("requestId").GetString()==requestId)matches.Add(path);
        }
        return matches.ToArray();
    }
    private static void CheckShared(SourceIdentity source)
    {if(File.Exists(Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",$"{source.ProcessId}-{source.CreationFileTime}.pending.json")))throw new InvalidDataException("Shared native pending remains.");}
    private static string Fingerprint(string[] files)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",files.Select(x=>x+":"+ManualLockReleaseStore.Hash(x))))));
    private static T Read<T>(string path,int max){using var doc=Document(path,max);return doc.RootElement.Deserialize<T>(HubProtocol.Json)??throw new InvalidDataException("Evidence missing.");}
    private static JsonDocument Document(string path,int max)
    {
        var info=new FileInfo(path);if(!info.Exists||info.Length is 0||info.Length>max||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Evidence missing, linked or exceeds bounds.");
        var doc=JsonDocument.Parse(File.ReadAllBytes(path));if(!Unique(doc.RootElement)){doc.Dispose();throw new InvalidDataException("Duplicate JSON evidence properties.");}return doc;
    }
    private static bool Unique(JsonElement value)
    {if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject())if(!names.Add(p.Name)||!Unique(p.Value))return false;}else if(value.ValueKind==JsonValueKind.Array){foreach(var e in value.EnumerateArray())if(!Unique(e))return false;}return true;}
}
