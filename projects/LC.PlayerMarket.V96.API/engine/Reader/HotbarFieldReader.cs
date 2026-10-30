using System.Buffers.Binary;
using System.Text;

namespace MemoryHub.Reader;

/// <summary>Read-only, bounded source and lower-bound slot observations derived from saved V11 research.</summary>
internal static class HotbarFieldReader
{
    private sealed record TypeSpec(uint Vtable,uint Col,uint Descriptor,string Name);
    private sealed record Slot(int Page,int FunctionKey,int GlobalSlot,bool Found,bool HasData,
        ulong? RawId,uint? RawType,byte? RawGate,uint? RawResourceState,string? DisplayText);
    private static readonly TypeSpec RootType=new(0x11672A0,0x1263C10,0x16AEE30,".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@");
    private static readonly TypeSpec HolderType=new(0,0x1263CF0,0x16AEF50,".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@");
    private static readonly TypeSpec HudType=new(0x11674B0,0x1263760,0x16AE830,".?AVHudSlotDataSubsystem@lineage@@");
    private static readonly TypeSpec CharacterType=new(0x1169730,0x12663F8,0x1643CF0,".?AVCharacterDataSubsystem@lineage@@");
    private static readonly TypeSpec DirectorType=new(0x121AC40,0x13456E8,0x176A2A0,".?AVDirector@cocos2d@@");
    private static readonly TypeSpec SceneType=new(0x121D980,0x1348E48,0x1660718,".?AVScene@cocos2d@@");
    private static readonly UTF8Encoding Utf8=new(false,true);

    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        bool readSlots=selection.Enabled("hotbarSlots"),readAts=selection.Enabled("atsEnabled"),readPage=selection.Enabled("hotbarPage"),readFocus=selection.Enabled("hotbarFocus");
        if(!readSlots&&!readAts&&!readPage&&!readFocus)return;
        var sampledAt=DateTimeOffset.UtcNow;
        var proofs=new Dictionary<(ulong Address,int Size),byte[]>();
        var sourceKeys=new HashSet<(ulong Address,int Size)>();
        var metadataKeys=new HashSet<(ulong Address,int Size)>();
        byte[] Read(ulong at,int size)
        {
            budget.Check();
            if(at>=memory.Image&&at-memory.Image<=(ulong)memory.ImageSize-(uint)size)return memory.Required((uint)(at-memory.Image),size);
            if(!memory.Pointed(at,size,out var value))throw new ProbeFailure("HOTBAR_READ_FAILED","快捷列資料未完整讀取",memory.LastNativeError);
            return value;
        }
        byte[] ReadProof(ulong at,int size)=>metadataKeys.Contains((at,size))?memory.MetaPointed(at,size):Read(at,size);
        byte[] Track(ulong at,int size,bool source=false,bool metadata=false)
        {
            budget.Check();Need(proofs.Count<4096,"HOTBAR_PROOF_LIMIT","快捷列有界證據數已達上限");
            var key=(at,size);if(source)sourceKeys.Add(key);if(metadata)metadataKeys.Add(key);
            if(proofs.TryGetValue(key,out var value))return value;
            value=metadata?memory.MetaPointed(at,size):Read(at,size);proofs.Add(key,value);return value;
        }
        void Exact(ulong address,TypeSpec type,bool source=true)
        {
            Pointer(address);
            ulong vt=U64(Track(address,8,source));
            Need(vt>=memory.Image+8&&vt-memory.Image<(ulong)memory.ImageSize-24&&(vt&7)==0,"HOTBAR_TYPE_RANGE","快捷列 vtable 超出相符映像");
            Need(type.Vtable==0||vt==memory.Address(type.Vtable),"HOTBAR_TYPE_VTABLE","快捷列精確 vtable 不符");
            var prefix=Track(vt-8,24,source,true);var col=Track(memory.Address(type.Col),24,source,true);
            Need(U64(prefix)==memory.Address(type.Col)&&U32(col)==1&&U32(col,4)==0&&U32(col,8)==0&&U32(col,12)==memory.Rva(type.Descriptor)&&U32(col,20)==memory.Rva(type.Col),
                "HOTBAR_TYPE_COL","快捷列完整 COL／TD 關係不符");
            byte[] expected=Encoding.ASCII.GetBytes(type.Name+'\0');
            Need(Track(memory.Address(type.Descriptor,16),expected.Length,source,true).AsSpan().SequenceEqual(expected),"HOTBAR_TYPE_NAME","快捷列完整 RTTI 名稱不符");
        }

