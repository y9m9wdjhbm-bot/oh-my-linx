namespace MemoryHub.Reader;

internal sealed partial class ReadSession
{
    private RoundMetadataClosureCacheV1? roundMetadata;
    private string? roundMetadataProfileIdentity;
    internal object? RoundMetadataMetrics=>roundMetadata?.Metrics;
    internal void EnableRoundMetadata()
    {
        Boundary();if(roundMetadata is not null)throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次metadata去重不可重新綁定。");
        roundMetadataProfileIdentity=Profile.CacheIdentity;roundMetadata=new(Image,ImageSize);
    }
    private byte[] FreshRoundMetadata(ulong address,int count)
    {
        Boundary();if(roundMetadataProfileIdentity!=Profile.CacheIdentity||address<Image||address-Image>uint.MaxValue||!InImage((uint)(address-Image),count))
            throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次metadata的profile／主映像綁定已改變。");
        uint rva=(uint)(address-Image);
        if(!Query(rva,out var page)||page.State!=0x1000||page.Type!=0x1000000||page.AllocationBase!=Image)
            throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次metadata不再屬於目前LC的同一MEM_IMAGE。");
        byte[] fresh=Required(rva,count);
        if(metadataTransaction?.Stage(new ModuleMetadataCache.EntryKey(address,count),fresh)==false)
            throw new ProbeFailure("GROUP_SOURCE_CHANGED","當次metadata與目前交易的首次讀值不一致。");
        return fresh;
    }
    private byte[] ReadRoundMetadata(uint actualRva,int count)
        =>roundMetadata!.Read(Image+actualRva,count,FreshRoundMetadata);
    internal void CloseRoundMetadata(){roundMetadata?.Close(FreshRoundMetadata,Boundary);}
}
