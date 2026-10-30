using System.Buffers.Binary;

namespace MemoryHub.Reader;

internal sealed record CoordinateEncodingSource(int ProcessId,long CreationFileTime,ulong Image,string ProfileIdentity,ulong SelfId,ulong Actor,ulong Transform)
{
    internal static CoordinateEncodingSource Current(ReadSession memory,ulong selfId,ulong actor,ulong transform)
        =>new(memory.ProcessId,memory.CreationFileTime,memory.Image,memory.Profile.CacheIdentity,selfId,actor,transform);
}

/// <summary>Evidence from two typed Coordinate40 values; never a relocated code or constant address claim.</summary>
internal sealed class CoordinateEncodingEvidence
{
    private readonly CoordinateEncodingSource source;
    private readonly byte[] original;
    private CoordinateEncodingEvidence(CoordinateEncodingSource source,byte[] raw){this.source=source;original=raw.ToArray();}
    internal static CoordinateEncodingEvidence Capture(CoordinateEncodingSource source,ReadOnlySpan<byte> raw)
    {
        ValidateSource(source);Validate(raw);return new(source,raw.ToArray());
    }
    internal void Verify(CoordinateEncodingSource current,ReadOnlySpan<byte> raw)
    {
        ValidateSource(current);
        if(current!=source)throw new ProbeFailure("GROUP_SOURCE_CHANGED","座標編碼來源已改變，不能跨程序、版型或角色合併比例證據。");
        Validate(raw);
        if(!raw.SequenceEqual(original))throw new ProbeFailure("COORDINATE_ENCODING_CHANGED","同來源 current／next Coordinate40 在閉合前改變，捨棄樣本。");
    }
    private static void ValidateSource(CoordinateEncodingSource value)
    {
        if(value.ProcessId<=0||value.CreationFileTime<=0||!Probe.Pointer(value.Image)||string.IsNullOrWhiteSpace(value.ProfileIdentity)||
            value.SelfId is 0 or ulong.MaxValue||!Probe.Pointer(value.Actor)||!Probe.Pointer(value.Transform))
            throw new ProbeFailure("COORDINATE_ENCODING_SOURCE","座標編碼缺少有效程序、版型或 typed owner 身分。");
    }
    private static void Validate(ReadOnlySpan<byte> raw)
    {
        if(raw.Length!=80)throw new ProbeFailure("COORDINATE_ENCODING_LENGTH","必須完整讀取 current 與 next 各 40 bytes。");
        for(int start=0;start<80;start+=40)
        {
            var b=raw.Slice(start,40);
            int x=BinaryPrimitives.ReadInt32LittleEndian(b[8..]),y=BinaryPrimitives.ReadInt32LittleEndian(b[12..]);
            if(x is <0 or >65535||y is <0 or >65535)throw new ProbeFailure("COORDINATE_ENCODING_RANGE","完整座標 XY 超出候選編碼範圍。");
            float px=BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b[24..]));
            float left=BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b[32..]));
            float horizontal=px-left;
            if(!float.IsFinite(horizontal)||horizontal!=48f)throw new ProbeFailure("COORDINATE_ENCODING_SCALE","同來源像素錨點差值不符已知 48 單位候選；沒有沿用舊常數 RVA。");
            float py=(x-y)*(horizontal/2f);
            int[] expected=[x/64+0x7E00,y/64+0x7E00,x,y,x*2-0x8000,y,
                BitConverter.SingleToInt32Bits((x+y-65535)*horizontal),BitConverter.SingleToInt32Bits(py),
                BitConverter.SingleToInt32Bits((x+y-65535)*horizontal-horizontal),BitConverter.SingleToInt32Bits(py+0f)];
            for(int index=0;index<10;index++)
                if(BinaryPrimitives.ReadInt32LittleEndian(b[(index*4)..])!=expected[index])
                    throw new ProbeFailure("COORDINATE_ENCODING_RELATION","Coordinate40 的 chunk、XY、UV 與像素候選關係不一致。");
        }
    }
}