        ulong director=U64(Track(memory.Address(0x17E00D8),8,true));Exact(director,DirectorType);
        ulong scene=U64(Track(director+0x150,8,true));Exact(scene,SceneType);
        var sceneOwner=Track(scene+0x190,16,true);
        Need(U64(sceneOwner)==0&&U64(sceneOwner,8)==director,"GROUP_SOURCE_CHANGED","快捷列場景所屬已變更");
        journal.RequireScene(director,scene);
        ulong root=U64(Track(memory.Address(0x1642B60),8,true));Exact(root,RootType);
        Need(Track(root+0xD8,1,true)[0]!=0,"HOTBAR_NOT_READY","遊戲服務尚未就緒");
        ulong holder=U64(Track(root+0xD0,8,true));Exact(holder,HolderType);
        var vector=Track(holder+0x180,24,true);
        ulong begin=U64(vector),end=U64(vector,8),capacity=U64(vector,16);
        Pointer(begin);Pointer(end);Pointer(capacity);
        Need(begin<=end&&end<=capacity&&(end-begin)%16==0&&(capacity-begin)%16==0&&(capacity-begin)/16<=128,
            "HOTBAR_SERVICES_RANGE","快捷列所屬服務集合超出128個界線");
        ulong hud=0,character=0;var owners=new Dictionary<ulong,ulong>();
        for(ulong offset=0;offset<end-begin;offset+=4096)
        {
            var rows=Track(begin+offset,(int)Math.Min(4096,end-begin-offset),true);
            for(int i=0;i<rows.Length;i+=16)
            {
                ulong obj=U64(rows,i),control=U64(rows,i+8);
                if(obj==0){Need(control==0,"HOTBAR_EMPTY_OWNER","空服務仍帶有所屬物件");continue;}
                Pointer(obj);Pointer(control);
                if(owners.TryGetValue(obj,out ulong old))Need(old==control,"HOTBAR_SERVICE_ALIAS","服務所屬控制物件衝突");
                else owners.Add(obj,control);
                ulong vt=U64(Track(obj,8));
                if(vt==memory.Address(HudType.Vtable))
                {Need(hud==0||hud==obj,"HOTBAR_AMBIGUOUS","快捷列服務不唯一");Exact(obj,HudType);hud=obj;}
                if(vt==memory.Address(CharacterType.Vtable))
                {Need(character==0||character==obj,"HOTBAR_CHARACTER_AMBIGUOUS","角色資料服務不唯一");Exact(obj,CharacterType);character=obj;}
            }
        }
        Pointer(hud);Pointer(character);
        ulong own=U64(Track(character+0x6D8,8,true));
        Need(own is >0 and <ulong.MaxValue,"HOTBAR_SELF_UNKNOWN","快捷列自身 key 未就緒");
        if(journal.SelfId is {} expectedOwn)Need(own==expectedOwn,"GROUP_SOURCE_CHANGED","快捷列與本輪自身 ID 不同");

