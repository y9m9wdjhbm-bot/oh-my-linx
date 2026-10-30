using System.Buffers.Binary;
using System.Text;

namespace MemoryHub.Reader;

internal static class BuffFieldReader
{
    private sealed record Proof(ulong At,byte[] Bytes,bool Metadata=false);
    private sealed record TypeRow(string Role,uint Vtable,uint Descriptor,uint Col,string Name);
    private sealed record SlotRow(int Id,ulong Layout,ulong Payload,ulong Sprite,ulong ImageView);
    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        if(!selection.Enabled("buffIds"))return;
        var sampledAt=DateTimeOffset.UtcNow;
        byte[] Read(ulong at,int count)
        {
            budget.Check();
            if(at>=memory.Image&&at-memory.Image<=(ulong)memory.ImageSize-(uint)count)return memory.Required((uint)(at-memory.Image),count);
            if(!memory.Pointed(at,count,out var b))throw new ProbeFailure("BUFF_RESEARCH_READ","BUFF 研究來源未完整讀取。",memory.LastNativeError);
            return b;
        }
        for(int cycle=1;cycle<=1;cycle++)
        {
            var proofs=new List<Proof>();var types=new List<TypeRow>();var visited=new HashSet<ulong>();
            var callbackProofs=new Dictionary<ulong,ReadOnlyBuffCallbackContract.Candidate>();
            byte[] Track(ulong at,int count){var bytes=Read(at,count);proofs.Add(new(at,bytes));return bytes;}
            byte[] Meta(ulong at,int count){var bytes=memory.MetaPointed(at,count);proofs.Add(new(at,bytes,true));return bytes;}
            TypeRow Type(ulong address,string role,string? requiredName=null,uint? requiredVtable=null)
            {
                var symbolic=FixedType(role);
                var exact=symbolic with{Vtable=memory.Rva(symbolic.Vtable),Descriptor=memory.Rva(symbolic.Descriptor),Col=memory.Rva(symbolic.Col)};
                ulong vt=Probe.U64(Track(address,8),0);
                if(vt!=memory.Image+exact.Vtable||requiredVtable is {} vtable&&memory.Rva(vtable)!=exact.Vtable||requiredName is {} expectedName&&expectedName!=exact.Name)
                    throw new ProbeFailure("BUFF_TYPE_VTABLE","BUFF 精確版型 vtable／名稱不符。");
                ulong col=Probe.U64(Meta(vt-8,8),0);
                if(col!=memory.Image+exact.Col)throw new ProbeFailure("BUFF_TYPE_COL","BUFF 精確 COL 不符。");
                var c=Meta(col,24);uint td=Probe.U32(c,12),hier=Probe.U32(c,16);
                if(Probe.U32(c,0)!=1||Probe.U32(c,4)!=0||Probe.U32(c,8)!=0||Probe.U32(c,20)!=exact.Col||td!=exact.Descriptor||hier>(uint)memory.ImageSize-16)
                    throw new ProbeFailure("BUFF_TYPE_LAYOUT","BUFF primary COL／TD 關係不符。");
                byte[] expected=Encoding.ASCII.GetBytes(exact.Name+'\0');
                if(!Meta(memory.Image+td+16,expected.Length).AsSpan().SequenceEqual(expected))
                    throw new ProbeFailure("BUFF_TYPE_NAME","BUFF 完整精確 RTTI 名稱不符。");
                string name=exact.Name;
                if(role=="InlineCallback"&&!callbackProofs.ContainsKey(vt))
                {
                    try{callbackProofs.Add(vt,ReadOnlyBuffCallbackContract.Inspect(memory,new(exact.Vtable,exact.Descriptor,exact.Col),Track,budget.Check));}
                    catch(InvalidDataException error){throw new ProbeFailure("BUFF_CALLBACK_DATA_CONTRACT",error.Message);}
                }
                var row=new TypeRow(role,(uint)(vt-memory.Image),td,(uint)(col-memory.Image),name);types.Add(row);return row;
            }
            ulong director=Probe.U64(Track(memory.Address(0x17E00D8),8),0);Type(director,"Director",".?AVDirector@cocos2d@@",0x121AC40);
            ulong scene=Probe.U64(Track(director+0x150,8),0);journal.RequireScene(director,scene);
            ulong[] Node(ulong at,ulong parent,string role,string? name=null,uint? vt=null)
            {
                if(!Probe.Pointer(at)||!visited.Add(at))throw new ProbeFailure("BUFF_RESEARCH_NODE","BUFF 節點無效或重複。");
                Type(at,role,name,vt);
                var links=Track(at+0x178,40);ulong begin=Probe.U64(links,0),end=Probe.U64(links,8),cap=Probe.U64(links,16);
                if(Probe.U64(links,24)!=parent||Probe.U64(links,32)!=director)throw new ProbeFailure("BUFF_RESEARCH_OWNER","BUFF parent/director 不符。");
                if(begin==0&&end==0&&cap==0)return [];
                if(!Probe.Pointer(begin)||!Probe.Pointer(end)||!Probe.Pointer(cap)||begin>end||end>cap||(end-begin)%8!=0||(cap-begin)%8!=0||(cap-begin)/8>4096)
                    throw new ProbeFailure("BUFF_RESEARCH_VECTOR","BUFF ordinary children 超界。");
                var children=new List<ulong>();
                for(ulong offset=0;offset<end-begin;offset+=4096)
                {
                    var bytes=Track(begin+offset,(int)Math.Min(4096,end-begin-offset));
                    for(int i=0;i<bytes.Length;i+=8)children.Add(Probe.U64(bytes,i));
                }
                if(children.Any(x=>!Probe.Pointer(x))||children.Count!=children.Distinct().Count())throw new ProbeFailure("BUFF_RESEARCH_CHILDREN","BUFF 子物件非法或不唯一。");
                return children.ToArray();
            }
            int[] path=[2,2,5,0,0];ulong current=scene,parent=0;
            for(int depth=0;depth<path.Length;depth++)
            {
                var children=Node(current,parent,"Prefix"+depth,depth==0?".?AVScene@cocos2d@@":".?AVNode@cocos2d@@",depth==0?0x121D980u:0x121B1D0u);
                if(path[depth]>=children.Length)throw new ProbeFailure("BUFF_RESEARCH_PATH","已知 BUFF 路徑不存在。");
                parent=current;current=children[path[depth]];
            }
            ulong view=current;var layouts=Node(view,parent,"BuffContainer");
            if(layouts.Length>128)throw new ProbeFailure("BUFF_RESEARCH_LIMIT","BUFF 版面超出128個。");
            var slots=new List<SlotRow>();
            foreach(ulong layout in layouts)
            {
                var children=Node(layout,view,"BuffLayout",".?AVLayout@ui@cocos2d@@");
                ulong callbackAt=layout+0x570;var callback=Track(callbackAt,0x40);Type(callbackAt,"InlineCallback");
                ulong payload=Probe.U64(callback,8);
                if(Probe.U64(callback,0x38)!=callbackAt||!Probe.Pointer(payload)||payload<0x10010)throw new ProbeFailure("BUFF_RESEARCH_CALLBACK","BUFF callback inline／payload 不符。");
                ulong control=payload-16;Type(control,"PayloadControl");
                var refs=Read(control+8,8);
                if(BinaryPrimitives.ReadInt32LittleEndian(refs)<1||BinaryPrimitives.ReadInt32LittleEndian(refs.AsSpan(4))<1)throw new ProbeFailure("BUFF_RESEARCH_REFS","BUFF 控制物件失效。");
                var data=Track(payload,32);ulong owner=Probe.U64(data,0),sprite=Probe.U64(data,8),imageView=Probe.U64(data,16);int id=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
                if(owner!=layout||id<=0||slots.Any(x=>x.Id==id)||sprite==imageView||children.Length!=2||children.Count(x=>x==sprite)!=1||children.Count(x=>x==imageView)!=1)
                    throw new ProbeFailure("BUFF_RESEARCH_PAYLOAD","BUFF owner／ID／兩個圖示子物件不符。");
                Node(sprite,layout,"Sprite",".?AVSprite@cocos2d@@");Node(imageView,layout,"ImageView",".?AVImageView@ui@cocos2d@@");
                slots.Add(new(id,layout,payload,sprite,imageView));
            }
            foreach(var proof in proofs)if(!(proof.Metadata?memory.MetaPointed(proof.At,proof.Bytes.Length):Read(proof.At,proof.Bytes.Length)).AsSpan().SequenceEqual(proof.Bytes))throw new ProbeFailure("BUFF_RESEARCH_CLOSURE","BUFF 結構／ID 已變動，捨棄本輪。");
            foreach(var callback in callbackProofs.Values)
                try{callback.CloseLocal(Read,where=>ReadOnlyBuffCallbackContract.Query(memory,where),budget.Check);}
                catch(InvalidDataException error){throw new ProbeFailure("BUFF_CALLBACK_DATA_CHANGED",error.Message);}
            foreach(var slot in slots)
            {
                var refs=Read(slot.Payload-8,8);
                if(BinaryPrimitives.ReadInt32LittleEndian(refs)<1||BinaryPrimitives.ReadInt32LittleEndian(refs.AsSpan(4))<1)throw new ProbeFailure("BUFF_RESEARCH_REFS","BUFF 關閉核對時控制物件失效。");
            }
            if(Probe.U64(Read(memory.Address(0x17E00D8),8),0)!=director||Probe.U64(Read(director+0x150,8),0)!=scene)throw new ProbeFailure("BUFF_RESEARCH_SOURCE","BUFF 最後場景已變更。");
            memory.Verify();
            journal.RequireScene(director,scene);
            journal.Set("buffIds",slots.Select(s=>s.Id).Order().ToArray(),sampledAt,"StructureValidated;ReadOnlyCallbackMetadataAndOwnership;"+memory.MetadataEvidence);
        }
    }
    private static TypeRow FixedType(string role)=>role switch
    {
        "Director"=>new(role,0x121AC40,0x176A2A0,0x13456E8,".?AVDirector@cocos2d@@"),
        "Prefix0"=>new(role,0x121D980,0x1660718,0x1348E48,".?AVScene@cocos2d@@"),
        "Prefix1" or "Prefix2" or "Prefix3" or "Prefix4"=>new(role,0x121B1D0,0x1649050,0x1345870,".?AVNode@cocos2d@@"),
        "BuffContainer"=>new(role,0x11FE948,0x173E3A8,0x1321460,".?AVBuffSlotView@ui@lineage@@"),
        "BuffLayout"=>new(role,0x1227950,0x1660EB0,0x134CBC8,".?AVLayout@ui@cocos2d@@"),
        "InlineCallback"=>new(role,0x11F62C0,0x1732020,0x1316F08,".?AV?$_Func_impl_no_alloc@V<lambda_1>@?1??Init@BuffSlot@ui@lineage@@AEAAXXZ@XPEAVRef@cocos2d@@_NPEAVEventMouse@7@@std@@"),
        "PayloadControl"=>new(role,0x11FE838,0x173E240,0x13213C0,".?AV?$_Ref_count_obj2@VBuffSlot@ui@lineage@@@std@@"),
        "Sprite"=>new(role,0x12241E0,0x1660F60,0x134C250,".?AVSprite@cocos2d@@"),
        "ImageView"=>new(role,0x1229A80,0x1660DF0,0x134E198,".?AVImageView@ui@cocos2d@@"),
        _=>throw new ProbeFailure("BUFF_TYPE_ROLE","未知 BUFF 型別角色")
    };

}
