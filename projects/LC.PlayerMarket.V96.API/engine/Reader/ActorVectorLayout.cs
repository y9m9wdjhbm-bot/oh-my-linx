using System.Buffers.Binary;

namespace MemoryHub.Reader;

internal readonly record struct ActorVectorLayout(ulong Begin,ulong End,ulong CapacityEnd,int ActiveBytes)
{
    internal static ActorVectorLayout Inspect(ulong at,byte[] header,int maximumSlots)
    {
        if(header.Length!=24)throw new ProbeFailure("ACTOR_VECTOR_HEADER","角色集合標頭未完整取得");
        ulong begin=BinaryPrimitives.ReadUInt64LittleEndian(header),end=BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)),cap=BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
        if(begin==0&&end==0&&cap==0)return new(0,0,0,0);
        if(!Probe.Pointer(begin)||!Probe.Pointer(end)||!Probe.Pointer(cap))throw new ProbeFailure("ACTOR_POINTER","角色來源指標不合法");
        bool validLayout=begin<=end&&end<=cap&&(end-begin)%16==0&&(cap-begin)%16==0;
        // Capacity is an allocation watermark, not readable/occupied payload.
        // Validate its geometry but read and bound only the active [begin,end) span.
        if(!validLayout||(end-begin)/16>(ulong)maximumSlots)
            throw new ProbeFailure("ACTOR_VECTOR_LIMIT",$"角色／元件集合超界；未截斷交付；Reason={(validLayout?"ActiveSlotLimit":"InvalidLayout")}，Header=0x{at:X}，Begin=0x{begin:X}，End=0x{end:X}，CapacityEnd=0x{cap:X}，Count={(end>=begin?(end-begin)/16:ulong.MaxValue)}，Capacity={(cap>=begin?(cap-begin)/16:ulong.MaxValue)}，MaxCount={maximumSlots}；預留容量不作為讀取數量");
        return new(begin,end,cap,checked((int)(end-begin)));
    }
}

internal static class ActorCollectionLimits
{
    // Raw slots also contain nulls, unclassified map objects and repeated entries.
    // Typed collections use the same bounded actor contract as all consumers.
    internal const int MaximumSlots=1024;
    internal const int MaximumObjects=1024;
    internal const int MaximumSelectedActors=ActorCollectionContract.MaximumActors;
    internal static void RequireObjectCount(int count)
    {
        if(count>MaximumObjects)throw new ProbeFailure("ACTOR_COLLECTION_LIMIT",$"角色原始全集合超界；Count={count}，Limit={MaximumObjects}；未截斷交付");
    }
    internal static void RequireSelectedCount(int count)
    {
        if(count>MaximumSelectedActors)throw new ProbeFailure("ACTOR_TYPED_COLLECTION_LIMIT",$"已分類角色超出接收協定上限；Count={count}，Limit={MaximumSelectedActors}；未截斷交付");
    }
}