        // Independent four-byte fields: a disabled field's adjacent bytes are not read.
        int? page=null,focus=null;
        if(readPage)
        {page=I32(Track(hud+memory.Profile.FieldOffset("offset:HudSlotDataSubsystem:page",0x40),4));Need(page is >=0 and <3,"HOTBAR_PAGE_RANGE","快捷頁值超出0至2");}
        if(readFocus)
        {focus=I32(Track(hud+memory.Profile.FieldOffset("offset:HudSlotDataSubsystem:focus",0x44),4));Need(focus is >=0 and <24,"HOTBAR_FOCUS_RANGE","快捷焦點值超出0至23");}
        var slots=new List<Slot>();
        if(readSlots||readAts)
        {
            ulong head=U64(Track(hud+memory.Profile.FieldOffset("offset:HudSlotDataSubsystem:slotsHead",0x20),8));Pointer(head);
            ulong count=U64(Track(hud+memory.Profile.FieldOffset("offset:HudSlotDataSubsystem:slotsCount",0x28),8));
            Need(count<=24,"HOTBAR_COUNT_RANGE","快捷格數量超出24個界線");
            var sentinel=Track(head,32);
            Need(sentinel[0x19]==1&&sentinel[0x18]<=1,"HOTBAR_SENTINEL","快捷格 sentinel 的 nil／color 不符");
            Pointer(U64(sentinel));Pointer(U64(sentinel,8));Pointer(U64(sentinel,16));
            for(int key=0;key<24;key++)slots.Add(CaptureSlot(key));
            Need((ulong)slots.Count(s=>s.Found)==count,"HOTBAR_COUNT_MEMBERSHIP","快捷格數量與完整0至23鍵集合不一致");

            Slot CaptureSlot(int key)
            {
                ulong cursor=U64(sentinel,8),parent=head,candidate=0;
                int candidateKey=0,pathCount=0;long low=(long)int.MinValue-1,high=(long)int.MaxValue+1;
                var seen=new HashSet<ulong>();
                while(cursor!=head)
                {
                    Pointer(cursor);Need(pathCount++<64&&seen.Add(cursor),"HOTBAR_PATH_LIMIT","快捷格搜尋路徑循環或超過64節點");
                    var node=Track(cursor,40);int nodeKey=I32(node,0x20);
                    Need(node[0x19]==0&&node[0x18]<=1&&U64(node,8)==parent&&nodeKey>low&&nodeKey<high,
                        "HOTBAR_PATH_OWNER","快捷格 parent／nil／color／排序不符");
                    ulong left=U64(node),right=U64(node,16);Pointer(left);Pointer(right);
                    Need(left!=cursor&&right!=cursor&&(left==head||right==head||left!=right),"HOTBAR_CHILD_ALIAS","快捷格子節點別名或循環不符");
                    Child(left,cursor,low,nodeKey);Child(right,cursor,nodeKey,high);
                    parent=cursor;
                    if(nodeKey>=key){candidate=cursor;candidateKey=nodeKey;high=nodeKey;cursor=left;}
                    else{low=nodeKey;cursor=right;}
                }
                bool found=candidate!=0&&candidateKey==key;
                if(!found)return new(key/8+1,key%8+5,key,false,false,null,null,null,null,null);
                var shared=Track(candidate+0x28,16);ulong data=U64(shared),control=U64(shared,8);
                if(data==0)
                {if(control!=0)Pointer(control);return new(key/8+1,key%8+5,key,true,false,null,null,null,null,null);}
                Pointer(data);Pointer(control);
                // ATS-only discovery reads kind/id/resource for all bounded slots, text only for ATS candidates.
                var prefix=Track(data,readSlots?0x50:16);
                ulong id=U64(prefix);uint type=U32(prefix,8),resource=U32(prefix,12);
                byte? gate=null;string? display=null;
                if(readSlots)gate=Track(data+0xB4,1)[0];
                if(readSlots||type==3&&id==16)
                {
                    var textDescriptor=readSlots?prefix.AsSpan(0x30,32).ToArray():Track(data+0x30,32);
                    display=Display(textDescriptor);
                }
                return new(key/8+1,key%8+5,key,true,true,id,type,gate,resource,display);
            }
            void Child(ulong child,ulong owner,long lower,long upper)
            {
                if(child==head)return;
                var bytes=Track(child,40);int key=I32(bytes,0x20);
                Need(bytes[0x19]==0&&bytes[0x18]<=1&&U64(bytes,8)==owner&&key>lower&&key<upper,
                    "HOTBAR_CHILD_ORDER","快捷格子節點 parent／nil／color／排序不符");
                Pointer(U64(bytes));Pointer(U64(bytes,16));
            }
            string Display(byte[] descriptor)
            {
                ulong length=U64(descriptor,16),cap=U64(descriptor,24);
                Need(length<=256&&cap>=length&&cap is >=15 and <=4096&&(cap!=15||length<=15),"HOTBAR_TEXT_SIZE","快捷格文字大小超界");
                byte[] bytes;
                if(cap==15)bytes=descriptor.AsSpan(0,(int)length+1).ToArray();
                else{ulong pointer=U64(descriptor);Pointer(pointer);bytes=Track(pointer,(int)length+1);}
                Need(bytes.Length==(int)length+1&&bytes[^1]==0&&!bytes.AsSpan(0,(int)length).Contains((byte)0),"HOTBAR_TEXT_NUL","快捷格文字並非完整單一NUL結尾");
                try{return Utf8.GetString(bytes,0,(int)length);}
                catch(DecoderFallbackException){throw new ProbeFailure("HOTBAR_TEXT_ENCODING","快捷格文字並非完整UTF8");}
            }
        }
        // Slot/field values must match within this capture only. Later normal changes do not renew or erase their original time.
        foreach(var proof in proofs)
            Need(ReadProof(proof.Key.Address,proof.Key.Size).AsSpan().SequenceEqual(proof.Value),sourceKeys.Contains(proof.Key)?"GROUP_SOURCE_CHANGED":"HOTBAR_CLOSURE_CHANGED","快捷列來源或欄位在本次採樣內改變");
        foreach(var source in sourceKeys)
            Need(ReadProof(source.Address,source.Size).AsSpan().SequenceEqual(proofs[source]),"GROUP_SOURCE_CHANGED","快捷列最終自身／場景來源已變更");
        memory.Verify();journal.RequireScene(director,scene);journal.BindSelf(own);
        var closingSource=sourceKeys.Select(k=>(k.Address,k.Size,Bytes:proofs[k])).ToArray();
        var previous=journal.SourceClosure;
        journal.SourceClosure=()=>
        {
            previous?.Invoke();
            foreach(var proof in closingSource)
                Need(ReadProof(proof.Address,proof.Size).AsSpan().SequenceEqual(proof.Bytes),"GROUP_SOURCE_CHANGED","跨資料組快捷列自身／場景來源已變更");
            memory.Verify();
        };
        journal.Write("HOTBAR_METADATA_MODE",new{MetadataEvidence=memory.MetadataEvidence,DynamicSourcesAndValuesFresh=true});
        if(readPage)journal.Set("hotbarPage",new{Raw=page!.Value,Page=page.Value+1},sampledAt,"StructureValidated");
        if(readFocus)journal.Set("hotbarFocus",new{Raw=focus!.Value,SlotInPage=focus.Value%8,FunctionKey=focus.Value%8+5},sampledAt,"StructureValidated");
        if(readSlots)journal.Set("hotbarSlots",slots.ToArray(),sampledAt,"StructureValidated");
        if(readAts)
        {
            var matches=slots.Where(s=>s.Found&&s.HasData&&s.RawType==3&&s.RawId==16&&s.DisplayText=="ATS On/Off").ToArray();
            var ats=AtsOffOnly(matches.Length,matches.Length==1?matches[0].RawResourceState:null,sampledAt);
            if(ats.State=="Observed")journal.Set("atsEnabled",false,sampledAt,"ObservedOffStateOnly");
            else journal.Unknown("atsEnabled",ats.Detail);
        }
    }
    internal static FieldReading AtsOffOnly(int matches,uint? resource,DateTimeOffset sampledAt)
        =>matches==1&&resource==20010?FieldReading.Observed(false,sampledAt,"ObservedOffStateOnly"):
            FieldReading.Unknown(matches==1?"目前只驗證ATS關閉碼20010，其餘狀態未知":"未找到唯一已驗ATS格；不把缺少格當作關閉");
    private static ulong U64(byte[] b,int at=0)=>BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at,8));
    private static uint U32(byte[] b,int at=0)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at,4));
    private static int I32(byte[] b,int at=0)=>BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at,4));
    private static void Pointer(ulong value)=>Need(Probe.Pointer(value),"HOTBAR_POINTER","快捷列來源指標無效");
    private static void Need(bool condition,string code,string message){if(!condition)throw new ProbeFailure(code,message);}
}
