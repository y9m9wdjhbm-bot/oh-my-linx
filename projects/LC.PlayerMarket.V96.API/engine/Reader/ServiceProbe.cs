using System.Buffers.Binary;
using System.Text;

namespace MemoryHub.Reader;

internal static class ServiceProbe
{
    private sealed record TypeInfo(string Name,uint Vtable,uint Descriptor,uint Col,bool FirstSlotExecutable,bool SecondSlotExecutable);
    private sealed record Proof(ulong At,byte[] Bytes,bool Metadata=false);
    private static readonly HashSet<string> Wanted=new(StringComparer.Ordinal)
    {
        ".?AVActorSubsystem@lineage@@", ".?AVCharacterDataSubsystem@lineage@@"
    };
    internal static int Run(ReadSession memory,Journal journal,Budget budget)
    {
        var types=new Dictionary<ulong,TypeInfo?>();
        byte[] Read(ulong at,int count)
        {
            budget.Check();if(!memory.Pointed(at,count,out var bytes))throw new ProbeFailure("SERVICE_READ_FAILED","有界服務資料無法讀取。",memory.LastNativeError);return bytes;
        }
        byte[] Meta(ulong at,int count)
        {
            if(at<memory.Image||at-memory.Image>(ulong)memory.ImageSize-(uint)count)throw new ProbeFailure("SERVICE_META_RANGE","RTTI 超出載入模組。");
            return memory.MetaRequired((uint)(at-memory.Image),count);
        }
        TypeInfo? Type(ulong pointer)
        {
            if(types.TryGetValue(pointer,out var cached))return cached;
            if(types.Count>2048)throw new ProbeFailure("SERVICE_TYPE_LIMIT","型別候選數超界。");
            int metadataCheckpoint=memory.MetadataCheckpoint();
            TypeInfo? result=null;
            if(pointer>=memory.Image+8&&pointer-memory.Image<(ulong)memory.ImageSize-16)
            {
                ulong col=Probe.U64(Meta(pointer-8,8),0);
                if(col>=memory.Image&&(col&3)==0&&col-memory.Image<(ulong)memory.ImageSize-24)
                {
                    var c=Meta(col,24);uint td=Probe.U32(c,12);
                    if(Probe.U32(c,0)==1&&Probe.U32(c,4)==0&&Probe.U32(c,8)==0&&Probe.U32(c,20)==col-memory.Image&&Probe.U32(c,16)<(uint)memory.ImageSize-16&&td<(uint)memory.ImageSize-160)
                    {
                        var collected=new List<byte>();bool terminated=false;
                        for(int offset=0;offset<128&&!terminated;offset+=8)
                        {
                            var chunk=Meta(memory.Image+td+16+(uint)offset,8);
                            foreach(byte b in chunk){if(b==0){terminated=true;break;}collected.Add(b);}
                        }
                        var bytes=collected.ToArray();int end=bytes.Length;
                        if(terminated&&end>5&&end<128&&bytes.AsSpan(0,end).IndexOfAnyExceptInRange((byte)33,(byte)126)<0&&bytes[0]=='.'&&bytes[1]=='?')
                        {
                            string name=Encoding.ASCII.GetString(bytes,0,end);ulong entry=Probe.U64(Meta(pointer,8),0);
                            bool executable=memory.Executable(entry);bool secondExecutable=memory.Executable(Probe.U64(Meta(pointer+8,8),0));
                            // These two roots begin with delegate subobjects, not a callable service interface.
                            bool leadingDelegate=name is ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@" or ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@";
                            if(executable||secondExecutable||leadingDelegate)result=new(name,(uint)(pointer-memory.Image),td,(uint)(col-memory.Image),executable,secondExecutable);
                        }
                    }
                }
            }
            if(result is null)memory.RollbackMetadataCandidate(metadataCheckpoint);
            types[pointer]=result;return result;
        }
        uint rootSlot=memory.Rva(0x1642B60);
        ulong rootObject=Probe.U64(memory.Required(rootSlot,8),0);
        if(!Probe.Pointer(rootObject))throw new ProbeFailure("SERVICE_ROOT_INVALID","已驗服務根目前未就緒。");
        var rootType=Type(Probe.U64(Read(rootObject,8),0));
        if(rootType?.Name!=".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@")
            throw new ProbeFailure("SERVICE_ROOT_TYPE","已驗服務根精確型別不符，不搜尋其他地址。");
        var root=(Slot:rootSlot,Object:rootObject,Type:rootType!);
        for(int cycle=1;cycle<=1;cycle++)
        {
            var proofs=new List<Proof>();
            byte[] Track(ulong at,int size){var bytes=Read(at,size);proofs.Add(new(at,bytes));return bytes;}
            void Exact(ulong address,string name)
            {
                var type=Type(Probe.U64(Track(address,8),0));
                if(type?.Name!=name)throw new ProbeFailure("SERVICE_TYPE_CHANGED","選中服務／自身物件型別不符。");
                var col=Meta(memory.Image+type.Col,24);var vprefix=Meta(memory.Image+type.Vtable-8,24);
                if(Probe.U32(col,0)!=1||Probe.U32(col,4)!=0||Probe.U32(col,8)!=0||Probe.U32(col,12)!=type.Descriptor||Probe.U32(col,20)!=type.Col||Probe.U64(vprefix,0)!=memory.Image+type.Col)
                    throw new ProbeFailure("SERVICE_TYPE_CHANGED","選中型別中繼資料已改變。");
                if((type.FirstSlotExecutable&&!memory.Executable(Probe.U64(vprefix,8)))||(type.SecondSlotExecutable&&!memory.Executable(Probe.U64(vprefix,16))))throw new ProbeFailure("SERVICE_TYPE_CHANGED","選中型別的函式槽已不再可執行。");
                var expected=Encoding.ASCII.GetBytes(name+'\0');
                if(!Meta(memory.Image+type.Descriptor+16,expected.Length).AsSpan().SequenceEqual(expected))throw new ProbeFailure("SERVICE_TYPE_CHANGED","選中精確名稱改變。");
                proofs.Add(new(memory.Image+type.Col,col,true));proofs.Add(new(memory.Image+type.Vtable-8,vprefix,true));
                proofs.Add(new(memory.Image+type.Descriptor+16,expected,true));
            }
            List<(ulong Object,ulong Control)> Vector(ulong at,int limit)
            {
                var h=Track(at,24);ulong begin=Probe.U64(h,0),end=Probe.U64(h,8),cap=Probe.U64(h,16);
                if(begin==0&&end==0&&cap==0)return [];
                if(!Probe.Pointer(begin)||!Probe.Pointer(end)||!Probe.Pointer(cap)||begin>end||end>cap||(end-begin)%16!=0||(cap-begin)%16!=0||(cap-begin)/16>(ulong)limit)
                    throw new ProbeFailure("SERVICE_VECTOR","shared_ptr 集合界線不符。");
                var rows=new List<(ulong Object,ulong Control)>();
                for(ulong i=0;i<end-begin;i+=4096)
                {
                    var b=Track(begin+i,(int)Math.Min(4096,end-begin-i));
                    for(int j=0;j<b.Length;j+=16)
                    {
                        ulong obj=Probe.U64(b,j),control=Probe.U64(b,j+8);
                        if(obj==0)continue;
                        if(!Probe.Pointer(obj)||!Probe.Pointer(control))throw new ProbeFailure("SERVICE_OWNERSHIP","shared_ptr 物件／控制指標無效。");
                        if(rows.Any(x=>x.Object==obj&&x.Control!=control))throw new ProbeFailure("SERVICE_OWNERSHIP","物件所屬控制指標衝突。");
                        rows.Add((obj,control));
                    }
                }
                return rows.Distinct().ToList();
            }
            if(Probe.U64(Track(memory.Image+root.Slot,8),0)!=root.Object)throw new ProbeFailure("SERVICE_ROOT_CHANGED","服務根改變。");
            Exact(root.Object,root.Type.Name);
            ulong director=Probe.U64(Track(memory.Address(0x17E00D8),8),0);Exact(director,".?AVDirector@cocos2d@@");
            ulong scene=Probe.U64(Track(director+0x150,8),0);Exact(scene,".?AVScene@cocos2d@@");
            var sceneOwner=Track(scene+0x190,16);
            if(Probe.U64(sceneOwner,0)!=0||Probe.U64(sceneOwner,8)!=director)throw new ProbeFailure("SERVICE_SCENE_OWNER","目前場景來源不符。");
            if(Track(root.Object+0xD8,1)[0]==0)throw new ProbeFailure("SERVICE_NOT_READY","服務根尚未就緒。");
            ulong holder=Probe.U64(Track(root.Object+0xD0,8),0);Exact(holder,".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@");
            var subs=Vector(holder+0x180,128);var found=new Dictionary<string,ulong>(StringComparer.Ordinal);
            foreach(var pair in subs)
            {
                var type=Type(Probe.U64(Track(pair.Object,8),0));
                if(type is null||!Wanted.Contains(type.Name))continue;
                if(!found.TryAdd(type.Name,pair.Object))throw new ProbeFailure("SERVICE_DUPLICATE","精確型別服務不唯一。");
                Exact(pair.Object,type.Name);
                journal.Write("TYPED_SERVICE_MEMBER",new{Sample=cycle,type.Name,Address=Probe.Hex(pair.Object),Control=Probe.Hex(pair.Control),VtableRva=Probe.Hex(type.Vtable),DescriptorRva=Probe.Hex(type.Descriptor)});
            }
            ulong System(string name)=>found.TryGetValue(".?AV"+name+"@lineage@@",out var address)?address:throw new ProbeFailure("SERVICE_MISSING","缺少必要精確型別服務："+name);
            ulong actors=System("ActorSubsystem"),character=System("CharacterDataSubsystem");
            ulong ownId=Probe.U64(Track(character+0x6D8,8),0);
            if(ownId is 0 or ulong.MaxValue)throw new ProbeFailure("SELF_ID_INVALID","目前自身 key 無效。");
            var actorRows=new List<(ulong Object,ulong Control)>();
            foreach(ulong offset in new ulong[]{0x8A8,0x8C0,0x8D8})actorRows.AddRange(Vector(actors+offset,512));
            if(actorRows.Select(x=>x.Object).Distinct().Count()>512)throw new ProbeFailure("SELF_ACTOR_LIMIT","自身所屬集合超界。");
            var selves=new List<(ulong Object,ulong Control)>();
            foreach(var pair in actorRows.Distinct())
            {
                var type=Type(Probe.U64(Track(pair.Object,8),0));
                if(type?.Name!=".?AVMyPlayer@lineage@@")continue;
                Exact(pair.Object,type.Name);
                var getterSlot=Meta(memory.Image+type.Vtable+8,8);ulong getter=Probe.U64(getterSlot,0);
                proofs.Add(new(memory.Image+type.Vtable+8,getterSlot,true));
                var expected=new byte[]{0x48,0x8B,0x41,0x18,0xC3};
                if(!Meta(getter,5).AsSpan().SequenceEqual(expected))throw new ProbeFailure("SELF_GETTER_CHANGED","完整 ID getter 需重新研究。");
                proofs.Add(new(getter,expected,true));
                if(Probe.U64(Track(pair.Object+0x18,8),0)==ownId)selves.Add(pair);
            }
            if(selves.Count!=1)throw new ProbeFailure("SELF_AMBIGUOUS","沒有唯一同 key MyPlayer。");
            ulong actor=selves[0].Object;ulong transform=0;int? x=null,y=null;
            var components=journal.Enabled("position")?Vector(actor+0x20,64):[];
            foreach(var pair in components)
            {
                var type=Type(Probe.U64(Track(pair.Object,8),0));
                if(type?.Name!=".?AVTransformComponent@lineage@@")continue;
                if(transform!=0)throw new ProbeFailure("TRANSFORM_AMBIGUOUS","Transform 不唯一。");
                transform=pair.Object;Exact(transform,type.Name);
                if(Probe.U64(Track(transform+8,8),0)!=actor)throw new ProbeFailure("TRANSFORM_OWNER","座標元件不屬於自身角色。");
            }
            if(journal.Enabled("position"))
            {
                if(transform==0)throw new ProbeFailure("TRANSFORM_MISSING","缺少自身 Transform。");
                var xy=Track(transform+0x40,8);x=BinaryPrimitives.ReadInt32LittleEndian(xy);y=BinaryPrimitives.ReadInt32LittleEndian(xy.AsSpan(4));
                if(x is <=0 or >1000000||y is <=0 or >1000000)throw new ProbeFailure("SELF_COORDINATE_RANGE","座標值超界。");
            }
            foreach(var p in proofs)
            {
                var after=p.Metadata?Meta(p.At,p.Bytes.Length):Read(p.At,p.Bytes.Length);
                if(!after.AsSpan().SequenceEqual(p.Bytes))throw new ProbeFailure("SERVICE_CLOSURE_CHANGED","服務／自身集合或欄位結尾核對改變，捨棄本輪。");
            }
            // Recheck source and selected owner/key after the longer full proof walk.
            var finalSources=new HashSet<ulong>{memory.Image+root.Slot,root.Object,root.Object+0xD0,root.Object+0xD8,
                memory.Address(0x17E00D8),director,director+0x150,scene,scene+0x190,holder,holder+0x180,
                character,character+0x6D8,actors,actor,actor+0x18,transform,transform+8,transform+0x40};
            foreach(var p in proofs.Where(p=>finalSources.Contains(p.At)))
                if(!Read(p.At,p.Bytes.Length).AsSpan().SequenceEqual(p.Bytes))throw new ProbeFailure("SERVICE_FINAL_SOURCE_CHANGED","輸出前來源／自身／座標已改變。");
            memory.Verify();
            var identityProofs=proofs.Where(p=>finalSources.Contains(p.At)&&p.At!=transform+0x40).ToArray();
            var previousSource=journal.SourceClosure;
            journal.SourceClosure=()=>
            {
                previousSource?.Invoke();
                foreach(var p in identityProofs)
                    if(!Read(p.At,p.Bytes.Length).AsSpan().SequenceEqual(p.Bytes))
                        throw new ProbeFailure("GROUP_SOURCE_CHANGED","跨資料組的自身／場景來源已變更。");
                memory.Verify();
            };
            journal.Write("SERVICE_SELF_SAMPLE",new{Sample=cycle,ServiceRootRva=Probe.Hex(root.Slot),Root=Probe.Hex(root.Object),Holder=Probe.Hex(holder),Director=Probe.Hex(director),Scene=Probe.Hex(scene),Subsystems=subs.Count,OwnId=ownId,Actor=Probe.Hex(actor),Transform=Probe.Hex(transform),X=x,Y=y,Proofs=proofs.Count,ClosedStable=true,MetadataEvidence=memory.MetadataEvidence,FinalSourceRechecked=true,UserCoordinateVerified=false,ProductionCompatible=false});
            
        }
        return 1;
    }
}
