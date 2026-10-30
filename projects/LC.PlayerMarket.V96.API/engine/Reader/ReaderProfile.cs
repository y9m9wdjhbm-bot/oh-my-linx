using System.Collections.ObjectModel;
using System.Text.Json;

namespace MemoryHub.Reader;

public sealed record LcBuildIdentity(string Sha256,long FileLength,uint PeOffset,uint PeTimestamp,uint ImageSize,ushort Machine);
internal sealed record RelocationTypeBinding(string Name,uint Descriptor,uint Col,uint Vtable);
internal sealed class ReaderProfile
{
    internal string Id {get;}
    internal string CacheIdentity {get;}
    internal LcBuildIdentity Identity {get;}
    internal string Sha256=>Identity.Sha256;
    internal IReadOnlyDictionary<uint,uint> Rvas {get;}
    internal IReadOnlyCollection<string> AllowedFields {get;}
    internal IReadOnlyList<RelocationAnchorEvidence> RelocationEvidence {get;}
    internal IReadOnlyDictionary<string,RelocationTypeBinding> TypeBindings {get;}
    internal IReadOnlyDictionary<string,uint> FieldOffsets {get;}
    internal ReaderProfile(string id,LcBuildIdentity identity,IReadOnlyDictionary<uint,uint> rvas,IReadOnlyCollection<string> allowedFields,IReadOnlyList<RelocationAnchorEvidence>? relocationEvidence=null,
        IReadOnlyDictionary<string,RelocationTypeBinding>? typeBindings=null,IReadOnlyDictionary<string,uint>? fieldOffsets=null)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>96||identity.Sha256.Length!=64||!identity.Sha256.All(Uri.IsHexDigit)||identity.Machine!=0x8664||
            identity.ImageSize is <4096 or >128*1024*1024||identity.PeOffset>identity.ImageSize-264||identity.FileLength<0||rvas.Count>512||
            rvas.Any(p=>p.Key<4096||p.Value<4096||p.Value>=identity.ImageSize)||allowedFields.Any(k=>!FieldCatalog.Contains(k))||allowedFields.Distinct().Count()!=allowedFields.Count)
            throw new InvalidDataException("版本設定內容無效或超界");
        Id=id;Identity=identity;Rvas=new ReadOnlyDictionary<uint,uint>(rvas.ToDictionary());AllowedFields=Array.AsReadOnly(allowedFields.Order(StringComparer.Ordinal).ToArray());
        RelocationEvidence=relocationEvidence??Array.Empty<RelocationAnchorEvidence>();
        var typed=typeBindings?.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal)??new(StringComparer.Ordinal);
        if(typed.Count>128||typed.Any(p=>p.Key!=p.Value.Name||!ProfileSymbols.Types.Any(t=>t.Name==p.Key)||
            new[]{p.Value.Descriptor,p.Value.Col,p.Value.Vtable}.Any(r=>r<4096||r>=identity.ImageSize)))
            throw new InvalidDataException("版本型別綁定內容無效或超界");
        TypeBindings=new ReadOnlyDictionary<string,RelocationTypeBinding>(typed);
        var offsets=fieldOffsets?.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal)??new(StringComparer.Ordinal);
        if(offsets.Count>16||offsets.Any(p=>p.Value>4096||p.Value%4!=0||p.Key is not("offset:HudSlotDataSubsystem:slotsHead" or "offset:HudSlotDataSubsystem:slotsCount" or "offset:HudSlotDataSubsystem:page" or "offset:HudSlotDataSubsystem:focus" or "offset:Actor:id" or "offset:Actor:components" or "offset:ActorComponentBase:owner" or "offset:ActionComponent:directAction")))
            throw new InvalidDataException("欄位偏移超出此讀取器支援範圍。");
        foreach(var witness in TemplateReaderProfileAdapter.ShopOffsetWitnessesFor(identity))
            if(offsets.TryGetValue(witness.Key,out uint value)&&value!=witness.Value)throw new InvalidDataException("商店布局偏移與此讀取器固定契約不符："+witness.Key);
        if(offsets.TryGetValue("offset:HudSlotDataSubsystem:slotsCount",out uint count)&&
            (!offsets.TryGetValue("offset:HudSlotDataSubsystem:slotsHead",out uint head)||count!=head+8))
            throw new InvalidDataException("快捷格數量偏移必須緊鄰同一 RB-tree header 的 head+8。");
        if(offsets.TryGetValue("offset:HudSlotDataSubsystem:focus",out uint focus)&&offsets.TryGetValue("offset:HudSlotDataSubsystem:page",out uint page)&&focus!=page+4)
            throw new InvalidDataException("快捷焦點偏移必須符合本版 page+4 契約。");
        FieldOffsets=new ReadOnlyDictionary<string,uint>(offsets);
        CacheIdentity=id+":"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{identity,Map=rvas.OrderBy(p=>p.Key).ToArray(),Fields=AllowedFields,Types=typed.OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray(),Offsets=offsets.OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray()},HubProtocol.Json)));
    }
    internal uint FieldOffset(string symbol,uint legacy)
    {
        if(FieldOffsets.TryGetValue(symbol,out uint value))return value;
        if(Id.StartsWith("template-",StringComparison.Ordinal)||Sha256 is not(Probe.NewSha256 or "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF"))
            throw new ProbeFailure("PROFILE_FIELD_OFFSET_MISSING","此版本必要欄位偏移仍未由模板定位："+symbol);
        return legacy;
    }
    internal uint Resolve(uint symbol)=>Rvas.TryGetValue(symbol,out uint result)?result:throw new ProbeFailure("PROFILE_SYMBOL_MISSING",$"本版尚未定位必要來源 0x{symbol:X}，該欄不放行。");
    internal bool Allows(string key)=>AllowedFields.Contains(key);
    internal static readonly string[] SupportedKeys=FieldCatalog.All.Select(f=>f.Key).ToArray();
    internal static ReaderProfile Builtin {get;}=CreateBuiltin();
    private static ReaderProfile CreateBuiltin()
    {
            var symbols=ProfileSymbols.Types.SelectMany(t=>new[]{t.Descriptor,t.Col,t.Vtable}).Where(r=>r!=0).Distinct().ToDictionary(r=>r,r=>r);
            foreach(uint rva in new uint[]{0x17E00D8,0x1642B60,0x1251A10})symbols[rva]=rva;
            var types=ProfileSymbols.Types.Where(t=>t.Descriptor!=0&&t.Col!=0&&t.Vtable!=0)
                .ToDictionary(t=>t.Name,t=>new RelocationTypeBinding(t.Name,t.Descriptor,t.Col,t.Vtable),StringComparer.Ordinal);
            return new("builtin-20260908",new(Probe.NewSha256,0,0x158,Probe.PeStamp,Probe.ImageSize,0x8664),symbols,SupportedKeys,typeBindings:types);
    }
}

