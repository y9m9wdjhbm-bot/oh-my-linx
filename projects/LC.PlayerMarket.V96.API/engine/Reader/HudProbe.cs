using System.Text;

namespace MemoryHub.Reader;

internal static class HudProbe
{
    private const uint Root=0x17E00D8;
    private static readonly (string Label,uint Vtable,uint Col)[] Profiles=
    [("Director",0x121AC40,0x13456E8),("Scene",0x121D980,0x1348E48),
     ("Node",0x121B1D0,0x1345870),("Text",0x1223A30,0x134C1D8),("Label",0x1220C90,0x134ABE8)];
    private sealed record Graph(ulong Hp,ulong Mp,ulong Director,ulong Scene,ulong[] Structure);
    private sealed record Vital(int Current,int Maximum);
    internal static int Run(ReadSession memory,Journal journal,Budget budget)
    {
        bool readHp=journal.Enabled("hp"),readMp=journal.Enabled("mp");
        if(!readHp&&!readMp)return 0;
        uint rootRva=memory.Rva(Root);
        var profiles=Profiles.Select(p=>(p.Label,Vtable:memory.Rva(p.Vtable),Col:memory.Rva(p.Col))).ToArray();
        void Types()
        {
            foreach(var profile in profiles)
            {
                budget.Check();var type=Probe.Types.Single(t=>t.Label==profile.Label);uint descriptorRva=memory.Rva(type.Rva);
                var before=memory.MetaRequired(profile.Vtable-8,24);var col=memory.MetaRequired(profile.Col,24);
                if(Probe.U64(before,0)!=memory.Image+profile.Col||Probe.U32(col,0)!=1||Probe.U32(col,4)!=0||
                   Probe.U32(col,8)!=0||Probe.U32(col,12)!=descriptorRva||Probe.U32(col,20)!=profile.Col||
                   (!memory.Executable(Probe.U64(before,8))&&!memory.Executable(Probe.U64(before,16))))
                    throw new ProbeFailure("HUD_TYPE_LINK",profile.Label+" 的新版本 COL/vtable 關聯不符。");
                byte[] expected=Encoding.ASCII.GetBytes(type.Name+'\0');
                if(!memory.MetaRequired(descriptorRva+16,expected.Length).AsSpan().SequenceEqual(expected))
                    throw new ProbeFailure("HUD_TYPE_NAME",profile.Label+" 精確型別名稱已改變。");
            }
        }
        byte[] Read(ulong address,int count)
        {
            budget.Check();
            if(!memory.Pointed(address,count,out var bytes))
                throw new ProbeFailure("HUD_READ_FAILED","有界 HUD 節點資料無法完整唯讀取得。",memory.LastNativeError);
            return bytes;
        }
        Graph Capture(string phase)
        {
            budget.Check();var parts=new List<ulong>();var seen=new HashSet<ulong>();
            var global=memory.Required(rootRva,8);ulong director=Probe.U64(global,0);
            if(!Probe.Pointer(director))throw new ProbeFailure("HUD_ROOT_INVALID","新版根指標無效。");
            byte[] d=Read(director,0x158);ulong scene=Probe.U64(d,0x150);
            if(Probe.U64(d,0)!=memory.Image+profiles[0].Vtable||!Probe.Pointer(scene))
                throw new ProbeFailure("HUD_DIRECTOR","Director 型別或目前 Scene 不符。");
            parts.AddRange([rootRva,director,Probe.U64(d,0),scene]);
            ulong[] Vector(ulong address,byte[] header,int offset)
            {
                ulong begin=Probe.U64(header,offset),end=Probe.U64(header,offset+8),cap=Probe.U64(header,offset+16);
                parts.AddRange([address,begin,end,cap]);
                if(begin==0&&end==0&&cap==0)return [];
                if(!Probe.Pointer(begin)||!Probe.Pointer(end)||!Probe.Pointer(cap)||end<begin||cap<end||
                   (end-begin)%8!=0||(cap-begin)%8!=0||(cap-begin)/8>4096)
                    throw new ProbeFailure("HUD_VECTOR","HUD 子集合的邊界或容量不符。");
                int size=checked((int)(end-begin));var children=new List<ulong>();var unique=new HashSet<ulong>();
                for(int at=0;at<size;at+=4096)
                {
                    var bytes=Read(begin+(uint)at,Math.Min(4096,size-at));
                    for(int i=0;i<bytes.Length;i+=8)
                    {
                        ulong child=Probe.U64(bytes,i);
                        if(!Probe.Pointer(child)||!unique.Add(child))throw new ProbeFailure("HUD_CHILD","HUD 子指標無效或重複。");
                        children.Add(child);
                    }
                }
                parts.Add((ulong)children.Count);parts.AddRange(children);return children.ToArray();
            }
            ulong[] Node(ulong address,ulong parent,string label)
            {
                if(!Probe.Pointer(address)||!seen.Add(address))throw new ProbeFailure("HUD_CYCLE","HUD 路徑無效或重複。");
                var node=Read(address,0x1A0);var profile=profiles.Single(p=>p.Label==label);
                if(Probe.U64(node,0)!=memory.Image+profile.Vtable||Probe.U64(node,0x190)!=parent||Probe.U64(node,0x198)!=director)
                {
                    journal.Write("HUD_NODE_REJECTED",new{Phase=phase,ExpectedType=label,Address=Probe.Hex(address),
                        ActualVtable=Probe.Hex(Probe.U64(node,0)),ExpectedVtable=Probe.Hex(memory.Image+profile.Vtable),
                        ParentMatches=Probe.U64(node,0x190)==parent,DirectorMatches=Probe.U64(node,0x198)==director});
                    throw new ProbeFailure("HUD_NODE_RELATION","HUD 型別、parent 或 Director 不符。");
                }
                parts.AddRange([address,Probe.U64(node,0),parent,director]);
                return Vector(address+0x178,node,0x178);
            }
            ulong current=scene,parent=0;int[] path=[2,2,0,0];ulong[] children=[];
            for(int depth=0;depth<=path.Length;depth++)
            {
                children=Node(current,parent,depth==0?"Scene":"Node");
                if(depth==path.Length)break;
                if(path[depth]>=children.Length)throw new ProbeFailure("HUD_PATH","既有 HUD 路徑在新版不存在。");
                parent=current;current=children[path[depth]];
            }
            if(readHp&&children.Length<=4||readMp&&children.Length<=7||readHp&&readMp&&children[4]==children[7])throw new ProbeFailure("HUD_VITAL_SLOT","所選 HP/MP 文字節點缺少或重複。");
            ulong Leaf(int slot)
            {
                ulong text=children[slot];Node(text,current,"Text");
                ulong renderer=Probe.U64(Read(text+0x740,8),0);parts.AddRange([text+0x740,renderer]);
                var protectedChildren=Vector(text+0x340,Read(text+0x340,24),0);
                if(protectedChildren.Count(c=>c==renderer)!=1)throw new ProbeFailure("HUD_RENDERER","文字 renderer 不在唯一 protected 子集合中。");
                Node(renderer,text,"Label");return renderer;
            }
            ulong hp=readHp?Leaf(4):0,mp=readMp?Leaf(7):0;return new(hp,mp,director,scene,parts.ToArray());
        }
        Vital Decode(ulong label,string prefix)
        {
            byte[] sso=Read(label+0x378,32);ulong size=Probe.U64(sso,16),cap=Probe.U64(sso,24);
            if(cap!=15||size is <6 or >15||sso[(int)size]!=0||sso[0]!=prefix[0]||sso[1]!=prefix[1]||sso[2]!=':')
                throw new ProbeFailure("HUD_VITAL_FORMAT",prefix+" 的完整 SSO 文字格式不符。");
            var ratio=sso.AsSpan(3,(int)size-3);int slash=ratio.IndexOf((byte)'/');
            if(slash<=0||slash>=ratio.Length-1)throw new ProbeFailure("HUD_VITAL_RATIO",prefix+" 比值分隔不符。");
            int Number(ReadOnlySpan<byte> digits)
            {
                int result=0;foreach(byte c in digits)
                {if(c<'0'||c>'9')throw new ProbeFailure("HUD_VITAL_NUMBER","比值不是十進位數字。");result=checked(result*10+c-'0');}
                return result;
            }
            int value=Number(ratio[..slash]),maximum=Number(ratio[(slash+1)..]);
            if(maximum<=0||value>maximum)throw new ProbeFailure("HUD_VITAL_BOUNDS",prefix+" 比值超界。");
            return new(value,maximum);
        }
        Graph? previous=null;int samples=0;
        for(int cycle=0;cycle<1;cycle++)
        {
            budget.Check();Types();var start=DateTimeOffset.UtcNow;
            Graph a=Capture("A");Vital? hp=readHp?Decode(a.Hp,"HP"):null,mp=readMp?Decode(a.Mp,"MP"):null;
            Graph b=Capture("B");Vital? hpAfter=readHp?Decode(b.Hp,"HP"):null,mpAfter=readMp?Decode(b.Mp,"MP"):null;
            Graph c=Capture("C");Types();memory.Verify();
            bool same=a.Hp==b.Hp&&a.Mp==b.Mp&&a.Structure.AsSpan().SequenceEqual(b.Structure)&&a.Structure.AsSpan().SequenceEqual(c.Structure);
            if(!same||hp!=hpAfter||mp!=mpAfter)throw new ProbeFailure("HUD_CHANGED","本輪 HUD 結構或血魔數值變動，已捨棄。");
            var closedRoot=memory.Required(rootRva,8);var closedDirector=Read(a.Director,0x158);var closedScene=Read(a.Scene,0x1A0);
            if(Probe.U64(closedRoot,0)!=a.Director||Probe.U64(closedDirector,0)!=memory.Image+profiles[0].Vtable||
               Probe.U64(closedDirector,0x150)!=a.Scene||Probe.U64(closedScene,0)!=memory.Image+profiles[1].Vtable||
               Probe.U64(closedScene,0x190)!=0||Probe.U64(closedScene,0x198)!=a.Director)
                throw new ProbeFailure("HUD_FINAL_SOURCE_CHANGED","輸出前的根節點或目前場景關聯已變動。");
            bool between=previous is not null&&previous.Structure.AsSpan().SequenceEqual(a.Structure);
            journal.Write("HUD_VITAL_SAMPLE",new{Sample=cycle+1,StartedAt=start,EndedAt=DateTimeOffset.UtcNow,
                Hp=hp,Mp=mp,
                HpLabel=Probe.Hex(a.Hp),MpLabel=Probe.Hex(a.Mp),Director=Probe.Hex(a.Director),Scene=Probe.Hex(a.Scene),
                SourceRootRva=Probe.Hex(rootRva),StructureABCStable=true,ValuesABStable=true,FinalSourceRechecked=true,StableAcrossPriorCycle=between,
                StructuralValues=a.Structure.Length,MetadataEvidence=memory.MetadataEvidence,ObservedGameValueChange=false,ProductionCompatible=false});
            samples++;previous=a;
        }
        return samples;
    }
}
