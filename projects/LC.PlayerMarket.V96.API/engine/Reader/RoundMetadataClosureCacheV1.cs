namespace MemoryHub.Reader;

/// <summary>One ReadSession only. First complete module-metadata A and mandatory fresh final C.
/// No heap, player values, cross-read publication, or timestamp renewal facility.</summary>
internal sealed class RoundMetadataClosureCacheV1(ulong image,int imageSize,int maximumEntries=4096,int maximumBytes=512*1024)
{
    private readonly Dictionary<(ulong Address,int Count),byte[]> entries=[];
    private bool closed,invalid;
    private int bytes;
    internal long Calls{get;private set;}
    internal long Hits{get;private set;}
    internal long SavedBytes{get;private set;}
    internal long FirstReads{get;private set;}
    internal long FinalReads{get;private set;}
    internal int EntryCount=>entries.Count;
    internal int StoredBytes=>bytes;
    private void Check(ulong address,int count)
    {
        if(closed||invalid)throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次模組 metadata 閉合後或失效後不可再次取值。");
        if(count is <=0 or >4096||address<image||count>imageSize||address-image>(ulong)(imageSize-count))
            throw new ProbeFailure("META_RANGE","當次 metadata 去重只接受有界主映像範圍；不接受 heap 或角色值。");
    }
    internal byte[] Read(ulong address,int count,Func<ulong,int,byte[]> readFresh)
    {
        Check(address,count);Calls++;
        foreach(var p in entries)
            if(address>=p.Key.Address&&address-p.Key.Address<=(ulong)p.Key.Count&&count<=p.Key.Count-(int)(address-p.Key.Address))
            {Hits++;SavedBytes+=count;return p.Value.AsSpan((int)(address-p.Key.Address),count).ToArray();}
        if(entries.Count>=maximumEntries||count>maximumBytes-bytes)throw new ProbeFailure("READ_BYTE_LIMIT","當次模組 metadata 證據超過有界數量／容量。");
        byte[] fresh=readFresh(address,count);FirstReads++;
        if(fresh.Length!=count){invalid=true;throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次 metadata 首次讀取不完整。");}
        foreach(var p in entries)
        {
            ulong start=Math.Max(address,p.Key.Address),end=Math.Min(address+(uint)count,p.Key.Address+(uint)p.Key.Count);
            if(start<end&&!fresh.AsSpan((int)(start-address),(int)(end-start)).SequenceEqual(p.Value.AsSpan((int)(start-p.Key.Address),(int)(end-start))))
            {invalid=true;throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次 metadata 重疊區間的真實讀值已改變。");}
        }
        entries.Add((address,count),fresh.ToArray());bytes+=count;return fresh;
    }
    internal void Close(Func<ulong,int,byte[]> readFresh,Action check)
    {
        if(closed||invalid)throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次 metadata 證據已閉合或失效。");
        foreach(var p in entries.OrderBy(p=>p.Key.Address).ThenBy(p=>p.Key.Count))
        {
            check();byte[] fresh=readFresh(p.Key.Address,p.Key.Count);FinalReads++;
            if(!fresh.AsSpan().SequenceEqual(p.Value)){invalid=true;throw new ProbeFailure("GROUP_SOURCE_CHANGED","最終 C 重新讀取發現當次模組 metadata 已變更；整輪捨棄。");}
        }
        check();closed=true;
    }
    internal object Metrics=>new{Mode="CurrentReadOnly_FirstFreshA_UniqueModuleRanges_FinalFreshC",Calls,Hits,SavedReadCalls=Hits,SavedBytes,FirstReads,FinalReads,Entries=entries.Count,StoredBytes=bytes,Closed=closed,Invalid=invalid,HeapReadsCached=0,ValueReadsCached=0,CrossReadReuse=false};
}
