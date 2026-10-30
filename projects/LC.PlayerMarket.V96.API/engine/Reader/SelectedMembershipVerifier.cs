using System.Buffers.Binary;

namespace MemoryHub.Reader;

/// <summary>Bounded selected-membership closure with fresh reads supplied by the owning graph.</summary>
internal static class SelectedMembershipVerifier
{
    internal static int Verify(Func<ulong,int,byte[]> read,Action<ulong> pointer,
        ulong headerAt,int limit,ulong selectedObject,ulong selectedControl)
    {
        for(int attempt=1;attempt<=2;attempt++)
        {
            // Each attempt reacquires its own header, entries and final selected pair.
            // Failed native reads and changed selected identity never enter the retry path.
            byte[] header=read(headerAt,24);ulong begin=U64(header),end=U64(header,8),capacity=U64(header,16);
            pointer(begin);pointer(end);pointer(capacity);
            Need(begin<=end&&end<=capacity&&(end-begin)%16==0&&(capacity-begin)%16==0&&(end-begin)/16<=(ulong)limit,
                "自身所屬集合無法完整重新確認。");
            int matches=0;ulong selectedSlot=0;
            for(ulong offset=0;offset<end-begin;offset+=4096)
            {
                byte[] entries=read(begin+offset,(int)Math.Min(4096,end-begin-offset));
                for(int i=0;i<entries.Length;i+=16)
                {
                    if(U64(entries,i)!=selectedObject)continue;
                    Need(U64(entries,i+8)==selectedControl,"已選自身／必要服務的控制來源改變。");
                    matches++;selectedSlot=begin+offset+(ulong)i;
                }
            }
            Need(matches==1,"已選自身／必要服務已離開原所屬集合或不再唯一。");
            byte[] pair=read(selectedSlot,16);
            Need(U64(pair)==selectedObject&&U64(pair,8)==selectedControl,"已選成員在結尾重讀時改變。");
            if(read(headerAt,24).AsSpan().SequenceEqual(header))return attempt;
        }
        throw new ProbeFailure("GROUP_SOURCE_CHANGED","自身成員資格查找期間集合邊界持續改變，一次重新查找後仍無法確認。");
    }
    private static void Need(bool condition,string detail)
    {if(!condition)throw new ProbeFailure("GROUP_SOURCE_CHANGED",detail);}
    private static ulong U64(byte[] value,int at=0)=>BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(at));
}
