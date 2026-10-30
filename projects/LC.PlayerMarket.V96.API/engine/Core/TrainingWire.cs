using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
namespace MemoryHub.TrainingProtocol;

// A separate, local read-only contract for a source-bound execution consumer.
// No operation accepts addresses, code, input, or a game action request.
public static class TrainingWire
{
    public const int Schema=1,MaximumBytes=1024*1024;
    public const string ServiceVersion="32.0.0";
    // Training consumers validate an exact service version. Keep V26's endpoint
    // free for its published consumer and its independent background service.
    public static string PipeName=>"LC.MemoryHub.v32."+WindowsIdentity.GetCurrent().User!.Value.Replace('-','_')+".training-read";
    public static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    public static async Task WriteAsync<T>(Stream stream,T value,CancellationToken token)
    {
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(value,Json);
        if(body.Length is <=0 or >MaximumBytes)throw new InvalidDataException("TRAINING_MESSAGE_SIZE");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,body.Length);
        await stream.WriteAsync(header,token).ConfigureAwait(false);await stream.WriteAsync(body,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken token)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,token).ConfigureAwait(false);
        int size=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(size is <=0 or >MaximumBytes)throw new InvalidDataException("TRAINING_MESSAGE_SIZE");
        byte[] body=new byte[size];await stream.ReadExactlyAsync(body,token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body,Json)??throw new InvalidDataException("TRAINING_EMPTY_MESSAGE");
    }
    public static async Task<TrainingReply> CallAsync(TrainingRequest request,CancellationToken token,string? pipeName=null)
    {
        using var pipe=new NamedPipeClientStream(".",pipeName??PipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(700,token).ConfigureAwait(false);await WriteAsync(pipe,request,token).ConfigureAwait(false);
        var reply=await ReadAsync<TrainingReply>(pipe,token).ConfigureAwait(false);
        if(reply.Schema!=Schema||reply.ServiceVersion!=ServiceVersion||reply.RequestId!=request.RequestId||string.IsNullOrWhiteSpace(reply.Instance))
            throw new InvalidDataException("TRAINING_PROTOCOL_IDENTITY");
        return reply;
    }
}
public sealed record TrainingRequest
{
    public int Schema{get;init;}=TrainingWire.Schema;
    public string RequestId{get;init;}=Guid.NewGuid().ToString("N");
    public string Operation{get;init;}="";
    public string? Session{get;init;}
    public ulong? TargetId{get;init;}
    public bool Names{get;init;}
    public bool Movement{get;init;}
    public int BudgetMilliseconds{get;init;}=600;
    public string? WorldToken{get;init;}
    public int X{get;init;}public int Y{get;init;}public int ToX{get;init;}public int ToY{get;init;}
}
public sealed record TrainingBinding(int Pid,uint Tid,long Creation,ulong Image,ulong Hwnd,string Path,string Sha256,ulong ImageSize);
public sealed record TrainingReply
{
    public int Schema{get;init;}=TrainingWire.Schema;
    public string ServiceVersion{get;init;}=TrainingWire.ServiceVersion;
    public string RequestId{get;init;}="";
    public string Instance{get;init;}="";
    public string? Session{get;init;}
    public string State{get;init;}="Unknown";
    public string? Error{get;init;}
    public string? Detail{get;init;}
    public DateTimeOffset SampledAt{get;init;}
    public long Reads{get;init;}public long Bytes{get;init;}
    public JsonElement? Data{get;init;}
}