internal sealed class ProfileStore(string directory)
{
    private sealed record Saved(int SchemaVersion,string Layout,string Id,LcBuildIdentity Identity,Dictionary<uint,uint> Rvas,string[] ConfirmedFields,DateTimeOffset SavedAt,
        Dictionary<string,RelocationTypeBinding>? TypeBindings=null,Dictionary<string,uint>? FieldOffsets=null);
    private sealed record Active(int SchemaVersion,string FileName,string Sha256);
    internal ReaderProfile? Load()
    {
        var imported=TemplateProfileImport.LoadLatest(directory);
        if(imported is not null)return imported;
        if(File.Exists(Path.Combine(directory,TemplateReaderProfileAdapter.PointerFile)))
            return TemplateReaderProfileAdapter.Load(directory);
        string activePath=Path.Combine(directory,"active.json");if(!File.Exists(activePath))return null;
        var pointer=Read<Active>(activePath,4096);
        if(pointer.SchemaVersion!=1||Path.GetFileName(pointer.FileName)!=pointer.FileName||!pointer.FileName.StartsWith("profile-",StringComparison.Ordinal)||!pointer.FileName.EndsWith(".json",StringComparison.Ordinal))throw new InvalidDataException("作用中版本設定索引無效");
        string profilePath=Path.Combine(directory,pointer.FileName);var bytes=ReadBounded(profilePath,128*1024);
        if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))!=pointer.Sha256)throw new InvalidDataException("版本設定雜湊不符");
        var saved=JsonSerializer.Deserialize<Saved>(bytes,HubProtocol.Json)??throw new InvalidDataException("版本設定為空");
        if(saved.SchemaVersion!=1||saved.Layout!="LC-20260908-structure-v1"||saved.ConfirmedFields.Length==0||saved.ConfirmedFields.Any(k=>!ReaderProfile.SupportedKeys.Contains(k)))throw new InvalidDataException("版本設定規則或確認欄位無效");
        var profile=new ReaderProfile(saved.Id,saved.Identity,saved.Rvas,saved.ConfirmedFields,typeBindings:saved.TypeBindings,fieldOffsets:saved.FieldOffsets);
        _=profile.Resolve(0x17E00D8);_=profile.Resolve(0x1642B60);return profile;
    }
    internal string Save(ReaderProfile profile,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();Directory.CreateDirectory(directory);
        string name="profile-"+profile.Sha256[..12]+"-"+Guid.NewGuid().ToString("N")+".json",path=Path.Combine(directory,name);
        var saved=new Saved(1,"LC-20260908-structure-v1",profile.Id,profile.Identity,profile.Rvas.ToDictionary(),profile.AllowedFields.ToArray(),DateTimeOffset.UtcNow,profile.TypeBindings.ToDictionary(),profile.FieldOffsets.ToDictionary());
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(saved,HubProtocol.Json);
        WriteNew(path,bytes);token.ThrowIfCancellationRequested();
        byte[] index=JsonSerializer.SerializeToUtf8Bytes(new Active(1,name,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))),HubProtocol.Json);
        string active=Path.Combine(directory,"active.json"),temporary=Path.Combine(directory,"active-"+Guid.NewGuid().ToString("N")+".tmp");
        WriteNew(temporary,index);token.ThrowIfCancellationRequested();
        if(File.Exists(active))File.Copy(active,Path.Combine(directory,"previous-active-"+Guid.NewGuid().ToString("N")+".json"),false);
        token.ThrowIfCancellationRequested();
        File.Move(temporary,active,true);return path;
    }
    private static void WriteNew(string path,byte[] bytes){using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);file.Write(bytes);file.Flush(true);}
    private static byte[] ReadBounded(string path,int limit)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <1||file.Length>limit)throw new InvalidDataException("版本設定為空或超界");
        byte[] bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);return bytes;
    }
    private static T Read<T>(string path,int limit)=>JsonSerializer.Deserialize<T>(ReadBounded(path,limit),HubProtocol.Json)??throw new InvalidDataException("版本設定為空");
}
