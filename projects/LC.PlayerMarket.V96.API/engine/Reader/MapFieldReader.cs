using System.Buffers.Binary;
using System.Text;

namespace MemoryHub.Reader;

/// <summary>One current foot tile; read-only and bounded. No pathfinding or game input.</summary>
internal static class MapFieldReader
{
    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        if(!selection.Enabled("safeZone"))return;
        var at=DateTimeOffset.UtcNow;var g=new CurrentTypedGraph(memory,journal,budget,"MAP_FIELD");
        ulong map=g.Subsystem(".?AVMapSubsystem@lineage@@",0x11ABCB8,0x1643E28);
        ulong mapData=g.PointerAt(map+0x6E0);
        var own=g.Self();ulong transform=g.Component(own.Actor,".?AVTransformComponent@lineage@@");
        byte[] raw=g.Track(transform+0x38,80);var current=Coordinate.Read(raw,0);var next=Coordinate.Read(raw,40);
        byte[] directXY=g.Track(transform+0x40,8);
        g.Need(CurrentTypedGraph.I32(directXY)==current.X&&CurrentTypedGraph.I32(directXY,4)==current.Y,"CURRENT_XY","目前 XY 與 current Coordinate40 不一致。");
        var coordinateEvidence=CoordinateEncodingEvidence.Capture(CoordinateEncodingSource.Current(memory,own.Id,own.Actor,transform),raw);
        ulong head=g.PointerAt(mapData+8);byte[] sentinel=g.Track(head,56);
        g.Need(sentinel[0x19]==1,"CHUNK_SENTINEL","地圖 chunk sentinel 不符。");
        ulong node=CurrentTypedGraph.U64(sentinel,8),parent=head,chunk=0;
        long low=-1,high=(long)uint.MaxValue+1;var visited=new HashSet<ulong>();bool terminated=false;
        for(int depth=0;depth<64;depth++)
        {
            g.Pointer(node);g.Need(visited.Add(node),"CHUNK_CYCLE","chunk 搜尋有循環。");
            if(node==head){terminated=true;break;}
            byte[] n=g.Track(node,56);uint key=CurrentTypedGraph.U32(n,0x20);
            g.Need(n[0x19]==0&&n[0x18]<=1&&CurrentTypedGraph.U64(n,8)==parent&&key>low&&key<high,"CHUNK_NODE","chunk 樹節點／排序不符。");
            if(key==current.Key){chunk=CurrentTypedGraph.U64(n,0x28);g.Pointer(chunk);g.Control(CurrentTypedGraph.U64(n,0x30));terminated=true;break;}
            parent=node;if(current.Key<key){high=key;node=CurrentTypedGraph.U64(n);}else{low=key;node=CurrentTypedGraph.U64(n,0x10);}
        }
        g.Need(terminated,"CHUNK_DEPTH","chunk 搜尋超過 64 節點。");
        if(chunk==0){g.Close();g.AttachSourceClosure();journal.Unknown("safeZone","目前格的 map chunk 未載入，不能判斷安全區");return;}
        int localX=current.LocalX,localY=current.LocalY;
        g.Need(localX is >=0 and <128&&localY is >=0 and <64,"LOCAL_RANGE","原生地圖局部座標超出 128×64。");
        ulong overrideHead=g.PointerAt(chunk+0x18);byte[] overrideSentinel=g.Track(overrideHead,48);
        g.Need(overrideSentinel[0x19]==1,"OVERRIDE_SENTINEL","地磚覆寫 sentinel 不符。");
        node=CurrentTypedGraph.U64(overrideSentinel,8);parent=overrideHead;visited.Clear();
        byte[]? tile=null;string tileSource="Base";terminated=false;(int X,int Y)? lower=null,upper=null;
        for(int depth=0;depth<64;depth++)
        {
            g.Pointer(node);g.Need(visited.Add(node),"OVERRIDE_CYCLE","地磚覆寫搜尋有循環。");
            if(node==overrideHead){terminated=true;break;}
            byte[] n=g.Track(node,48);var key=(X:CurrentTypedGraph.I32(n,0x1C),Y:CurrentTypedGraph.I32(n,0x20));
            g.Need(n[0x19]==0&&n[0x18]<=1&&CurrentTypedGraph.U64(n,8)==parent&&
                (lower is null||Compare(key,lower.Value)>0)&&(upper is null||Compare(key,upper.Value)<0),"OVERRIDE_NODE","地磚覆寫樹節點／排序不符。");
            int comparison=Compare((localX,localY),key);
            if(comparison==0){tile=n.AsSpan(0x24,8).ToArray();tileSource="Override";terminated=true;break;}
            parent=node;if(comparison<0){upper=key;node=CurrentTypedGraph.U64(n);}else{lower=key;node=CurrentTypedGraph.U64(n,0x10);}
        }
        g.Need(terminated,"OVERRIDE_DEPTH","地磚覆寫搜尋超過 64 節點。");
        if(tile is null)
        {
            byte[] rows=g.Track(chunk,24);ulong begin=CurrentTypedGraph.U64(rows),end=CurrentTypedGraph.U64(rows,8),capacity=CurrentTypedGraph.U64(rows,16);
            g.Pointer(begin);g.Pointer(end);g.Pointer(capacity);
            g.Need(begin<=end&&end<=capacity&&(end-begin)%24==0&&(capacity-begin)%24==0&&(end-begin)/24==64&&(capacity-begin)/24<=128,"ROWS","地磚列陣列不符合 64 列。");
            byte[] row=g.Track(begin+(ulong)localY*24,24);ulong tiles=CurrentTypedGraph.U64(row),tileEnd=CurrentTypedGraph.U64(row,8),tileCap=CurrentTypedGraph.U64(row,16);
            g.Pointer(tiles);g.Pointer(tileEnd);g.Pointer(tileCap);
            g.Need(tiles<=tileEnd&&tileEnd<=tileCap&&(tileEnd-tiles)%8==0&&(tileCap-tiles)%8==0&&(tileEnd-tiles)/8==128&&(tileCap-tiles)/8<=256,"COLUMNS","地磚欄陣列不符合 128 欄。");
            tile=g.Track(tiles+(ulong)localX*8,8);
        }
        ushort flags=BinaryPrimitives.ReadUInt16LittleEndian(tile.AsSpan(4));
        coordinateEvidence.Verify(CoordinateEncodingSource.Current(memory,own.Id,own.Actor,transform),g.Track(transform+0x38,80));
        g.Close();g.AttachSourceClosure();
        journal.Write("MAP_CURRENT_TILE_SAMPLE",new {StartedAt=at,CurrentX=current.X,CurrentY=current.Y,NextX=next.X,NextY=next.Y,
            CurrentEqualsNext=current.X==next.X&&current.Y==next.Y,ChunkX=current.ChunkX,ChunkY=current.ChunkY,
            LocalX=localX,LocalY=localY,RawTileHex=Convert.ToHexString(tile),Flags=flags,TileSource=tileSource,
            SourceClosed=true,CoordinateEncodingValidated=true,CoordinateScaleEvidence="SameSourceCurrentNextCoordinate40_48And24Candidate_OwnerAndABClosed",RelocatedCodeConstant=false,AtomicSnapshot=false,UserZoneVerified=false});
        if(flags==4)journal.Set("safeZone",true,at,"CurrentTileFlagsStructureValidated");
        else if(flags is 0 or 8)journal.Set("safeZone",false,at,"CurrentTileFlagsStructureValidated");
        else journal.Unknown("safeZone",$"目前地磚原始旗標 0x{flags:X4} 尚無已確認分類");
    }
    private static int Compare((int X,int Y) a,(int X,int Y) b)=>a.X!=b.X?a.X.CompareTo(b.X):a.Y.CompareTo(b.Y);
    private sealed record Coordinate(int ChunkX,int ChunkY,int X,int Y,int U,int V)
    {
        internal uint Key=>unchecked(((uint)ChunkX<<16)|(uint)ChunkY);
        internal int LocalX=>checked((0x7F00-ChunkX)*128+U);
        internal int LocalY=>checked((0x7E00-ChunkY)*64+V);
        internal static Coordinate Read(byte[] bytes,int offset)
        {
            int I(int at)=>CurrentTypedGraph.I32(bytes,offset+at);int x=I(8),y=I(12);
            if(x is <0 or >65535||y is <0 or >65535)throw new ProbeFailure("MAP_FIELD_COORDINATE","Coordinate40 世界座標範圍不符。");
            float px=(x+y-65535)*48,py=(x-y)*24;
            int[] expected=[x/64+0x7E00,y/64+0x7E00,x,y,x*2-0x8000,y,
                BitConverter.SingleToInt32Bits(px),BitConverter.SingleToInt32Bits(py),BitConverter.SingleToInt32Bits(px-48f),BitConverter.SingleToInt32Bits(py+0f)];
            for(int i=0;i<10;i++)if(I(i*4)!=expected[i])throw new ProbeFailure("MAP_FIELD_COORDINATE","完整 Coordinate40 的 chunk／XY／UV／像素編碼不一致。");
            return new(expected[0],expected[1],x,y,expected[4],expected[5]);
        }
    }
}

