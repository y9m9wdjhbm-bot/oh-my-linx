using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using LocalMiniMap;
namespace LcPlayerMarket;
internal interface ILocalTerrainProvider
{
 string? Error{get;}
 Task<TerrainPatch?> GetAsync(int map,int x,int y,CancellationToken token);
}
/// <summary>The same Nav TerrainCatalog u16le resources, lazy bounded chunks; no game source.</summary>
internal sealed class LocalTerrainProvider(string root):ILocalTerrainProvider
{
 private Dictionary<(int Map,string Chunk),string>? entries;
 private readonly Dictionary<(int,string),ushort[]> cache=[];
 private readonly Queue<(int,string)> order=[];
 private TerrainPatch? patch;
 internal int FileReads{get;private set;}
 internal int LoadedChunks{get;private set;}
 internal int PatchBuilds{get;private set;}
 public string? Error{get;private set;}
 private string manifestHash="";
 public async Task<TerrainPatch?> GetAsync(int map,int x,int y,CancellationToken token)
 {
  if(map<0||x<0||y<0||x>65535||y>65535)return null;
  if(patch is{} saved&&saved.Map==map&&Math.Abs(x-saved.CenterX)<=8&&Math.Abs(y-saved.CenterY)<=8)return saved;
  return await Task.Run(()=>Load(map,x,y,token),token).ConfigureAwait(false);
 }
 private TerrainPatch? Load(int map,int x0,int y0,CancellationToken token)
 {
  Error=null;try
  {
   token.ThrowIfCancellationRequested();
   if(entries is null)
   {
    string path=Path.Combine(root,"terrain_catalog_manifest_v1.json");if(!File.Exists(path)||new FileInfo(path).Length>32*1024*1024){Error="等待地形資源";return null;}
    byte[] raw=File.ReadAllBytes(path);FileReads++;manifestHash=Convert.ToHexString(SHA256.HashData(raw));
    using var doc=JsonDocument.Parse(raw);var parsedEntries=new Dictionary<(int Map,string Chunk),string>();foreach(var r in doc.RootElement.GetProperty("chunks").EnumerateArray())
    {token.ThrowIfCancellationRequested();if(r.GetProperty("success").GetBoolean())parsedEntries.Add((r.GetProperty("mapDirectoryId").GetInt32(),r.GetProperty("chunk").GetString()!.ToLowerInvariant()),r.GetProperty("attributeSHA256").GetString()!);}
    entries=parsedEntries;
   }
   if(!entries.Keys.Any(k=>k.Map==map)){Error="當前地圖沒有地形資源";return null;}
   List<TerrainCell> cells=[];int known=0;const int radius=64;
   for(int y=y0-radius;y<=y0+radius;y++){token.ThrowIfCancellationRequested();for(int x=x0-radius;x<=x0+radius;x++)
    {int?[] q=[Attr(map,x,y,0),Attr(map,x,y,1),Attr(map,x-1,y,1),Attr(map,x,y+1,0)];bool present=q.All(a=>a.HasValue);if(present)known++;cells.Add(new(x,y,present,q.All(a=>a.HasValue&&(a.Value&0x4001)!=0)));}}
   if(known==0||!cells.Single(c=>c.X==x0&&c.Y==y0).Known){Error="目前周邊地形尚未取得";return null;}
   patch=new(map,x0,y0,radius,manifestHash,cells.ToArray());PatchBuilds++;return patch;
  }catch(Exception e)when(e is IOException or JsonException or InvalidDataException or KeyNotFoundException or ArgumentException){Error="周邊地形驗證失敗："+e.Message;return null;}
 }
 private int? Attr(int map,int x,int y,int which)
 {
  if(x<0||y<0||x>65535||y>65535)return null;string key=$"{x/64+0x7e00:x4}{y/64+0x7e00:x4}";var pair=(map,key);if(entries is null||!entries.TryGetValue(pair,out var expected))return null;
  if(!cache.TryGetValue(pair,out var data))
  {
   string file=Path.Combine(root,"terrain_catalog_v1",map.ToString(System.Globalization.CultureInfo.InvariantCulture),key+".attributes.u16le");
   byte[] bytes=File.ReadAllBytes(file);FileReads++;if(bytes.Length!=16384||!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("TERRAIN_INTEGRITY");
   data=new ushort[8192];for(int i=0;i<data.Length;i++)data[i]=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i*2,2));
   if(cache.Count>=24)cache.Remove(order.Dequeue());cache.Add(pair,data);order.Enqueue(pair);LoadedChunks++;
  }
  return data[((y%64)*64+x%64)*2+which];
 }
}
