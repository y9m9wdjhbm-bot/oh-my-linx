using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MemoryHub.Reader;

/// <summary>Profile-bound typed structural candidates. Symbolic anchors are resolved once; loaded RTTI remains actual.</summary>
internal static class ActorFieldsReader
{
    private sealed record Pair(ulong Object,ulong Control);
    private sealed record Actor(Pair Owner,ulong Id,string Kind,string LocalIdentity,string RawType);
    private sealed record ActorItem(ulong Id,string Kind,string LocalIdentity);
    private sealed record Position(int X,int Y);
    private sealed record NameItem(ulong Id,string LocalIdentity,string? Name,string State,string Detail,DateTimeOffset SampledAt);
    private sealed record PositionItem(ulong Id,string LocalIdentity,Position? Position,string State,string Detail,DateTimeOffset SampledAt);
    private sealed record ShopStateItem(ulong Id,string LocalIdentity,string? ActionType,string ShopState,
        DateTimeOffset ShopStateSampledAt,string ShopStateEvidence,string ShopStateDetail);
    private sealed record NearbyItem(ulong? Id,string LocalIdentity,string Kind,string RawType,
        string? Name,string NameState,Position? Position,string PositionState,string State,string Detail,DateTimeOffset SampledAt);
    private sealed record TypeData(ulong Vtable,uint Col,uint Descriptor,string Name,Dictionary<(ulong At,int Size),byte[]> Metadata);
    private readonly record struct LeafCostStart(long Timestamp,long ReadCalls,long ReadBytes,int ReadFailures);
    // CaptureOnce-owned aggregate diagnostics. No actor identities or leaf values are retained.
    private sealed class LeafCost(string phase)
    {
        private long count,readCalls,readBytes,readFailures,unknownResults,absentResults,exceptions;
        private double elapsedMs;
        internal LeafCostStart Begin(ReadSession memory)
        {
            count++;
            return new(System.Diagnostics.Stopwatch.GetTimestamp(),memory.ReadCalls,memory.RequestedBytes,memory.ReadFailures);
        }
        internal void End(ReadSession memory,LeafCostStart start,bool completed,bool unknown=false,bool absent=false)
        {
            elapsedMs+=System.Diagnostics.Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds;
            readCalls+=memory.ReadCalls-start.ReadCalls;
            readBytes+=memory.RequestedBytes-start.ReadBytes;
            readFailures+=memory.ReadFailures-start.ReadFailures;
            if(!completed)exceptions++;
            else if(unknown)unknownResults++;
            else if(absent)absentResults++;
        }
        internal object Snapshot()=>new{Phase=phase,Count=count,ElapsedMs=elapsedMs,ReadCalls=readCalls,
            ReadBytes=readBytes,ReadFailures=readFailures,UnknownResults=unknownResults,AbsentResults=absentResults,
            Exceptions=exceptions,FailureCount=unknownResults+exceptions};
    }
    private const string RootName=".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@";
    private const string HolderName=".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@";
    private const string ActorSystemName=".?AVActorSubsystem@lineage@@";
    private const string CharacterName=".?AVCharacterDataSubsystem@lineage@@";
    private static readonly byte[] Getter=[0x48,0x8B,0x41,0x18,0xC3];

    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        if(!selection.EnabledKeys.Any(k=>k is "actors" or "actorNames" or "actorPositions" or "playerShopStates"||
            NearbyActorClassification.IsNearbyKey(k)))return;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var sourceClosure=journal.SourceClosure;
        ActorCollectionRetry.Run(()=>CaptureOnce(memory,journal,budget,selection),
            ()=>{memory.Verify();sourceClosure?.Invoke();budget.Check();},memory.MetadataCheckpoint,memory.RollbackMetadataCandidate,
            ()=>timer.Elapsed.TotalMilliseconds,()=>budget.RemainingMilliseconds,
            entry=>journal.Write(entry.Phase,entry));
    }

    private static void CaptureOnce(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        bool wantActors=selection.Enabled("actors"),wantNames=selection.Enabled("actorNames"),wantPositions=selection.Enabled("actorPositions"),wantOwnName=selection.Enabled("characterName");
        bool wantShopStates=selection.Enabled("playerShopStates");
        var nearbyKeys=NearbyActorClassification.Keys.Where(selection.Enabled).ToArray();
        if(!wantActors&&!wantNames&&!wantPositions&&!wantOwnName&&!wantShopStates&&nearbyKeys.Length==0)return;
        bool allActors=wantActors||wantNames||wantPositions;
        bool WantNearby(string kind)=>NearbyActorClassification.FieldForKind(kind) is {} key&&selection.Enabled(key);
        bool WantPlayerState(string kind)=>wantShopStates&&kind=="Player";
        var started=DateTimeOffset.UtcNow;var reader=new Reader(memory,budget,journal.Write);var core=reader.Evidence();
        var costTimer=System.Diagnostics.Stopwatch.StartNew();
        string phase="Roots",outcome="Failed",failureCode="";int objects=0,selectedObjects=0;
        long phaseReads=memory.ReadCalls,phaseBytes=memory.RequestedBytes;int phaseFailures=memory.ReadFailures;
        double phaseStarted=0;var costs=new List<object>();
        var nameCost=new LeafCost("Name");var positionCost=new LeafCost("Position");var shopStateCost=new LeafCost("ShopState");
        void Mark(string next)
        {
            double now=costTimer.Elapsed.TotalMilliseconds;
            costs.Add(new{Phase=phase,ElapsedMs=now-phaseStarted,ReadCalls=memory.ReadCalls-phaseReads,
                ReadBytes=memory.RequestedBytes-phaseBytes,ReadFailures=memory.ReadFailures-phaseFailures});
            phase=next;phaseStarted=now;phaseReads=memory.ReadCalls;phaseBytes=memory.RequestedBytes;phaseFailures=memory.ReadFailures;
        }
        try
        {
        var crossSource=new HashSet<(ulong At,int Size)>();
        byte[] Source(ulong at,int size){crossSource.Add((at,size));return core.Track(at,size);}
        ulong director=U64(Source(memory.Address(0x17E00D8),8));reader.RequireType(director,".?AVDirector@cocos2d@@",core,0x121AC40);
        ulong scene=U64(Source(director+0x150,8));reader.RequireType(scene,".?AVScene@cocos2d@@",core,0x121D980);
        var links=Source(scene+0x190,16);Need(U64(links)==0&&U64(links,8)==director,"GROUP_SOURCE_CHANGED","角色欄位場景所屬不符");
        journal.RequireScene(director,scene);
        ulong root=U64(Source(memory.Address(0x1642B60),8));reader.RequireType(root,RootName,core,0x11672A0);
        Need(Source(root+0xD8,1)[0]!=0,"ACTOR_ROOT_NOT_READY","角色服務尚未就緒");
        ulong holder=U64(Source(root+0xD0,8));reader.RequireType(holder,HolderName,core);
        // Root object type proofs belong to identity; later normal actor collection changes do not.
        foreach(var p in core.Proofs.Keys)crossSource.Add(p);
        var subsystemEvidence=reader.Evidence();var systems=reader.Vector(holder+0x180,128,subsystemEvidence);
        ulong actors=0,character=0;
        foreach(var pair in systems)
        {
            var type=reader.Type(pair.Object,subsystemEvidence);
            if(type.Name==ActorSystemName)
            {Need(actors==0||actors==pair.Object,"ACTOR_SYSTEM_AMBIGUOUS","ActorSubsystem 不唯一");Need(type.Vtable==memory.Address(0x1162EB8),"ACTOR_SYSTEM_TYPE","ActorSubsystem 新版 vtable 不符");actors=pair.Object;}
            if(type.Name==CharacterName)
            {Need(character==0||character==pair.Object,"ACTOR_CHARACTER_AMBIGUOUS","CharacterDataSubsystem 不唯一");Need(type.Vtable==memory.Address(0x1169730),"ACTOR_CHARACTER_TYPE","CharacterDataSubsystem 新版 vtable 不符");character=pair.Object;}
        }
        Pointer(actors);Pointer(character);subsystemEvidence.Verify("ACTOR_SUBSYSTEM_CHANGED");
        core.Merge(subsystemEvidence);foreach(var p in subsystemEvidence.Proofs.Keys)crossSource.Add(p);
        ulong ownId=U64(Source(character+0x6D8,8));Need(ownId is >0 and <ulong.MaxValue,"ACTOR_SELF_UNKNOWN","目前自身 key 尚未就緒");
        if(journal.SelfId is {} expected)Need(expected==ownId,"GROUP_SOURCE_CHANGED","角色欄位與本輪自身 ID 不符");

        Mark("Membership");
        var rows=new Dictionary<ulong,Pair>();
        foreach(ulong offset in new ulong[]{0x8A8,0x8C0,0x8D8})
        {
            foreach(var pair in reader.Vector(actors+offset,ActorCollectionLimits.MaximumSlots,core,orderInsensitiveMembership:true))
            {
                if(rows.TryGetValue(pair.Object,out var existing))Need(existing.Control==pair.Control,"ACTOR_OWNER_ALIAS","角色集合的控制物件所屬衝突");
                else rows.Add(pair.Object,pair);
                ActorCollectionLimits.RequireObjectCount(rows.Count);
            }
        }
        objects=rows.Count;Mark("Identity");
        var selected=new List<Actor>();var ids=new HashSet<ulong>();int otherKinds=0;
        var unknownPairs=new List<(Pair Pair,TypeData Type)>();
        foreach(var pair in rows.Values)
        {
            var type=reader.Type(pair.Object,core);
            string? kind=NearbyActorClassification.KnownKind(type.Name);
            if(kind is null)
            {
                otherKinds++;
                if(selection.Enabled("nearbyUnknown"))unknownPairs.Add((pair,type));
                continue;
            }
            if(!allActors&&kind!="MyPlayer"&&!WantNearby(kind)&&!WantPlayerState(kind))continue;
            ulong getter=U64(core.Meta(type.Vtable+8,8));
            Need(memory.Executable(getter),"ACTOR_GETTER_PAGE","角色 ID getter 不在目前映像可執行頁");
            Need(core.Meta(getter,Getter.Length).AsSpan().SequenceEqual(Getter),"ACTOR_GETTER_CODE","角色完整 ID getter 需重新確認");
            ulong id=U64(core.Track(pair.Object+0x18,8,"ActorId"));Need(id is >0 and <ulong.MaxValue,"ACTOR_ID_INVALID","角色 ID 無效");
            if(!allActors&&id!=ownId&&!WantNearby(kind)&&!WantPlayerState(kind))continue;
            Need(ids.Add(id),"ACTOR_ID_AMBIGUOUS","不同角色共用同一 ID，本輪物件身份不唯一");
            string identity=Opaque(memory,director,scene,pair,id);
            selected.Add(new(pair,id,kind,identity,type.Name));
            ActorCollectionLimits.RequireSelectedCount(selected.Count);
        }
        var self=selected.Where(a=>a.Kind=="MyPlayer"&&a.Id==ownId).ToArray();
        Need(self.Length==1,"ACTOR_SELF_UNRESOLVED","全集合未找到唯一同 key 自身角色");
        selectedObjects=selected.Count;Mark("MembershipRecheck");
        core.VerifyUnorderedVectors(reader.Read,budget.Check,
            (address,length)=>journal.Write("ACTOR_MEMBERSHIP_EARLY_CHANGED",new{Address=$"0x{address:X}",Length=length,Objects=objects,Selected=selectedObjects,ElapsedMs=costTimer.Elapsed.TotalMilliseconds,LeafReadsSkipped=true}));
        ActorMembershipFence.Verify(core.Proofs.Where(p=>(!core.IsUnorderedMember(p.Key)||crossSource.Contains(p.Key))&&(core.Category(p.Key) is "VectorHeader" or "VectorMembers"))
            .Select(p=>(p.Key.At,p.Value)),reader.Read,budget.Check,
            (address,length)=>journal.Write("ACTOR_MEMBERSHIP_EARLY_CHANGED",new{Address=$"0x{address:X}",Length=length,
                Objects=objects,Selected=selectedObjects,ElapsedMs=costTimer.Elapsed.TotalMilliseconds,LeafReadsSkipped=true}));
        Mark("Leaves");
        var names=new List<NameItem>();var positions=new List<PositionItem>();NameItem? ownName=null;
        var shopStates=new List<ShopStateItem>();
        var nearby=nearbyKeys.ToDictionary(k=>k,_=>new List<NearbyItem>());
        void ReadActorLeaves(Actor actor)
        {
            bool isNearby=actor.Id!=ownId&&WantNearby(actor.Kind);
            bool name=wantNames||wantOwnName&&actor.Id==ownId||isNearby;
            NameItem? observedName=null;PositionItem? observedPosition=null;
            if(name)
            {
                var leafStart=nameCost.Begin(memory);
                try{observedName=ReadName(reader,actor);}
                finally{nameCost.End(memory,leafStart,observedName is not null,observedName?.State=="Unknown",observedName?.State=="Absent");}
                if(wantNames)names.Add(observedName);
                if(actor.Id==ownId)ownName=observedName;
            }
            if(wantPositions||isNearby)
            {
                var leafStart=positionCost.Begin(memory);
                try{observedPosition=ReadPosition(reader,actor);}
                finally{positionCost.End(memory,leafStart,observedPosition is not null,observedPosition?.State=="Unknown");}
                if(wantPositions)positions.Add(observedPosition);
            }
            // This is a separately selectable leaf. Nearby/name/position reads
            // alone never dereference ActionComponent+C0.
            if(actor.Id!=ownId&&WantPlayerState(actor.Kind))
            {
                ShopStateItem? observedShopState=null;var leafStart=shopStateCost.Begin(memory);
                try{observedShopState=ReadShopState(reader,actor);}
                finally{shopStateCost.End(memory,leafStart,observedShopState is not null,observedShopState?.ShopState=="Unknown");}
                shopStates.Add(observedShopState);
            }
            if(isNearby)
            {
                string state=observedName?.State=="Observed"&&observedPosition?.State=="Observed"?"ObservedCandidate":"Partial";
                var sampledAt=observedName!.SampledAt<observedPosition!.SampledAt?observedName.SampledAt:observedPosition.SampledAt;
                nearby[NearbyActorClassification.FieldForKind(actor.Kind)!].Add(new(actor.Id,actor.LocalIdentity,actor.Kind,actor.RawType,
                    observedName.Name,observedName.State,observedPosition.Position,observedPosition.State,state,
                    "精確 RTTI 本機集合分類候選；名稱："+observedName.Detail+"；座標："+observedPosition.Detail,sampledAt));
            }
        }
        // Full identity discovery and duplicate-ID checks above are unchanged.
        // The original complete collection/source closure below still gates publication.
        ActorDispatchLeafPlan.Run(selected, journal.DispatchTargetOwnerId, actor => actor.Id, ReadActorLeaves, () =>
        {
        var unknownResults=new List<(Pair Pair,TypeData Type,ulong? Id,string Detail,DateTimeOffset SampledAt)>();
        foreach(var unknown in unknownPairs)
        {
            var sampledAt=DateTimeOffset.UtcNow;
            var result=ReadUnknownId(memory,reader,core,unknown.Pair,unknown.Type);
            unknownResults.Add((unknown.Pair,unknown.Type,result.Id,result.Detail,sampledAt));
        }
        // Resolve ambiguity across the complete unknown sample before publishing
        // any row: a later duplicate also invalidates the first unknown row's ID.
        var ambiguousUnknownIds=NearbyActorClassification.AmbiguousUnknownIds(unknownResults.Select(x=>x.Id),ids);
        foreach(var unknown in unknownResults)
        {
            ulong? id=unknown.Id;string detail=unknown.Detail;
            if(id is {} value&&ambiguousUnknownIds.Contains(value))
            {id=null;detail="ID 與本輪已知／未知物件重複，所有相關未知物件均保留但不提供不唯一的 ID。";}
            // Optional getter availability must not change the opaque identity of
            // the same still-owned unclassified object between samples.
            nearby["nearbyUnknown"].Add(new(id,Opaque(memory,director,scene,unknown.Pair,0),"Unknown",unknown.Type.Name,
                null,"Unknown",null,"Unknown","Unclassified",detail+" 尚未驗證此類型的名稱／座標元件布局，未套用角色偏移。",unknown.SampledAt));
        }
        Mark("Closure");
        core.VerifyUnorderedVectors(reader.Read,budget.Check,
            (address,length)=>journal.Write("ACTOR_PROOF_CHANGED",new{Category="VectorMembership",Address=$"0x{address:X}",Length=length,ElapsedMs=costTimer.Elapsed.TotalMilliseconds,RemainingMs=budget.RemainingMilliseconds,Objects=objects,Selected=selectedObjects}));
        ActorFinalProofClosure.Verify(core.Proofs.Select(proof=>new ActorFinalProofClosure.Proof(proof.Key.At,proof.Value,
            crossSource.Contains(proof.Key),core.IsMetadataProof(proof.Key),core.IsUnorderedMember(proof.Key),core.Category(proof.Key))),
            core.ReadProof,budget.Check,proof=>journal.Write("ACTOR_PROOF_CHANGED",new{Category=proof.Source?"Source":proof.Category,
                Address=$"0x{proof.Address:X}",Length=proof.Expected.Length,ElapsedMs=costTimer.Elapsed.TotalMilliseconds,
                RemainingMs=budget.RemainingMilliseconds,Objects=objects,Selected=selectedObjects}));
        foreach(var key in crossSource)
            Need(core.ReadProof(key.At,key.Size).AsSpan().SequenceEqual(core.Proofs[key]),"GROUP_SOURCE_CHANGED","角色欄位最終自身／場景來源已变更");
        memory.Verify();journal.RequireScene(director,scene);journal.BindSelf(ownId);
        var captured=crossSource.Select(k=>(k.At,k.Size,Bytes:core.Proofs[k])).ToArray();
        var previous=journal.SourceClosure;
        journal.SourceClosure=()=>
        {
            previous?.Invoke();
            foreach(var p in captured)
                Need(core.ReadProof(p.At,p.Size).AsSpan().SequenceEqual(p.Bytes),"GROUP_SOURCE_CHANGED","跨資料組角色自身／場景來源已變更");
            memory.Verify();
        };
        Mark("Publication");
        journal.Write("ACTOR_METADATA_MODE",new{MetadataEvidence=memory.MetadataEvidence,DynamicSourcesAndValuesFresh=true});
        if(wantActors)journal.Set("actors",new{Complete=true,Items=selected.Select(a=>new ActorItem(a.Id,a.Kind,a.LocalIdentity)).ToArray(),OtherTypedObjects=otherKinds},started,"TypedStructureCandidate");
        if(wantNames)journal.Set("actorNames",new{Complete=true,Items=names.ToArray()},started,"TypedStructureCandidate");
        if(wantPositions)journal.Set("actorPositions",new{Complete=true,Items=positions.ToArray()},started,"TypedStructureCandidate");
        foreach(var entry in nearby)
            journal.Set(entry.Key,new{Complete=journal.DispatchTargetOwnerId is null,Items=entry.Value.ToArray(),
                Scope=journal.DispatchTargetOwnerId is null?"LocalActorCollections":"SelectedDispatchTarget",
                SelectedOwnerId=journal.DispatchTargetOwnerId,FullIdentityCollectionVerified=true,ScreenVisibilityVerified=false},
                started,"CurrentTypedActorCollections_PerItemUnknown_SemanticsCandidate");
        if(wantShopStates)
        {
            journal.Set("playerShopStates",new{Complete=journal.DispatchTargetOwnerId is null,Items=shopStates.ToArray(),
                Scope=journal.DispatchTargetOwnerId is null?"LocalActorCollections":"SelectedDispatchTarget",
                SelectedOwnerId=journal.DispatchTargetOwnerId,FullIdentityCollectionVerified=true,
                ScreenVisibilityVerified=false},started,"CurrentTypedActionComponent_DirectAction_ABClosed_PerItemUnknown");
            journal.Write("PLAYER_SHOP_STATE_SAMPLE",new{StartedAt=started,Total=shopStates.Count,
                Counts=shopStates.GroupBy(s=>s.ShopState).ToDictionary(g=>g.Key,g=>g.Count()),
                ActorVectorsEnumeratedOnce=true,UnknownIsNotNonShop=true});
        }
        if(nearby.Count>0)journal.Write("NEARBY_COLLECTION_SAMPLE",new{StartedAt=started,DistinctObjects=rows.Count,
            Counts=nearby.ToDictionary(k=>k.Key,k=>k.Value.Count),OtherTypedObjects=otherKinds,
            UnknownTypeCounts=unknownPairs.GroupBy(x=>x.Type.Name).ToDictionary(x=>x.Key,x=>x.Count()),
            ActorVectorsEnumeratedOnce=true,GroundItemContractVerified=false,ScreenVisibilityVerified=false});
        if(wantOwnName)
        {
            if(ownName is {State:"Observed",Name:not null})journal.Set("characterName",ownName.Name,ownName.SampledAt,"TypedStructureCandidate");
            else journal.Unknown("characterName",ownName?.Detail??"自身名稱尚未讀到；未以數字 ID 代替名稱");
        }
        });
        outcome="Complete";
        }
        catch(ProbeFailure error){failureCode=error.Code;throw;}
        catch(OperationCanceledException){failureCode="Cancelled";throw;}
        finally
        {
            string finalPhase=phase;Mark("End");
            journal.Write("ACTOR_CAPTURE_COST",new{StartedAt=started,Outcome=outcome,FailureCode=failureCode,FinalPhase=finalPhase,
                ElapsedMs=costTimer.Elapsed.TotalMilliseconds,RemainingMs=budget.RemainingMilliseconds,
                DispatchTargetOwnerId=journal.DispatchTargetOwnerId,LeafScope=journal.DispatchTargetOwnerId is null?"AllSelectedActors":"SelectedDispatchTarget",
                Objects=objects,Selected=selectedObjects,ProofCount=core.Proofs.Count,Stages=costs,
                LeafStages=new[]{nameCost.Snapshot(),positionCost.Snapshot(),shopStateCost.Snapshot()},
                ComponentDiscoveries=reader.ComponentDiscoveries,ComponentReuses=reader.ComponentReuses,LeafValuesReused=false,
                AtomicSnapshot=false,PartialLeafFailuresDoNotImplyCompleteCollectionFailure=true});
        }
    }

    private static (ulong? Id,string Detail) ReadUnknownId(ReadSession memory,Reader reader,Evidence core,Pair pair,TypeData type)
    {
        int checkpoint=reader.MetadataCheckpoint();
        try
        {
            var scope=reader.Evidence();
            ulong getter=U64(scope.Meta(type.Vtable+8,8));
            if(!memory.Executable(getter))
                return(null,"未分類物件的 ID getter 不在目前映像可執行頁；未讀取 object+0x18。");
            if(!scope.Meta(getter,Getter.Length).AsSpan().SequenceEqual(Getter))
                return(null,"未分類物件的 ID getter 不符合完整已知契約；未讀取 object+0x18。");
            ulong id=U64(scope.Track(pair.Object+0x18,8));
            if(id is 0 or ulong.MaxValue)return(null,"getter 形狀已核對，但此物件 ID 無效；保留物件與原始型別。");
            scope.Verify("ACTOR_UNKNOWN_ID_CHANGED");core.Merge(scope);
            return(id,"保留完整 RTTI 與本機 ID getter 的結構候選；物件業務分類尚未確認。");
        }
        catch(ProbeFailure e)when(!Fatal(e))
        {
            reader.RollbackMetadata(checkpoint);
            return(null,Detail(e)+"；保留物件，ID 未確認。");
        }
    }

    private static NameItem ReadName(Reader reader,Actor actor)
    {
        var at=DateTimeOffset.UtcNow;
        int metadataCheckpoint=reader.MetadataCheckpoint();
        try
        {
            var scope=reader.Evidence();ulong description=Component(reader,actor,".?AVDescriptionComponent@lineage@@",scope);
            if(description==0){reader.RollbackMetadata(metadataCheckpoint);return new(actor.Id,actor.LocalIdentity,null,"Unknown","缺少唯一 DescriptionComponent",at);}
            var value=ActorNameDecoder.Read(description+0x40,scope.Track,Pointer,
                leaves=>scope.Verify("ACTOR_NAME_CHANGED",leaves),$"ActorId={actor.Id};Kind={actor.Kind}");
            return new(actor.Id,actor.LocalIdentity,value.Name,value.State,value.Detail,at);
        }
        catch(ProbeFailure e)when(!Fatal(e)){reader.ItemFailure(e.Code,metadataCheckpoint);return new(actor.Id,actor.LocalIdentity,null,"Unknown",Detail(e),at);}
    }
    private static PositionItem ReadPosition(Reader reader,Actor actor)
    {
        var at=DateTimeOffset.UtcNow;
        int metadataCheckpoint=reader.MetadataCheckpoint();
        try
        {
            var scope=reader.Evidence();ulong transform=Component(reader,actor,".?AVTransformComponent@lineage@@",scope);
            if(transform==0){reader.RollbackMetadata(metadataCheckpoint);return new(actor.Id,actor.LocalIdentity,null,"Unknown","缺少唯一 TransformComponent",at);}
            var xy=scope.Track(transform+0x40,8);int x=I32(xy),y=I32(xy,4);
            Need(x is >0 and <=1_000_000&&y is >0 and <=1_000_000,"ACTOR_XY_RANGE","角色座標值超界");
            scope.Verify("ACTOR_XY_CHANGED",(transform+0x40,8));
            return new(actor.Id,actor.LocalIdentity,new(x,y),"Observed","Transform+0x40 型別與AB閉合候選；他角色座標語義待比對",at);
        }
        catch(ProbeFailure e)when(!Fatal(e)){reader.ItemFailure(e.Code,metadataCheckpoint);return new(actor.Id,actor.LocalIdentity,null,"Unknown",Detail(e),at);}
    }
    private static ShopStateItem ReadShopState(Reader reader,Actor actor)
    {
        var at=DateTimeOffset.UtcNow;int metadataCheckpoint=reader.MetadataCheckpoint();
        ShopStateItem Unknown(string detail,string? actionType=null)=>new(actor.Id,actor.LocalIdentity,actionType,
            "Unknown",at,"Unconfirmed",detail);
        try
        {
            var scope=reader.Evidence();
            ulong component=Component(reader,actor,".?AVActionComponent@lineage@@",scope);
            if(component==0){reader.RollbackMetadata(metadataCheckpoint);return Unknown("缺少唯一 ActionComponent；不能判定未擺攤。");}
            reader.RequireSimpleHierarchy(reader.Type(component,scope),".?AVActorComponentBase@lineage@@",scope);
            // Exact current-build data witness; still a direct Action pointer,
            // never interpret its following qword as a shared control block.
            uint actionOffset=reader.DirectActionOffset;
            ulong action=U64(scope.Track(component+actionOffset,8));
            if(action==0){reader.RollbackMetadata(metadataCheckpoint);return Unknown("Action 指標為空，可能是動作切換空窗；不能判定未擺攤。");}
            var type=reader.Type(action,scope);
            string state=PlayerShopStatePolicy.ClassifyExactAction(type.Name);
            if(state=="Unknown")
            {
                scope.Verify("ACTOR_SHOP_STATE_CHANGED",(component+actionOffset,8));
                return Unknown("已讀取精確 Action 型別，但尚未驗證其擺攤語義；保留未知。",type.Name);
            }
            reader.RequireSimpleHierarchy(type,".?AVActionBase@lineage@@",scope);
            scope.Verify("ACTOR_SHOP_STATE_CHANGED",(component+actionOffset,8));
            return new(actor.Id,actor.LocalIdentity,type.Name,state,at,
                "ExactPlayerId_ComponentOwner_DirectAction_PrimaryRtti_ActionBaseHierarchy_ABClosed",
                state=="ShopActionObserved"?"取樣時為 ShopAction；不保證伺服器接受開啟商店。":"取樣時為已確認的站立／移動動作，當時未觀察到擺攤動作。");
        }
        catch(ProbeFailure e)when(!Fatal(e))
        {
            // Optional state failure must not invalidate successfully closed
            // names/positions or another player's state in this collection.
            reader.RollbackMetadata(metadataCheckpoint);return Unknown(Detail(e)+"；僅此玩家狀態未知。");
        }
    }
    private static ulong Component(Reader reader,Actor actor,string exactName,Evidence scope)
        =>reader.Component(actor,exactName,scope);
    private static string Opaque(ReadSession memory,ulong director,ulong scene,Pair pair,ulong id)
    {
        string seed=$"{memory.Profile.Sha256}|{memory.ProcessId}|{memory.CreationFileTime}|{memory.Image:X}|{director:X}|{scene:X}|{pair.Object:X}|{pair.Control:X}|{id}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }
    private static bool Fatal(ProbeFailure e)=>e.Code is "TIME_LIMIT" or "READ_BYTE_LIMIT" or "CONFIGURATION_CHANGED" or "GROUP_SOURCE_CHANGED" or "PROCESS_CHANGED" or "PROCESS_EXITED" or "PROCESS_PATH_CHANGED" or "IDENTITY_API_FAILED" or "PID_CHANGED";
    internal static string ClosureChangeCode(bool source,bool metadata)=>source||metadata?"GROUP_SOURCE_CHANGED":"ACTOR_COLLECTION_CHANGED";
    private static string Detail(ProbeFailure e)=>e.Code+"："+e.Message+(e.NativeError==0?"":$"（Win32 {e.NativeError}）");
    private static ulong U64(byte[] b,int at=0)=>BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at,8));
    private static uint U32(byte[] b,int at=0)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at,4));
    private static int I32(byte[] b,int at=0)=>BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at,4));
    private static void Pointer(ulong p)=>Need(Probe.Pointer(p),"ACTOR_POINTER","角色來源指標不合法");
    private static void Need(bool condition,string code,string detail){if(!condition)throw new ProbeFailure(code,detail);}

    private sealed class Reader(ReadSession memory,Budget budget,Action<string,object> diagnostics)
    {
        private readonly Dictionary<ulong,TypeData> types=[];
        private sealed record ComponentBinding(Evidence Proofs,(ulong Object,string Name)[] Components);
        private readonly ActorCaptureCache<ComponentBinding> components=new();
        internal int ComponentDiscoveries=>components.Discoveries;
        internal int ComponentReuses=>components.Reuses;
        internal ulong Component(Actor actor,string exactName,Evidence scope)
        {
            var key=new ActorObservationKey(actor.Owner.Object,actor.Owner.Control,actor.Id);
            var binding=components.Get(key,()=>
            {
                var proof=Evidence();var identity=proof.Track(actor.Owner.Object,32);
                Need(U64(identity,0x18)==actor.Id,"ACTOR_ID_CHANGED","指定角色 ID 已變更");
                var found=new List<(ulong Object,string Name)>();
                foreach(var pair in Vector(actor.Owner.Object+0x20,64,proof))
                    found.Add((pair.Object,Type(pair.Object,proof).Name));
                return new ComponentBinding(proof,found.ToArray());
            });
            // Merge every original identity/vector/vptr/metadata proof into the
            // leaf's own scope. Its existing A/B verification rereads all of them.
            // A failed or replaced component cannot become a cached leaf result.
            scope.Merge(binding.Proofs);ulong selected=0;
            foreach(var component in binding.Components)
            {
                if(component.Name!=exactName)continue;
                Need(selected==0,"ACTOR_COMPONENT_AMBIGUOUS","指定角色元件不唯一");
                Need(U64(scope.Track(component.Object+8,8))==actor.Owner.Object,"ACTOR_COMPONENT_OWNER","指定角色元件 owner 不符");
                selected=component.Object;
            }
            return selected;
        }
        internal uint DirectActionOffset=>memory.Profile.FieldOffset("offset:ActionComponent:directAction",0xC0);
        internal Evidence Evidence()=>new(this);
        internal void Check()=>memory.Boundary();
        internal int MetadataCheckpoint()=>memory.MetadataCheckpoint();
        internal void RollbackMetadata(int checkpoint)=>memory.RollbackMetadataCandidate(checkpoint);
        internal void ItemFailure(string reason,int checkpoint)
        {
            components.Clear();
            if(reason is "ACTOR_NAME_FORMAT" or "ACTOR_NAME_NUL" or "ACTOR_NAME_UTF8" or "ACTOR_NAME_TEXT" or
                "ACTOR_NAME_CHANGED_VALUE" or "ACTOR_XY_RANGE" or "ACTOR_XY_CHANGED_VALUE")
                RollbackMetadata(checkpoint);
            else memory.DiscardMetadataCache(reason);
        }
        internal byte[] Meta(ulong at,int size){budget.Check();return memory.MetaPointed(at,size);}
        internal byte[] Read(ulong at,int size)
        {
            budget.Check();
            if(at>=memory.Image&&at-memory.Image<=(ulong)memory.ImageSize-(uint)size)return memory.Required((uint)(at-memory.Image),size);
            if(!memory.Pointed(at,size,out var bytes))throw new ProbeFailure("ACTOR_READ_FAILED","角色必要資料未完整取得",memory.LastNativeError);
            return bytes;
        }
        internal TypeData Type(ulong obj,Evidence evidence)
        {
            Pointer(obj);ulong vt=U64(evidence.Track(obj,8,"ObjectVtable"));
            Need(vt>=memory.Image+8&&vt-memory.Image<(ulong)memory.ImageSize-24&&(vt&7)==0,"ACTOR_TYPE_RANGE","角色型別不在目前載入映像");
            if(types.TryGetValue(vt,out var cached)){evidence.Remember(cached.Metadata,true);return cached;}
            Need(types.Count<1024,"ACTOR_TYPE_LIMIT","角色型別數超界");
            var meta=new Dictionary<(ulong At,int Size),byte[]>();
            byte[] Meta(ulong at,int count)
            {
                Need(at>=memory.Image&&at-memory.Image<=(ulong)memory.ImageSize-(uint)count,"ACTOR_META_RANGE","角色型別metadata超出映像");
                var bytes=evidence.Meta(at,count);meta[(at,count)]=bytes;return bytes;
            }
            ulong col=U64(Meta(vt-8,8));Need(col>=memory.Image&&col-memory.Image<(ulong)memory.ImageSize-24&&(col&3)==0,"ACTOR_COL_RANGE","角色COL超出映像");
            var c=Meta(col,24);uint td=U32(c,12),hier=U32(c,16);
            Need(U32(c)==1&&U32(c,4)==0&&U32(c,8)==0&&U32(c,20)==col-memory.Image&&td<(uint)memory.ImageSize-272&&hier<(uint)memory.ImageSize-16,
                "ACTOR_COL_LAYOUT","角色primary COL／TD關係不符");
            var nameBytes=new List<byte>();bool end=false;
            for(uint offset=0;offset<256&&!end;offset+=8)
            {
                foreach(byte b in Meta(memory.Image+td+16+offset,8))
                {if(b==0){end=true;break;}Need(b is >=33 and <=126,"ACTOR_TYPE_NAME","角色型別名稱不是ASCII RTTI");nameBytes.Add(b);}
            }
            Need(end&&nameBytes.Count>=6,"ACTOR_TYPE_NAME","角色型別名稱未有界結束");
            string name=Encoding.ASCII.GetString(nameBytes.ToArray());Need(name.StartsWith(".?",StringComparison.Ordinal),"ACTOR_TYPE_NAME","角色型別不是完整RTTI名稱");
            var result=new TypeData(vt,(uint)(col-memory.Image),td,name,meta);types.Add(vt,result);return result;
        }
        internal void RequireType(ulong obj,string name,Evidence evidence,uint? vt=null)
        {
            var actual=Type(obj,evidence);Need(actual.Name==name&&(!vt.HasValue||actual.Vtable==memory.Address(vt.Value)),"ACTOR_EXACT_TYPE","角色來源精確型別不符");
        }
        internal void RequireSimpleHierarchy(TypeData type,string baseName,Evidence scope)
        {
            byte[] Meta(uint rva,int size)
            {
                Need(rva>=4096&&rva<=(uint)memory.ImageSize-(uint)size,"ACTOR_SHOP_META_RANGE","Action 型別階層超出目前映像");
                return scope.Meta(memory.Image+rva,size);
            }
            string Name(uint descriptor)
            {
                Need(descriptor>=4096&&descriptor<(uint)memory.ImageSize-272,"ACTOR_SHOP_META_RANGE","Action 基底描述子超出目前映像");
                var bytes=new List<byte>();
                for(uint offset=0;offset<256;offset+=8)
                    foreach(byte b in Meta(checked(descriptor+16+offset),8))
                    {
                        if(b==0)return Encoding.ASCII.GetString(bytes.ToArray());
                        Need(b is >=33 and <=126,"ACTOR_SHOP_BASE_NAME","Action 基底型別名稱不是完整 ASCII RTTI");bytes.Add(b);
                    }
                throw new ProbeFailure("ACTOR_SHOP_BASE_NAME","Action 基底型別名稱未有界結束");
            }
            var col=Meta(type.Col,24);uint hierarchy=U32(col,16);var h=Meta(hierarchy,16);
            Need(U32(h)==0&&U32(h,4)==0&&U32(h,8)==2,"ACTOR_SHOP_HIERARCHY","Action 精確單繼承階層與已驗版型不同");
            var bases=Meta(U32(h,12),8);
            for(int i=0;i<2;i++)
            {
                var b=Meta(U32(bases,i*4),28);uint descriptor=U32(b);
                Need(U32(b,4)==(uint)(1-i)&&I32(b,8)==0&&I32(b,12)==-1&&I32(b,16)==0&&U32(b,20)==0x40,
                    "ACTOR_SHOP_BASE_LAYOUT","Action 基底型別的非虛擬零位移關係不符");
                Need(i==0?descriptor==type.Descriptor&&Name(descriptor)==type.Name:Name(descriptor)==baseName,
                    "ACTOR_SHOP_BASE_TYPE","Action primary／基底精確型別不符");
            }
        }
        internal IReadOnlyList<Pair> Vector(ulong at,int maxCount,Evidence evidence,bool orderInsensitiveMembership=false)
        {
            var header=evidence.Track(at,24,"VectorHeader");ulong begin=U64(header),end=U64(header,8),cap=U64(header,16);
            if(maxCount>=128)diagnostics("ACTOR_VECTOR_LAYOUT",new{Header=$"0x{at:X}",Begin=$"0x{begin:X}",End=$"0x{end:X}",CapacityEnd=$"0x{cap:X}",UsedSlots=end>=begin?(end-begin)/16:ulong.MaxValue,ReservedSlots=cap>=begin?(cap-begin)/16:ulong.MaxValue,MaximumActiveSlots=maxCount,ReadReservedStorage=false});
            var layout=ActorVectorLayout.Inspect(at,header,maxCount);
            var rows=new Dictionary<ulong,Pair>();
            for(ulong i=0;i<(ulong)layout.ActiveBytes;i+=4096)
            {
                var bytes=evidence.Track(layout.Begin+i,(int)Math.Min(4096,(ulong)layout.ActiveBytes-i),"VectorMembers");
                for(int p=0;p<bytes.Length;p+=16)
                {
                    ulong obj=U64(bytes,p),control=U64(bytes,p+8);
                    if(obj==0){Need(control==0,"ACTOR_NULL_OWNER","空角色仍帶有所屬控制物件");continue;}
                    Pointer(obj);Pointer(control);
                    if(rows.TryGetValue(obj,out var prior))Need(prior.Control==control,"ACTOR_OWNER_ALIAS","角色／元件所屬控制物件衝突");
                    else rows.Add(obj,new(obj,control));
                }
            }
            if(orderInsensitiveMembership)evidence.RegisterUnorderedVector(at,header,layout);
            return rows.Values.ToArray();
        }
    }
    private sealed class Evidence(Reader reader)
    {
        internal Dictionary<(ulong At,int Size),byte[]> Proofs{get;}=[];
        private readonly HashSet<(ulong At,int Size)> metadataProofs=[];
        private readonly Dictionary<(ulong At,int Size),string> categories=[];
        private readonly List<ActorUnorderedMembership.Snapshot> unorderedVectors=[];
        private readonly HashSet<(ulong At,int Size)> unorderedMembers=[];
        internal void RegisterUnorderedVector(ulong at,byte[] header,ActorVectorLayout layout)
        {
            var snapshot=ActorUnorderedMembership.Capture(at,header,layout,Proofs);unorderedVectors.Add(snapshot);
            foreach(var chunk in snapshot.Chunks)unorderedMembers.Add((chunk.Address,chunk.Expected.Length));
        }
        internal bool IsUnorderedMember((ulong At,int Size) key)=>unorderedMembers.Contains(key);
        internal void VerifyUnorderedVectors(Func<ulong,int,byte[]> read,Action check,Action<ulong,int> changed)
        {foreach(var vector in unorderedVectors)ActorUnorderedMembership.Verify(vector,read,check,changed);}
        internal string Category((ulong At,int Size) key)=>metadataProofs.Contains(key)?"ModuleMetadata":categories.GetValueOrDefault(key,"Structural");
        internal bool IsMetadataProof((ulong At,int Size) key)=>metadataProofs.Contains(key);
        internal byte[] ReadProof(ulong at,int size)=>metadataProofs.Contains((at,size))?reader.Meta(at,size):reader.Read(at,size);
        internal byte[] Meta(ulong at,int size)
        {
            reader.Check();
            metadataProofs.Add((at,size));
            if(Proofs.TryGetValue((at,size),out var prior))return prior;
            Need(Proofs.Count<8192,"ACTOR_PROOF_LIMIT","角色證據數超出有界限制");
            var bytes=reader.Meta(at,size);Proofs.Add((at,size),bytes);return bytes;
        }
        internal byte[] Track(ulong at,int size)=>Track(at,size,"Structural");
        internal byte[] Track(ulong at,int size,string category)
        {
            categories.TryAdd((at,size),category);
            if(Proofs.TryGetValue((at,size),out var old))return old;
            Need(Proofs.Count<8192,"ACTOR_PROOF_LIMIT","角色證據數超出有界限制");
            var bytes=reader.Read(at,size);Proofs.Add((at,size),bytes);return bytes;
        }
        internal void Remember(Dictionary<(ulong At,int Size),byte[]> values,bool metadata=false)
        {foreach(var p in values){if(metadata)metadataProofs.Add(p.Key);if(!Proofs.ContainsKey(p.Key))Proofs.Add(p.Key,p.Value);}}
        internal void Merge(Evidence other){Remember(other.Proofs);metadataProofs.UnionWith(other.metadataProofs);}
        internal void Verify(string code,params (ulong At,int Size)[] dynamicLeaves)
        {
            foreach(var p in Proofs)
            {
                bool stable=ReadProof(p.Key.At,p.Key.Size).AsSpan().SequenceEqual(p.Value);
                // Only the explicitly named value leaves are routine per-item changes.
                // A changed vptr, owner, vector or metadata still invalidates the cache.
                string changedCode=!metadataProofs.Contains(p.Key)&&dynamicLeaves.Contains(p.Key)?code+"_VALUE":code;
                Need(stable,changedCode,"角色集合／型別或該欄位於AB核對內已變更");
            }
        }
    }
}