/// <summary>Shared bounded current-build identity graph. It owns no process handle.</summary>
internal sealed class CurrentTypedGraph
{
    private readonly ReadSession memory;private readonly Journal journal;private readonly Budget budget;private readonly string prefix;
    private readonly Dictionary<(ulong At,int Count),byte[]> proofs=[];
    private readonly HashSet<(ulong At,int Count)> sourceProofs=[];
    private readonly HashSet<(ulong At,int Count)> metadataProofs=[];
    private readonly Dictionary<ulong,TypeRow?> types=[];private readonly HashSet<ulong> controls=[];
    private readonly HashSet<ulong> sourceControls=[];
    private readonly List<ReadOnlyUiSubsystemContract.Candidate> subsystemProofs=[];
    private readonly List<SourceMembership> sourceMemberships=[];
    private readonly Dictionary<ulong,ulong> ownership=[];
    private readonly List<(ulong Header,List<Pair> Entries)> actorVectors=[];
    private readonly List<Pair> subs;private List<Pair>? actors;private int reads,bytes;
    private readonly ulong director,scene,subsystemHeader;private ulong actorSubsystem;private Player? selectedSelf;
    internal sealed record Pair(ulong Object,ulong Control);
    internal sealed record TypeRow(ulong Vtable,uint Descriptor,uint Col,string Name);
    internal sealed record Player(ulong Actor,ulong Id,string Type);
    private sealed record SourceMembership(ulong Header,int Limit,ulong Object,ulong Control);
    internal CurrentTypedGraph(ReadSession memory,Journal journal,Budget budget,string prefix)
    {
        this.memory=memory;this.journal=journal;this.budget=budget;this.prefix=prefix;
        ulong root=PointerAt(memory.Address(0x1642B60),true);SourceType(root,Exact(root,".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@"));
        Need(Track(root+0xD8,1,true)[0]!=0,"ROOT_INACTIVE","服務根尚未啟用。");
        ulong holder=PointerAt(root+0xD0,true);SourceType(holder,Exact(holder,".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@"));
        director=PointerAt(memory.Address(0x17E00D8),true);SourceType(director,Exact(director,".?AVDirector@cocos2d@@",0x121AC40,0x176A2A0));
        scene=PointerAt(director+0x150,true);SourceType(scene,Exact(scene,".?AVScene@cocos2d@@",0x121D980,0x1660718));
        byte[] owner=Track(scene+0x190,16,true);Need(U64(owner)==0&&U64(owner,8)==director,"SCENE_OWNER","Scene 所屬關係不符。");
        journal.RequireScene(director,scene);subsystemHeader=holder+0x180;subs=Vector(subsystemHeader,128);
    }
    internal void Need(bool value,string code,string detail){if(!value)throw new ProbeFailure(prefix+"_"+code,detail);}
    internal void Pointer(ulong address)=>Need(address>=0x10000&&address<=0x7FFFFFFFF000UL&&(address&7)==0,"POINTER","必要的結構指標格式不符。");
    private byte[] Read(ulong at,int count)
    {
        budget.Check();Need(count is >0 and <=4096&&at>=0x10000&&at<=0x7FFFFFFFFFFFUL-(ulong)count&&reads++<10000&&(bytes+=count)<=1024*1024,"BOUNDS","資料組超過讀取界限。");
        if(at>=memory.Image&&at-memory.Image<=(ulong)memory.ImageSize-(uint)count)return memory.Required((uint)(at-memory.Image),count);
        if(!memory.Pointed(at,count,out var result))throw new ProbeFailure(prefix+"_READ","必要的有界資料未完整讀取。",memory.LastNativeError);
        return result;
    }
    private byte[] ReadProof(ulong at,int count)=>metadataProofs.Contains((at,count))?memory.MetaPointed(at,count):Read(at,count);
    internal byte[] Track(ulong at,int count,bool source=false,bool metadata=false)
    {
        budget.Check();byte[] current=metadata?memory.MetaPointed(at,count):Read(at,count);var key=(at,count);
        if(metadata)metadataProofs.Add(key);
        if(proofs.TryGetValue(key,out var prior))Need(current.AsSpan().SequenceEqual(prior),"CHANGED","資料在同輪讀取中改變，捨棄結果。");
        else {Need(proofs.Count<5000,"PROOFS","來源證據超界。");proofs.Add(key,current);}
        if(source)sourceProofs.Add(key);return current;
    }
    internal byte[] Meta(ulong at,int count,bool source=false)
    {Need(count>0&&at>=memory.Image&&at-memory.Image<=(ulong)memory.ImageSize-(uint)count,"META_RANGE","RTTI／程式碼超出 LC 模組。");return Track(at,count,source,true);}
    internal ulong PointerAt(ulong at,bool source=false){ulong result=U64(Track(at,8,source));Pointer(result);return result;}
    internal TypeRow? Type(ulong obj)
    {
        Pointer(obj);ulong vt=U64(Track(obj,8));
        if(types.TryGetValue(vt,out var old))return old;
        Need(types.Count<2048,"TYPE_LIMIT","型別數量超界。");
        int metadataCheckpoint=memory.MetadataCheckpoint();
        var candidateProofs=new List<(ulong At,int Count)>();
        byte[] CandidateMeta(ulong at,int count)
        {
            bool existing=proofs.ContainsKey((at,count));var bytes=Meta(at,count);
            if(!existing)candidateProofs.Add((at,count));return bytes;
        }
        TypeRow? Reject()
        {
            memory.RollbackMetadataCandidate(metadataCheckpoint);
            // Unaccepted candidate metadata must not be restaged by local closure.
            // Its fresh object vptr and all other dynamic proofs remain in the graph.
            foreach(var key in candidateProofs){proofs.Remove(key);metadataProofs.Remove(key);}
            return types[vt]=null;
        }
        if(vt<memory.Image+8||vt-memory.Image>(ulong)memory.ImageSize-16)return Reject();
        ulong col=U64(CandidateMeta(vt-8,8));
        if(col<memory.Image||(col&3)!=0||col-memory.Image>(ulong)memory.ImageSize-24)return Reject();
        byte[] c=CandidateMeta(col,24);uint td=U32(c,12),hier=U32(c,16);
        if(U32(c)!=1||U32(c,4)!=0||U32(c,8)!=0||U32(c,20)!=col-memory.Image||td>(uint)memory.ImageSize-224||hier>(uint)memory.ImageSize-16)return Reject();
        ulong typeInfo=U64(CandidateMeta(memory.Image+td,8));
        if(typeInfo<memory.Image||typeInfo-memory.Image>(ulong)memory.ImageSize-8)return Reject();
        var letters=new List<byte>();bool terminated=false;
        for(uint offset=0;offset<192&&!terminated;offset+=8)
            foreach(byte b in CandidateMeta(memory.Image+td+16+offset,8)){if(b==0){terminated=true;break;}letters.Add(b);}
        if(!terminated||letters.Count<6||letters.Any(b=>b is <33 or >126))return Reject();
        string name=Encoding.ASCII.GetString(letters.ToArray());
        if(!name.StartsWith(".?A",StringComparison.Ordinal)||!name.EndsWith("@@",StringComparison.Ordinal))return Reject();
        bool delegateRoot=name is ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@" or ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@";
        byte[] slots=CandidateMeta(vt,16);
        if(ReadOnlyUiSubsystemContract.Supports(name))
        {
            if(!memory.Profile.TypeBindings.TryGetValue(name,out var binding)||
                vt!=memory.Image+binding.Vtable||col!=memory.Image+binding.Col||td!=binding.Descriptor)return Reject();
            byte[] FreshMetadata(ulong at,int count)
            {
                bool existing=proofs.ContainsKey((at,count));var bytes=Track(at,count);
                if(!existing)candidateProofs.Add((at,count));return bytes;
            }
            try{subsystemProofs.Add(ReadOnlyUiSubsystemContract.Inspect(memory,
                new(binding.Vtable,binding.Descriptor,binding.Col),FreshMetadata,budget.Check,name));}
            catch(InvalidDataException){return Reject();}
        }
        else if(!delegateRoot&&!memory.Executable(U64(slots))&&!memory.Executable(U64(slots,8)))
        {
            // Only explicitly proven primary control types use this four-slot
            // layout. Every address must come from this profile's independently
            // relocated TD/COL/vtable anchors; no SHA or old-RVA fallback.
            if(!RelocationControlSlots.IsLateSlotType(name))return Reject();
            // Dynamic control types have no invented old-RVA symbol: keep the
            // exact discovered TD/COL/vtable triple with the confirmed profile.
            if(!memory.Profile.TypeBindings.TryGetValue(name,out var binding)||
                vt!=memory.Image+binding.Vtable||col!=memory.Image+binding.Col||td!=binding.Descriptor)return Reject();
            byte[] fourSlots=CandidateMeta(vt,32);
            if(!RelocationControlSlots.Valid(name,fourSlots,memory.Image,(uint)memory.ImageSize,memory.Executable))return Reject();
        }
        return types[vt]=new(vt,td,(uint)(col-memory.Image),name);
    }
    // Optional uint arguments are baseline symbols; returned TypeRow always contains loaded actual addresses.
    internal TypeRow Exact(ulong obj,string name,uint? vtable=null,uint? descriptor=null,uint? col=null)
    {
        var type=Type(obj);Need(type is not null&&type.Name==name&&(!vtable.HasValue||type.Vtable==memory.Address(vtable.Value))&&
            (!descriptor.HasValue||type.Descriptor==memory.Rva(descriptor.Value))&&(!col.HasValue||type.Col==memory.Rva(col.Value)),"TYPE","必要型別的 current COL／TD／vtable／完整名稱不符："+name);
        return type!;
    }
    internal void Control(ulong control)
    {
        Pointer(control);ulong vt=U64(Track(control,8));Need(vt>=memory.Image&&vt-memory.Image<(ulong)memory.ImageSize,"CONTROL_TYPE","shared_ptr 控制型別不在 LC。");
        var refs=Read(control+8,8);Need(I32(refs)>0&&I32(refs,4)>0,"CONTROL_REFS","shared_ptr 已失去有效參照。");controls.Add(control);
    }
    internal List<Pair> Vector(ulong at,int limit,bool source=false)
    {
        var h=Track(at,24,source);ulong begin=U64(h),end=U64(h,8),cap=U64(h,16);
        if(begin==0&&end==0&&cap==0)return [];
        Pointer(begin);Pointer(end);Pointer(cap);
        Need(begin<=end&&end<=cap&&(end-begin)%16==0&&(cap-begin)%16==0&&(end-begin)/16<=(ulong)limit,"VECTOR","shared_ptr 集合超界或不完整。");
        var result=new List<Pair>();
        for(ulong offset=0;offset<end-begin;offset+=4096)
        {
            byte[] data=Track(begin+offset,(int)Math.Min(4096,end-begin-offset),source);
            for(int i=0;i<data.Length;i+=16)
            {
                ulong obj=U64(data,i),control=U64(data,i+8);if(obj==0){Need(control==0,"NULL_PAIR","空物件帶有非空控制指標。");continue;}
                Pointer(obj);Pointer(control);
                Need(!ownership.TryGetValue(obj,out var previous)||previous==control,"OWNERSHIP","同物件的 shared_ptr 控制來源衝突。");ownership[obj]=control;
                result.Add(new(obj,control));
            }
        }
        return result.Distinct().ToList();
    }
    internal ulong Subsystem(string name,uint? vtable=null,uint? descriptor=null)
    {
        var candidates=subs.Where(p=>Type(p.Object)?.Name==name).ToArray();Need(candidates.Length==1,"SUBSYSTEM","必要服務型別不存在或不唯一："+name);
        var found=candidates[0];Exact(found.Object,name,vtable,descriptor);Control(found.Control);return found.Object;
    }
    internal List<Player> Players()
    {
        if(actors is null)
        {
            if(actorSubsystem==0)actorSubsystem=Subsystem(".?AVActorSubsystem@lineage@@",0x1162EB8,0x1643C28);
            var entries=new List<Pair>();
            foreach(ulong offset in new ulong[]{0x8A8,0x8C0,0x8D8})
            {var vector=Vector(actorSubsystem+offset,ActorCollectionLimits.MaximumSlots);actorVectors.Add((actorSubsystem+offset,vector));entries.AddRange(vector);}
            actors=entries.Distinct().ToList();Need(actors.Count<=ActorCollectionLimits.MaximumObjects,"ACTORS",$"合併角色集合超過 {ActorCollectionLimits.MaximumObjects}，不截斷。");
        }
        var result=new List<Player>();
        foreach(var actor in actors)
        {
            var type=Type(actor.Object);if(type?.Name is not(".?AVMyPlayer@lineage@@" or ".?AVPlayer@lineage@@"))continue;
            Control(actor.Control);ulong getter=U64(Meta(type.Vtable+8,8));
            Need(Meta(getter,5).AsSpan().SequenceEqual(new byte[]{0x48,0x8B,0x41,0x18,0xC3}),"ID_GETTER","完整角色 ID getter 不符。");
            ulong id=U64(Track(actor.Object+0x18,8));Need(id is not(0 or ulong.MaxValue),"PLAYER_ID","角色 ID 無效。");
            result.Add(new(actor.Object,id,type.Name));
        }
        return result;
    }
    internal Player Self()
    {
        ulong data=Subsystem(".?AVCharacterDataSubsystem@lineage@@",0x1169730,0x1643CF0);
        ulong id=U64(Track(data+0x6D8,8,true));Need(id is not(0 or ulong.MaxValue),"SELF_ID","自身 ID 無效。");
        // Keep complete subsystem discovery/uniqueness proofs. Only the bounded
        // actor lookup introduced below is temporary; prior callers of Players()
        // keep their full collection evidence for their own projected fields.
        if(actorSubsystem==0)actorSubsystem=Subsystem(".?AVActorSubsystem@lineage@@",0x1162EB8,0x1643C28);
        var beforeActorProofs=proofs.Keys.ToHashSet();
        var beforeActorControls=controls.ToHashSet();
        var beforeActorOwnership=ownership.Keys.ToHashSet();
        var found=Players().Where(p=>p.Id==id&&p.Type==".?AVMyPlayer@lineage@@").ToArray();Need(found.Length==1,"SELF_UNIQUE","自身 MyPlayer 與 ID 不唯一。");
        var selectedMemberships=actorVectors.SelectMany(vector=>vector.Entries
            .Where(p=>p.Object==found[0].Actor)
            .Select(p=>new SourceMembership(vector.Header,ActorCollectionLimits.MaximumSlots,p.Object,p.Control))).Distinct().ToArray();
        Need(selectedMemberships.Length>0,"SELF_MEMBERSHIP","自身沒有可重新核對的選中成員資格。");
        CloseAndDiscardActorDiscovery(beforeActorProofs,beforeActorControls,beforeActorOwnership);
        // Never expose the discarded actor snapshot through a later Players()
        // call (Party uses it after Self). Its next lookup must be fresh and its
        // complete collection proofs are then retained through ordinary Close.
        actors=null;actorVectors.Clear();
        selectedSelf=found[0];
        SourceSubsystem(data);SourceSubsystem(actorSubsystem);
        var selfType=Exact(selectedSelf.Actor,selectedSelf.Type,descriptor:0x1644900);SourceType(selectedSelf.Actor,selfType);
        Need(U64(Track(selectedSelf.Actor+0x18,8,true))==id,"SELF_ID_REBIND","選中 MyPlayer 的 ID 已不同於 CharacterData ID。");
        ulong getter=U64(Meta(selfType.Vtable+8,8));Meta(getter,5,true);
        foreach(var member in selectedMemberships)
        {
            Need(!ownership.TryGetValue(member.Object,out ulong priorOwner)||priorOwner==member.Control,"SELF_OWNERSHIP","選中自身的控制来源衝突。");
            ownership[member.Object]=member.Control;SourceControl(member.Control);sourceMemberships.Add(member);
        }
        VerifySource();
        journal.BindSelf(id);return found[0];
    }
    private void CloseAndDiscardActorDiscovery(HashSet<(ulong At,int Count)> priorProofs,HashSet<ulong> priorControls,HashSet<ulong> priorOwnership)
    {
        // First close the complete short discovery window: uniqueness was
        // determined from these rows, so a mixed/incomplete lookup is rejected.
        // Immutable metadata remains in the ordinary proof/cache transaction.
        var temporary=proofs.Keys.Where(key=>!priorProofs.Contains(key)&&!sourceProofs.Contains(key)&&!metadataProofs.Contains(key)).ToArray();
        var temporaryControls=controls.Where(control=>!priorControls.Contains(control)&&!sourceControls.Contains(control)).ToArray();
        VerifySource();
        foreach(var key in temporary)
            Need(Read(key.At,key.Count).AsSpan().SequenceEqual(proofs[key]),"ACTOR_DISCOVERY_CHANGED","自身候選的短查找窗內集合／物件改變，不能確認唯一性。");
        foreach(ulong control in temporaryControls)
        {
            var refs=Read(control+8,8);
            Need(I32(refs)>0&&I32(refs,4)>0,"ACTOR_DISCOVERY_CONTROL","自身查找窗內的候選控制物件已失效。");
        }
        VerifySource();
        foreach(var key in temporary)proofs.Remove(key);
        foreach(ulong control in temporaryControls)controls.Remove(control);
        foreach(ulong obj in ownership.Keys.Where(obj=>!priorOwnership.Contains(obj)).ToArray())ownership.Remove(obj);
    }
    internal ulong Component(ulong actor,string name,uint? vtable=null,uint? descriptor=null)
    {
        var entries=Vector(actor+0x20,64);var found=entries.Where(p=>Type(p.Object)?.Name==name).ToArray();
        Need(found.Length==1,"COMPONENT","所選角色元件不存在或不唯一："+name);
        var type=Exact(found[0].Object,name,vtable,descriptor);Control(found[0].Control);
        Need(U64(Track(found[0].Object+8,8))==actor,"COMPONENT_OWNER","元件所屬角色不符。");
        // Only components belonging to the selected self are global identity anchors.
        if(selectedSelf?.Actor==actor)
        {
            SourceType(found[0].Object,type);SourceControl(found[0].Control);Track(found[0].Object+8,8,true);
            sourceMemberships.Add(new(actor+0x20,64,found[0].Object,found[0].Control));
        }
        return found[0].Object;
    }
    private void SourceType(ulong obj,TypeRow type)
    {
        // Promote only explicitly selected identity metadata. Discovery types and other
        // actors stay in the local field proof set even if they share this vtable.
        Track(obj,8,true);Meta(type.Vtable-8,8,true);Meta(memory.Image+type.Col,24,true);
        Meta(memory.Image+type.Descriptor,8,true);Meta(type.Vtable,16,true);
        int length=Encoding.ASCII.GetByteCount(type.Name)+1;
        for(int offset=0;offset<length;offset+=8)Meta(memory.Image+type.Descriptor+16+(uint)offset,8,true);
    }
    private void SourceControl(ulong control){Control(control);Track(control,8,true);sourceControls.Add(control);}
    private void SourceSubsystem(ulong obj)
    {
        var member=subs.Single(p=>p.Object==obj);var type=Type(obj);
        Need(type is not null,"SOURCE_TYPE","自身所需服務的型別未確認。");
        SourceType(obj,type!);SourceControl(member.Control);
        sourceMemberships.Add(new(subsystemHeader,128,member.Object,member.Control));
    }
    internal void Close()
    {
        VerifySource();
        try
        {
            foreach(var proof in proofs)Need(ReadProof(proof.Key.At,proof.Key.Count).AsSpan().SequenceEqual(proof.Value),"CLOSURE","本資料組的集合、型別或欄位在結尾核對改變。");
            foreach(ulong control in controls){var refs=Read(control+8,8);Need(I32(refs)>0&&I32(refs,4)>0,"CONTROL_CLOSURE","本資料組的控制物件已失效。");}
        }
        catch(ProbeFailure error)when(error.Code.StartsWith(prefix+"_",StringComparison.Ordinal))
        {
            // A local actor/party/map change stays local, but an accompanying real
            // self or scene change must still revoke the whole sampling cycle.
            VerifySource();throw;
        }
        VerifySource();journal.Write(prefix+"_METADATA_MODE",new{MetadataEvidence=memory.MetadataEvidence,DynamicSourcesAndValuesFresh=true});
    }
    private void VerifySource()
    {
        try
        {
            foreach(var proof in subsystemProofs)proof.CloseLocal(Read,at=>ReadOnlyUiSubsystemContract.Query(memory,at),budget.Check);
            foreach(var key in sourceProofs)VerifySourceProof(key,"根、Scene 或已選自身身分改變。");
            foreach(var member in sourceMemberships.Distinct())VerifyMembership(member);
            foreach(ulong control in sourceControls){var refs=Read(control+8,8);SourceNeed(I32(refs)>0&&I32(refs,4)>0,"自身／必要根控制物件已失效。");}
            // Recheck the selected source after the bounded membership lookup.
            foreach(var key in sourceProofs)VerifySourceProof(key,"選中成員核對後根、Scene 或自身已改變。");
            journal.RequireScene(director,scene);memory.Verify();
        }
        catch(ProbeFailure error)when(error.Code.StartsWith(prefix+"_",StringComparison.Ordinal))
        {throw new ProbeFailure("GROUP_SOURCE_CHANGED","無法完整重新確認已選自身／根／Scene 的來源。",error.NativeError);}
    }
    private void VerifySourceProof((ulong At,int Count) key,string message)
    {
        var current=ReadProof(key.At,key.Count);
        if(current.AsSpan().SequenceEqual(proofs[key]))return;
        throw new ProbeFailure("GROUP_SOURCE_CHANGED",$"{message} Group={prefix}; Address=0x{key.At:X}; Bytes={key.Count}; Metadata={metadataProofs.Contains(key)}; Before={Convert.ToHexString(proofs[key])}; After={Convert.ToHexString(current)}");
    }
    private static void SourceNeed(bool condition,string message){if(!condition)throw new ProbeFailure("GROUP_SOURCE_CHANGED",message);}
    private void VerifyMembership(SourceMembership selected)
    {
        // A fresh bounded lookup allows unrelated neighbors to enter, leave or reorder.
        // Only the selected object/control must still be uniquely owned by this vector.
        int attempts=SelectedMembershipVerifier.Verify(Read,Pointer,selected.Header,selected.Limit,selected.Object,selected.Control);
        if(attempts>1)journal.Write("SOURCE_MEMBERSHIP_RECOVERED",new{Group=prefix,Attempts=attempts,
            CollectionHeader=$"0x{selected.Header:X}",SelectedObject=$"0x{selected.Object:X}",
            SelectedObjectControlClosed=true,WholeSourceFenceStillRequired=true});
    }
    internal void AttachSourceClosure(){var previous=journal.SourceClosure;journal.SourceClosure=()=>{previous?.Invoke();VerifySource();};}
    internal static ulong U64(byte[] bytes,int offset=0)=>BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    internal static uint U32(byte[] bytes,int offset=0)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    internal static int I32(byte[] bytes,int offset=0)=>BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
}
