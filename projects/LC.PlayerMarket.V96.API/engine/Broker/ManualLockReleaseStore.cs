using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MemoryHub.Service;
internal sealed record ManualLockRelease(int Schema,string RequestId,string PlanId,SourceIdentity Source,ulong OwnerId,string Direction,
    DateTimeOffset At,string Mode,bool RiskAcknowledged,string NativeSha256,string PendingSha256,string BackupDirectory);
internal static class ManualLockReleaseStore
{
    internal const string Warning="使用者已手動解除應用程式鎖；原派送結果仍未知，重新查詢可能重複。";
    internal static string Key(string request)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
    internal static string PathFor(string root,string request)=>Path.Combine(root,"manual-lock-releases",Key(request)+".json");
    internal static ManualLockRelease[] Read(string root)
    {
        string folder=Path.Combine(root,"manual-lock-releases");if(!Directory.Exists(folder))return [];
        var paths=Directory.GetFiles(folder,"*.json");if(paths.Length>256)throw new InvalidDataException("人工解除紀錄超過界線。");
        var output=new List<ManualLockRelease>();
        foreach(string path in paths)
        {
            if(new FileInfo(path).Length>32768)throw new InvalidDataException("人工解除紀錄大小無效。");
            var r=JsonSerializer.Deserialize<ManualLockRelease>(File.ReadAllBytes(path),HubProtocol.Json)??throw new InvalidDataException("人工解除紀錄缺失。");
            if(r.Schema!=1||r.Mode!="ManualRiskAccepted_OutcomeUnknown"||!r.RiskAcknowledged||r.Source is null||r.At==default
                ||r.RequestId.Length is 0 or >128||r.RequestId.Any(char.IsControl)||r.OwnerId==0||r.Direction is not("ViewerBuy" or "ViewerSell")
                ||!Guid.TryParseExact(r.PlanId,"N",out _)||Path.GetFullPath(path)!=Path.GetFullPath(PathFor(root,r.RequestId)))throw new InvalidDataException("人工解除格式或識別無效。");
            string backup=Path.GetFullPath(r.BackupDirectory),allowed=Path.GetFullPath(Path.Combine(root,"lock-tool-backups"))+Path.DirectorySeparatorChar;
            if(!backup.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("人工解除備份位置無效。");
            string pending=Path.Combine(backup,"original-pending.json"),native=Path.Combine(backup,"native.jsonl");
            if(Hash(pending)!=r.PendingSha256||Hash(native)!=r.NativeSha256)throw new InvalidDataException("人工解除原始證據校驗失敗。");
            using var doc=JsonDocument.Parse(File.ReadAllBytes(pending));var d=doc.RootElement;
            var source=d.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json);
            if(d.GetProperty("requestId").GetString()!=r.RequestId||source!=r.Source||d.GetProperty("targetOwner").GetUInt64()!=r.OwnerId
                ||d.GetProperty("viewerDirection").GetString()!=r.Direction)throw new InvalidDataException("人工解除與原 pending 不符。");
            output.Add(r);
        }
        return output.ToArray();
    }
    internal static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
}
