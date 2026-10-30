using System.Runtime.InteropServices;

namespace MemoryHub.Reader;

internal sealed partial class ReadSession
{
    internal bool Executable(ulong address)
    {
        Boundary();if(address<Image||address-Image>=(ulong)ImageSize)return false;

        var key=new ModuleMetadataCache.EntryKey(address,1,true);
        if(metadataTransaction?.TryGet(key,out var cached)==true){Boundary();return cached[0]==1;}
        if(!Query((uint)(address-Image),out var region))return false;
        bool executable=region.State==0x1000&&(region.Protect&0x101)==0&&(region.Protect&0xFF) is 0x10 or 0x20 or 0x40 or 0x80;
        // A non-executable first slot can legitimately precede an executable second slot.
        // Negative candidates are never cached; the caller validates the complete type rule.
        if(!executable){NotePageAccess("ExecutableCheckRejected",address,0,region);return false;}
        if(metadataTransaction?.Stage(key,[1])==false)throw new ProbeFailure("GROUP_SOURCE_CHANGED","模組函式頁面驗證已改變。");
        return true;
    }
    // Called only for the stage2 typed root and its bounded HUD graph edges.
    // This method never enumerates or scans heap regions.
    internal bool Pointed(ulong address,int count,out byte[] bytes)
    {
        if(address<0x10000||count<=0||count>4096||address>0x7FFFFFFFFFFFUL-(ulong)count)
            throw new ProbeFailure("POINTED_RANGE","有界物件讀取位址或長度不符。");
        bytes=new byte[count];Boundary();LastNativeError=0;
        nuint got=Native.VirtualQueryEx(handle,(nuint)address,out var region,(nuint)Marshal.SizeOf<Native.MemoryRegion>());
        Boundary();
        if(got!=(nuint)Marshal.SizeOf<Native.MemoryRegion>()){LastNativeError=Marshal.GetLastWin32Error();DiscardMetadataCache("PointedQueryFailure");return false;}
        uint p=region.Protect&0xFF;
        if(region.State!=0x1000||(region.Protect&0x101)!=0||p is not(0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80)||
            region.BaseAddress>address||region.BaseAddress>ulong.MaxValue-region.RegionSize||address+(ulong)count>region.BaseAddress+region.RegionSize){NotePageAccess("PointedReadSkipped",address,count,region);DiscardMetadataCache("PointedPageInvalid");return false;}
        if(count>budget.MaximumReadBytes-RequestedBytes)throw new ProbeFailure("READ_BYTE_LIMIT","已達本次設定的讀取上限。");
        RequestedBytes+=count;ReadCalls++;Audit(address,count);bool ok=Native.ReadProcessMemory(handle,(nuint)address,bytes,(nuint)count,out nuint read);
        if(!ok||read!=(nuint)count)
        {int error=ok?299:Marshal.GetLastWin32Error();if(error==0)error=299;NotePageAccess("PointedReadFailed",address,count,region,error);LastNativeError=error;Array.Clear(bytes);ReadFailures++;ReadErrors[error]=ReadErrors.GetValueOrDefault(error)+1;DiscardMetadataCache("PointedReadFailure");Verify();return false;}
        CompletedBytes+=count;Boundary();return true;
    }
}
