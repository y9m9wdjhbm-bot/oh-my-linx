using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryHub.Reader;

internal static class HudFieldsProbe
{
    private static readonly (string Key,int[] Path,string? Prefix)[] Fields=
    [
        ("Level",[2,2,3,0,3],"LEV|LV|LEVEL|等級"),
        ("Experience",[2,2,3,0,9],"EXP|經驗值|經驗"),
        ("Weight",[2,2,3,0,7],"WT|WEIGHT|負重"),
        ("MapName",[2,2,7,4],null)
    ];
    private sealed record Proof(ulong Address,byte[] Bytes);
    private sealed record Observation(string Text,string Value,ulong Director,ulong Scene,List<Proof> Proofs);
    internal static void Run(ReadSession memory,Journal journal,Budget budget)
    {
        byte[] Read(ulong address,int size)
        {
            budget.Check();if(!memory.Pointed(address,size,out var bytes))throw new ProbeFailure("INFO_READ_FAILED","資訊 HUD 必要資料讀取失敗。",memory.LastNativeError);
            return bytes;
        }
        foreach(var field in Fields)
        {
            string key=field.Key switch{"Level"=>"level","Experience"=>"experiencePercent","Weight"=>"weightPercent",_=>"mapName"};
            if(!journal.Enabled(key))continue;
            for(int cycle=1;cycle<=1;cycle++)
            {
                Observation Capture()
                {
                    var proofs=new List<Proof>();var seen=new HashSet<ulong>();
                    byte[] Track(ulong at,int size){var bytes=Read(at,size);proofs.Add(new(at,bytes));return bytes;}
                    ulong root=memory.Address(0x17E00D8);
                    ulong director=Probe.U64(Track(root,8),0);
                    if(!Probe.Pointer(director))throw new ProbeFailure("INFO_DIRECTOR","無效 Director root。");
                    if(Probe.U64(Track(director,8),0)!=memory.Address(0x121AC40))throw new ProbeFailure("INFO_DIRECTOR_TYPE","Director 型別不符。");
                    ulong scene=Probe.U64(Track(director+0x150,8),0);
                    ulong current=scene,parent=0;
                    ulong[] Node(ulong address,ulong expectedParent,uint vt)
                    {
                        if(!Probe.Pointer(address)||!seen.Add(address))throw new ProbeFailure("INFO_NODE","節點指標無效／重複。");
                        if(Probe.U64(Track(address,8),0)!=memory.Address(vt))throw new ProbeFailure("INFO_NODE_TYPE","精確節點型別不符。");
                        var links=Track(address+0x178,40);
                        if(Probe.U64(links,24)!=expectedParent||Probe.U64(links,32)!=director)throw new ProbeFailure("INFO_NODE_OWNER","節點 parent/director 不符。");
                        return Vector(links.AsSpan(0,24).ToArray());
                    }
                    ulong[] Vector(byte[] bytes)
                    {
                        ulong begin=Probe.U64(bytes,0),end=Probe.U64(bytes,8),cap=Probe.U64(bytes,16);
                        if(begin==0&&end==0&&cap==0)return [];
                        if(!Probe.Pointer(begin)||!Probe.Pointer(end)||!Probe.Pointer(cap)||begin>end||end>cap||(end-begin)%8!=0||(cap-begin)%8!=0||(cap-begin)/8>4096)
                            throw new ProbeFailure("INFO_VECTOR","子集合邊界不符。");
                        var result=new List<ulong>();
                        for(ulong offset=0;offset<end-begin;offset+=4096)
                        {
                            var entries=Track(begin+offset,(int)Math.Min(4096,end-begin-offset));
                            for(int i=0;i<entries.Length;i+=8)result.Add(Probe.U64(entries,i));
                        }
                        if(result.Any(x=>!Probe.Pointer(x))||result.Distinct().Count()!=result.Count)throw new ProbeFailure("INFO_VECTOR_ELEMENT","子指標不合法／不唯一。");
                        return result.ToArray();
                    }
                    for(int depth=0;depth<field.Path.Length;depth++)
                    {
                        var children=Node(current,parent,depth==0?0x121D980u:0x121B1D0u);
                        int index=field.Path[depth];
                        if(index>=children.Length)throw new ProbeFailure("INFO_PATH","既有資訊路徑不存在。");
                        parent=current;current=children[index];
                    }
                    Node(current,parent,0x1223A30);
                    ulong renderer=Probe.U64(Track(current+0x740,8),0);
                    var protectedChildren=Vector(Track(current+0x340,24));
                    if(protectedChildren.Count(x=>x==renderer)!=1)throw new ProbeFailure("INFO_RENDERER","renderer 不屬於唯一 protected child。");
                    Node(renderer,current,0x1220C90);
                    var descriptor=Track(renderer+0x378,32);ulong length=Probe.U64(descriptor,16),capacity=Probe.U64(descriptor,24);
                    if(length is 0 or >511||capacity<length||capacity>4096||(capacity<16&&capacity!=15))throw new ProbeFailure("INFO_STRING","文字大小不合法。");
                    byte[] text=capacity==15?descriptor[..((int)length+1)]:Track(Probe.U64(descriptor,0),(int)length+1);
                    if(text[(int)length]!=0)throw new ProbeFailure("INFO_STRING_END","文字結尾不符。");
                    string raw=new UTF8Encoding(false,true).GetString(text,0,(int)length);
                    string clean=Regex.Replace(raw,@"\x1B\[[0-9;]{0,24}m","").Trim();
                    string value=clean;
                    if(field.Prefix is {} prefix)
                    {
                        value=Regex.Replace(value,"^(?:"+prefix+")\\s*:?\\s*","",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant).TrimEnd('%').Trim();
                        if(!decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out _))throw new ProbeFailure("INFO_VALUE_FORMAT","資訊值不是完整十進位數字。");
                    }
                    return new(raw,value,director,scene,proofs);
                }
                try
                {
                    var at=DateTimeOffset.UtcNow;var a=Capture();var b=Capture();
                    if(a.Director!=b.Director||a.Scene!=b.Scene||a.Text!=b.Text||a.Proofs.Count!=b.Proofs.Count||
                        a.Proofs.Where((p,i)=>p.Address!=b.Proofs[i].Address||!p.Bytes.AsSpan().SequenceEqual(b.Proofs[i].Bytes)).Any())
                        throw new ProbeFailure("INFO_GRAPH_CHANGED","兩輪結構／文字不一致。");
                    foreach(var p in b.Proofs)if(!Read(p.Address,p.Bytes.Length).AsSpan().SequenceEqual(p.Bytes))throw new ProbeFailure("INFO_CLOSING_CHANGED","輸出前結構核對已改變。");
                    memory.Verify();
                    journal.Write("HUD_INFORMATION_SAMPLE",new{field.Key,Sample=cycle,StartedAt=at,EndedAt=DateTimeOffset.UtcNow,a.Text,a.Value,
                        SourceDirector=Probe.Hex(a.Director),SourceScene=Probe.Hex(a.Scene),Path=string.Join('/',field.Path),Proofs=a.Proofs.Count,StructureABAndClosingStable=true,ProductionCompatible=false});
                }
                catch(ProbeFailure error)
                {
                    // This reader performs fresh dynamic HUD reads and stages no
                    // module metadata. A local value/layout change need not evict
                    // unrelated verified metadata; low-level failures still do.
                    journal.Write("HUD_INFORMATION_REJECTED",new{field.Key,Sample=cycle,error.Code,Reason=error.Message,NativeError=error.NativeError,ProductionCompatible=false});
                    if(error.Code is "CONFIGURATION_CHANGED" or "TIME_LIMIT" or "READ_BYTE_LIMIT" or "PROCESS_CHANGED" or "PROCESS_EXITED" or "PROCESS_PATH_CHANGED" or "GROUP_SOURCE_CHANGED" or "IDENTITY_API_FAILED")throw;
                    break;
                }
                
            }
        }
    }
}
