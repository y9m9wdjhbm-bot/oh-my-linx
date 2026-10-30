using System.Text;

namespace MemoryHub.Reader;

// Source identity is an internal dependency, independent of the visible selfId switch.
internal static class CharacterSourceFence
{
    private sealed record Spec(uint Vtable,uint Col,uint Descriptor,string Name);
    private static readonly Spec Root=new(0x11672A0,0x1263C10,0x16AEE30,".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@");
    private static readonly Spec Holder=new(0,0x1263CF0,0x16AEF50,".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@");
    private static readonly Spec Character=new(0x1169730,0x12663F8,0x1643CF0,".?AVCharacterDataSubsystem@lineage@@");

    internal static void Bind(ReadSession memory,Journal journal,Budget budget)
    {
        var proofs=new Dictionary<(ulong At,int Count),byte[]>();
        var metadataProofs=new HashSet<(ulong At,int Count)>();
        var subsystemProofs=new List<ReadOnlyUiSubsystemContract.Candidate>();
        byte[] Read(ulong at,int count)
        {
            budget.Check();
            if(!memory.Pointed(at,count,out var bytes))throw new ProbeFailure("GROUP_SOURCE_CHANGED","角色來源核對未完整讀取。",memory.LastNativeError);
            return bytes;
        }
        byte[] Track(ulong at,int count,bool metadata=false)
        {
            var bytes=metadata?memory.MetaPointed(at,count):Read(at,count);var key=(at,count);
            if(metadata)metadataProofs.Add(key);
            if(proofs.TryGetValue(key,out var prior))Need(prior.AsSpan().SequenceEqual(bytes),"角色來源在起始核對中改變。");
            else proofs.Add(key,bytes);
            return bytes;
        }
        ulong PointerAt(ulong at){ulong p=Probe.U64(Track(at,8),0);Need(Probe.Pointer(p),"角色來源指標無效。");return p;}
        byte[] Meta(ulong at,int count)
        {
            Need(at>=memory.Image&&at-memory.Image<=(ulong)memory.ImageSize-(uint)count,"角色來源中繼資料超出模組。");
            return Track(at,count,true);
        }
        void Exact(ulong obj,Spec spec)
        {
            ulong vt=PointerAt(obj);Need(spec.Vtable==0||vt==memory.Address(spec.Vtable),"角色來源精確 vtable 不符。");
            var prefix=Meta(vt-8,24);var col=Meta(memory.Address(spec.Col),24);
            Need(Probe.U64(prefix,0)==memory.Address(spec.Col)&&Probe.U32(col,0)==1&&Probe.U32(col,4)==0&&Probe.U32(col,8)==0&&
                Probe.U32(col,12)==memory.Rva(spec.Descriptor)&&Probe.U32(col,20)==memory.Rva(spec.Col),"角色來源 COL／TD 關係不符。");
            byte[] expected=Encoding.ASCII.GetBytes(spec.Name+'\0');
            Need(Meta(memory.Address(spec.Descriptor,16),expected.Length).AsSpan().SequenceEqual(expected),"角色來源完整型別名稱不符。");
            if(spec==Character)subsystemProofs.Add(ReadOnlyUiSubsystemContract.Inspect(memory,
                new((uint)(vt-memory.Image),memory.Rva(spec.Descriptor),memory.Rva(spec.Col)),
                (at,count)=>Track(at,count),budget.Check,spec.Name));
        }
        ulong root=PointerAt(memory.Address(0x1642B60));Exact(root,Root);
        Need(Track(root+0xD8,1)[0]!=0,"角色服務尚未就緒。");
        ulong holder=PointerAt(root+0xD0);Exact(holder,Holder);
        byte[] header=Track(holder+0x180,24);ulong begin=Probe.U64(header,0),end=Probe.U64(header,8),cap=Probe.U64(header,16);
        Need(Probe.Pointer(begin)&&Probe.Pointer(end)&&Probe.Pointer(cap)&&begin<=end&&end<=cap&&
            (end-begin)%16==0&&(cap-begin)%16==0&&(end-begin)/16<=128,"角色來源服務集合無效或超界。");
        ulong character=0,characterControl=0;var ownership=new Dictionary<ulong,ulong>();
        for(ulong offset=0;offset<end-begin;offset+=4096)
        {
            byte[] rows=Track(begin+offset,(int)Math.Min(4096,end-begin-offset));
            for(int i=0;i<rows.Length;i+=16)
            {
                ulong obj=Probe.U64(rows,i),control=Probe.U64(rows,i+8);
                if(obj==0){Need(control==0,"空來源服務有非空控制物件。");continue;}
                Need(Probe.Pointer(obj)&&Probe.Pointer(control),"來源服務所屬指標無效。");
                Need(!ownership.TryGetValue(obj,out var prior)||prior==control,"來源服務所屬衝突。");ownership[obj]=control;
                if(Probe.U64(Read(obj,8),0)!=memory.Address(Character.Vtable))continue;
                Need(character==0||character==obj,"角色資料服務不唯一。");Exact(obj,Character);character=obj;characterControl=control;
            }
        }
        Need(character!=0,"缺少目前角色資料服務。");
        ulong own=Probe.U64(Track(character+0x6D8,8),0);Need(own is not(0 or ulong.MaxValue),"目前角色來源 ID 尚未就緒。");
        void Close()
        {
            foreach(var proof in subsystemProofs)proof.CloseLocal(Read,at=>ReadOnlyUiSubsystemContract.Query(memory,at),budget.Check);
            foreach(var proof in proofs)Need((metadataProofs.Contains(proof.Key)?memory.MetaPointed(proof.Key.At,proof.Key.Count):Read(proof.Key.At,proof.Key.Count)).AsSpan().SequenceEqual(proof.Value),"角色來源在跨欄位採樣中改變，捨棄本輪。");
            var refs=Read(characterControl+8,8);Need(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(refs)>0&&System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(refs.AsSpan(4))>0,"角色資料服務已失去有效參照。");
            memory.Verify();
        }
        Close();journal.BindSelf(own);
        journal.Write("CHARACTER_SOURCE_METADATA_MODE",new{MetadataEvidence=memory.MetadataEvidence,DynamicSourcesFresh=true});
        var previous=journal.SourceClosure;journal.SourceClosure=()=>{previous?.Invoke();Close();};
    }
    private static void Need(bool value,string detail){if(!value)throw new ProbeFailure("GROUP_SOURCE_CHANGED",detail);}
}
