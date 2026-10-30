using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MemoryHub.Reader;

internal sealed class ProbeFailure(string code,string message,int nativeError=0):Exception(message)
{internal string Code{get;}=code;internal int NativeError{get;}=nativeError;}
internal sealed class Budget(Stopwatch timer,CancellationToken token,Func<bool>? selected=null,int maximumMilliseconds=Probe.MaximumMilliseconds,long maximumReadBytes=Probe.MaximumReadBytes)
{
    internal long MaximumReadBytes {get;}=maximumReadBytes;
    internal double RemainingMilliseconds=>Math.Max(0,maximumMilliseconds-timer.Elapsed.TotalMilliseconds);
    internal void Check(){token.ThrowIfCancellationRequested();if(selected is not null&&!selected())throw new ProbeFailure("CONFIGURATION_CHANGED","讀取選項已變更，停止舊設定的採樣。");if(timer.ElapsedMilliseconds>=maximumMilliseconds)throw new ProbeFailure("TIME_LIMIT",$"已達 {maximumMilliseconds/1000} 秒期限，停止後續讀取。");}
}

internal sealed partial class ReadSession:IDisposable
{
    private readonly SafeProcessHandle handle;
    private readonly int pid;
    private readonly long creation;
    private readonly Budget budget;
    private readonly Action<ulong,int>? audit;
    private readonly StringBuilder path=new(32768);
    private int boundaries;
    // A bound handle cannot switch to a reused PID. Full identity checks run at open, groups,
    // final closure, failures and every 128 boundaries; cancellation/budget run at every boundary.
    internal void Boundary(){try{budget.Check();if((++boundaries&127)==0)Verify();}catch{DiscardMetadataCache("BudgetCancellationOrIdentityFailure");throw;}}
    internal ulong Image{get;private set;}
    internal ReaderProfile Profile {get;private set;}=ReaderProfile.Builtin;
    internal uint Rva(uint symbol)=>Profile.Resolve(symbol);
    internal ulong Address(uint symbol,int delta=0)
    {long rva=(long)Rva(symbol)+delta;if(rva<0||rva>=ImageSize)throw new ProbeFailure("PROFILE_RANGE","版本來源超出模組");return Image+(ulong)rva;}
    internal void UseProfile(ReaderProfile profile){Profile=profile;}
    internal int ProcessId=>pid;
    internal long CreationFileTime=>creation;
    internal int ImageSize{get;private set;}
    internal long RequestedBytes{get;private set;}
    internal long ReadCalls{get;private set;}
    internal int LastNativeError{get;private set;}
    internal long CompletedBytes{get;private set;}
    internal int ReadFailures{get;private set;}
    internal Dictionary<int,int> ReadErrors{get;}=[];
    internal ReadSession(int pid,long creation,Budget budget,Action<ulong,int>? audit=null)
    {
        this.pid=pid;this.creation=creation;this.budget=budget;this.audit=audit;budget.Check();
        AdministratorExecution.Require();
        handle=Native.OpenProcess(0x410,false,pid);
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new ProbeFailure("OPEN_PROCESS","無法以 0x410 唯讀權限開啟指定程序。",error);}
    }
    internal void Bind(ulong image,int size){Image=image;ImageSize=size;}
    internal bool InImage(uint rva,int count)=>count>0&&count<=ImageSize&&rva<=(uint)(ImageSize-count);
    internal void Verify()
    {
        budget.Check();uint actual=Native.GetProcessId(handle);
        if(actual==0)throw NativeFailure("GetProcessId");
        if(actual!=(uint)pid)throw new ProbeFailure("PID_CHANGED","原程序 PID 已變更。");
        budget.Check();if(!Native.GetProcessTimes(handle,out long started,out long exited,out _,out _))throw NativeFailure("GetProcessTimes");
        if(started!=creation||exited!=0)throw new ProbeFailure("PROCESS_CHANGED","原程序建立時間不符或已結束。");
        budget.Check();if(!Native.GetExitCodeProcess(handle,out uint exitCode))throw NativeFailure("GetExitCodeProcess");
        if(exitCode!=259)throw new ProbeFailure("PROCESS_EXITED","原程序已結束。");
        budget.Check();path.Clear();uint length=32768;
        if(!Native.QueryFullProcessImageName(handle,0,path,ref length))throw NativeFailure("QueryFullProcessImageName");
        if(!string.Equals(path.ToString(),Probe.ExePath,StringComparison.OrdinalIgnoreCase))throw new ProbeFailure("PROCESS_PATH_CHANGED","原程序完整路徑不符。");
        budget.Check();
    }
    internal bool Query(uint rva,out Native.MemoryRegion region)
    {
        Boundary();LastNativeError=0;if(!InImage(rva,1))throw new ProbeFailure("READ_RANGE","頁面查詢超出主模組。");
        nuint count=Native.VirtualQueryEx(handle,(nuint)(Image+rva),out region,(nuint)Marshal.SizeOf<Native.MemoryRegion>());
        if(count!=(nuint)Marshal.SizeOf<Native.MemoryRegion>()){LastNativeError=Marshal.GetLastWin32Error();DiscardMetadataCache("ModuleQueryFailure");}Boundary();return count==(nuint)Marshal.SizeOf<Native.MemoryRegion>();
    }
    internal bool Small(uint rva,byte[] data,int count)
    {
        if(!InImage(rva,count)||!Query(rva,out var region)){DiscardMetadataCache("ModuleReadRangeOrQueryFailure");return false;}
        ulong address=Image+rva;
        uint protection=region.Protect&0xFF;
        if(region.State!=0x1000||(region.Protect&0x101)!=0||protection is not(0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80)||
            region.BaseAddress>address||region.BaseAddress>ulong.MaxValue-region.RegionSize||address+(uint)count>region.BaseAddress+region.RegionSize){NotePageAccess("ModuleReadSkipped",address,count,region);DiscardMetadataCache("ModulePageInvalid");return false;}
        return Read(rva,data,count);
    }
    internal byte[] Required(uint rva,int count)
    {var bytes=new byte[count];if(!Small(rva,bytes,count))throw new ProbeFailure("REQUIRED_READ_FAILED","必要模組資料未完整讀取。",LastNativeError);return bytes;}
    internal bool Read(uint rva,byte[] data,int count)
    {
        Boundary();
        if(count<=0||count>Probe.ChunkBytes||count>data.Length||!InImage(rva,count))throw new ProbeFailure("READ_RANGE","讀取超出主模組或單塊界線。");
        if(count>budget.MaximumReadBytes-RequestedBytes)throw new ProbeFailure("READ_BYTE_LIMIT","已達本次設定的讀取上限。");
        RequestedBytes+=count;ReadCalls++;
        Audit(Image+rva,count);
        bool ok=Native.ReadProcessMemory(handle,(nuint)(Image+rva),data,(nuint)count,out nuint read);
        if(!ok||read!=(nuint)count)
        {
            int error=ok?299:Marshal.GetLastWin32Error();if(error==0)error=299;
            NotePageAccess("ModuleReadFailed",Image+rva,count,nativeError:error);
            LastNativeError=error;Array.Clear(data,0,count);ReadFailures++;ReadErrors[error]=ReadErrors.GetValueOrDefault(error)+1;DiscardMetadataCache("ModuleReadFailure");Verify();return false;
        }
        CompletedBytes+=count;Boundary();return true;
    }
    private static ProbeFailure NativeFailure(string api)=>new("IDENTITY_API_FAILED","來源核對 API 失敗："+api,Marshal.GetLastWin32Error());
    private void Audit(ulong address,int count){try{audit?.Invoke(address,count);}catch{}}
    public void Dispose(){DiscardMetadataCache("UncommittedSessionDisposed");handle.Dispose();}
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryRegion
    {
        internal ulong BaseAddress,AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId,Padding;
        internal ulong RegionSize;
        internal uint State,Protect,Type,Padding2;
    }
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool GetProcessTimes(SafeProcessHandle process,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint code);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref uint length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ReadProcessMemory(SafeProcessHandle process,nuint address,[Out]byte[] data,nuint count,out nuint read);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern nuint VirtualQueryEx(SafeProcessHandle process,nuint address,out MemoryRegion region,nuint length);
}
