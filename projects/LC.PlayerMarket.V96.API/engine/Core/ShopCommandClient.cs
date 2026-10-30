using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
namespace MemoryHub;

public static class ShopCommandWire
{
    public static async Task WriteAsync<T>(Stream stream,T message,CancellationToken token)
    {
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(message,HubProtocol.Json);
        if(body.Length is <=0 or >HubProtocol.MaxMessageBytes)throw new InvalidDataException("商店訊息超過 1 MiB");
        byte[] size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,body.Length);
        await stream.WriteAsync(size,token);await stream.WriteAsync(body,token);await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken token)
    {
        byte[] size=new byte[4];await stream.ReadExactlyAsync(size,token);
        int length=BinaryPrimitives.ReadInt32LittleEndian(size);
        if(length is <=0 or >HubProtocol.MaxMessageBytes)throw new InvalidDataException("商店訊息長度無效");
        byte[] body=new byte[length];await stream.ReadExactlyAsync(body,token);
        return JsonSerializer.Deserialize<T>(body,HubProtocol.Json)??throw new InvalidDataException("商店訊息為空");
    }
}

/// <summary>One send per caller request. A timeout never resubmits an action.</summary>
public sealed class ShopCommandClient(string? pipeName=null)
{
    public async Task<ShopCommandResponse> SendAsync(ShopCommandRequest request,CancellationToken token=default)
    {
        using var pipe=new NamedPipeClientStream(".",pipeName??ShopCommands.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(20000);
        await pipe.ConnectAsync(2000,deadline.Token);
        await ShopCommandWire.WriteAsync(pipe,request,deadline.Token);
        var response=await ShopCommandWire.ReadAsync<ShopCommandResponse>(pipe,deadline.Token);
        if(response.Schema!=ShopCommands.Schema||response.RequestId!=request.RequestId||response.Listings is null||response.Scan is null)
            throw new InvalidDataException("商店回覆版本或請求識別不符；動作未自動重送");
        return response;
    }
}
