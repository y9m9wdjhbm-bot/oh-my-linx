using System.Buffers.Binary;
using System.Text;

namespace MemoryHub.Reader;

/// <summary>Parses a tracked Description string and requires the owner's supplied closure before returning.</summary>
internal static class ActorNameDecoder
{
    internal sealed record Result(string? Name,string State,string Detail);
    private static readonly UTF8Encoding Utf8=new(false,true);

    internal static Result Read(ulong descriptorAt,Func<ulong,int,byte[]> track,Action<ulong> pointer,
        Action<(ulong At,int Size)[]> close,string identity)
    {
        byte[] descriptor=track(descriptorAt,32);ulong size=U64(descriptor,16),capacity=U64(descriptor,24);
        Need(size<=96&&capacity>=size&&capacity<=4096&&(capacity==15&&size<=15||capacity>=16),
            "ACTOR_NAME_FORMAT",$"角色名稱描述欄格式不符；{identity};Length={size};Capacity={capacity};Descriptor={Convert.ToHexString(descriptor)}");
        ulong buffer=capacity==15?descriptorAt:U64(descriptor);if(capacity!=15)pointer(buffer);
        byte[] bytes=track(buffer,(int)size+1);
        Need(bytes[^1]==0&&!bytes.AsSpan(0,bytes.Length-1).Contains((byte)0),"ACTOR_NAME_NUL","角色名稱非完整單一字串");
        string value;
        try{value=Utf8.GetString(bytes,0,bytes.Length-1);}
        catch(DecoderFallbackException){throw new ProbeFailure("ACTOR_NAME_UTF8","角色名稱不是完整UTF8");}
        Need(size==0||!string.IsNullOrWhiteSpace(value)&&!value.Any(char.IsControl),"ACTOR_NAME_TEXT","角色名稱為空白或含控制字元");
        close([(descriptorAt,32),(buffer,(int)size+1)]);
        // This classifies only the verified Description field, never the actor's existence or display name.
        if(size==0)return new(null,"Absent","此 Description 名稱欄為空；不代表物件沒有名稱或不存在");
        return new(value,"Observed","Description+0x40 型別與AB閉合候選；名稱語義待實際比對");
    }
    private static ulong U64(byte[] value,int at=0)=>BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(at));
    private static void Need(bool condition,string code,string detail){if(!condition)throw new ProbeFailure(code,detail);}
}
