using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
namespace MemoryHub;

public static class HubWire
{
    public static async Task WriteAsync(Stream stream,HubSnapshot snapshot,CancellationToken token)
    {
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(snapshot,HubProtocol.Json);
        if(body.Length>HubProtocol.MaxMessageBytes)throw new InvalidDataException("快照超過 1 MiB 上限");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,body.Length);
        await stream.WriteAsync(header,token);await stream.WriteAsync(body,token);await stream.FlushAsync(token);
    }
    public static async Task<HubSnapshot> ReadAsync(Stream stream,CancellationToken token)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,token);
        int size=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(size is <=0 or >HubProtocol.MaxMessageBytes)throw new InvalidDataException("不合法的快照長度");
        byte[] body=new byte[size];await stream.ReadExactlyAsync(body,token);
        var snapshot=JsonSerializer.Deserialize<HubSnapshot>(body,HubProtocol.Json)??throw new InvalidDataException("空快照");
        if(snapshot.SchemaVersion!=HubProtocol.Schema)throw new InvalidDataException("不支援的資料協定版本");
        if(snapshot.Fields is null||snapshot.Fields.Count>128||snapshot.Fields.Values.Any(f=>f is null))throw new InvalidDataException("欄位格式錯誤");
        return snapshot.AtTime(DateTimeOffset.UtcNow);
    }
}
public sealed class HubClient(string? pipeName=null)
{
    public string PipeName {get;}=pipeName??HubProtocol.PipeName;
    public async IAsyncEnumerable<HubSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken token=default)
    {
        HubSnapshot? previous=null;
        while(!token.IsCancellationRequested)
        {
            using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.In,PipeOptions.Asynchronous);
            string? error=null;
            try {await pipe.ConnectAsync(1200,token);} catch(Exception ex) when(ex is IOException or TimeoutException or UnauthorizedAccessException) {error=ex.Message;}
            if(error is null)
            {
                while(!token.IsCancellationRequested)
                {
                    HubSnapshot? snapshot=null;
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(3000);
                    try {snapshot=await HubWire.ReadAsync(pipe,deadline.Token);}
                    catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
                    { if(token.IsCancellationRequested)yield break;error=ex.Message; }
                    if(snapshot is null)break;
                    previous=snapshot;yield return snapshot;
                }
            }
            if(token.IsCancellationRequested)yield break;
            var cleared=FieldCatalog.All.ToDictionary(f=>f.Key,f=>previous?.Fields.GetValueOrDefault(f.Key)?.State=="Disabled"
                ?FieldReading.Disabled():FieldReading.Unknown("服務未連線，沒有有效資料"));
            yield return new HubSnapshot{State="Disconnected",ReadConfigurationVersion=previous?.ReadConfigurationVersion??0,PublishedAt=DateTimeOffset.UtcNow,Detail="讀取服務未連線，正在重連："+error,Fields=cleared};
            await Task.Delay(500,token);
        }
    }
}
