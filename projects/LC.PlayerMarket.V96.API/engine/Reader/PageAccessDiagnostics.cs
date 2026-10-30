namespace MemoryHub.Reader;

internal sealed partial class ReadSession
{
    internal sealed record PageAccess(DateTimeOffset At,string Stage,string Address,int Bytes,int NativeError,
        uint? State,uint? Protection,uint? Type,string? AllocationBase);
    private readonly List<PageAccess> pageAccess=[];
    private readonly HashSet<(string,ulong,uint?)> pageAccessKeys=[];
    internal IReadOnlyList<PageAccess> PageAccessEvidence=>pageAccess;
    internal long PageAccessObservations {get;private set;}
    internal void NotePageAccess(string stage,ulong address,int count,Native.MemoryRegion? region=null,int nativeError=0)
    {
        PageAccessObservations++;
        if(pageAccess.Count>=64||!pageAccessKeys.Add((stage,address,region?.Protect)))return;
        pageAccess.Add(new(DateTimeOffset.UtcNow,stage,Probe.Hex(address),count,nativeError,
            region?.State,region?.Protect,region?.Type,region is {} r?Probe.Hex(r.AllocationBase):null));
    }
}
