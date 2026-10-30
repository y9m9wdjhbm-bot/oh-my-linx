using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemoryHub.Reader;

/// <summary>A durable negative-only ledger. It never stores reusable successful evidence.</summary>
public sealed class ItemStateContradictionStore
{
    private readonly string root;
    private static readonly ConcurrentDictionary<string,byte> denied=new(StringComparer.Ordinal);
    public ItemStateContradictionStore(string? directory=null)
    {
        root=Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LCPlayerMarket","item-state-contradictions-v1"));
    }
    public static string BindingKey(SourceIdentity source)
    {
        // Scene/self/session changes cannot clear a contradiction in the same loaded process/module.
        string value=JsonSerializer.Serialize(new {source.ProcessId,source.CreationFileTime,
            Path=source.ImagePath.ToUpperInvariant(),Base=source.ImageBase.ToUpperInvariant(),source.PeTimestamp,source.ImageSize,
            Sha=source.DiskSha256.ToUpperInvariant()});
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    private string PathFor(SourceIdentity source)=>Path.Combine(root,BindingKey(source)+".rejected.json");
    public bool IsRejected(SourceIdentity source)
    {
        string path=PathFor(source);if(denied.ContainsKey(path))return true;
        try
        {
            Directory.CreateDirectory(root);
            // Open() distinguishes a missing record from access/IO errors; malformed and partial records also deny.
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return true;
        }
        catch(FileNotFoundException){return false;}
        catch(Exception error){throw new IOException("ITEM_STATE_NEGATIVE_LEDGER_UNAVAILABLE",error);}
    }
    public void RecordClosedContradiction(SourceIdentity source,string reason)
    {
        string path=PathFor(source);denied.TryAdd(path,0);Directory.CreateDirectory(root);
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(new {Schema="ItemStateStableContradictionV1",AtUtc=DateTimeOffset.UtcNow,
            Source=source,Reason=reason,CertificateSha256=PersonalShopBuildCertificate.CertificateSha256,NegativeOnly=true});
        FileStream file;
        try {file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);}
        catch(IOException) when(File.Exists(path)){return;}
        // Write/flush failures must propagate to READ_FAILED; they are never successful publication.
        using(file){file.Write(body);file.WriteByte(10);file.Flush(true);}
    }
}
