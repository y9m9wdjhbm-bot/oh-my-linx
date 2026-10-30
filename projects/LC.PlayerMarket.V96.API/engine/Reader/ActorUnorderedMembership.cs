using System.Buffers.Binary;
namespace MemoryHub.Reader;
// Research candidate. Equality means the exact multiset of all 16-byte slots,
// including null slots and repeated slots. Identity and source proofs stay exact.
internal static class ActorUnorderedMembership
{
    internal sealed record Chunk(ulong Address,byte[] Expected);
    internal sealed record Snapshot(ulong HeaderAddress,byte[] Header,ActorVectorLayout Layout,Chunk[] Chunks);
    internal static Snapshot Capture(ulong headerAddress,byte[] header,ActorVectorLayout layout,
        IReadOnlyDictionary<(ulong At,int Size),byte[]> proofs)
    {
        if(ActorVectorLayout.Inspect(headerAddress,header,ActorCollectionLimits.MaximumSlots)!=layout)throw new ProbeFailure("ACTOR_VECTOR_LAYOUT","Snapshot layout differs from validated header");
        var chunks=new List<Chunk>();
        for(int offset=0;offset<layout.ActiveBytes;offset+=4096)
        {
            int length=Math.Min(4096,layout.ActiveBytes-offset);
            if(!proofs.TryGetValue((layout.Begin+(ulong)offset,length),out var expected)||expected.Length!=length)
                throw new ProbeFailure("ACTOR_VECTOR_READ_INCOMPLETE","Expected active vector chunk missing");
            chunks.Add(new(layout.Begin+(ulong)offset,expected.ToArray()));
        }
        return new(headerAddress,header.ToArray(),layout,chunks.ToArray());
    }
    internal static void Verify(Snapshot snapshot,Func<ulong,int,byte[]> read,Action check,Action<ulong,int>? changed=null)
    {
        void Header(){check();var actual=read(snapshot.HeaderAddress,24);check();
            if(actual.Length!=24)throw new ProbeFailure("ACTOR_VECTOR_READ_INCOMPLETE","Short vector header read");
            if(!actual.AsSpan().SequenceEqual(snapshot.Header))Fail(snapshot.HeaderAddress,24);}
        void Fail(ulong address,int length){changed?.Invoke(address,length);throw new ProbeFailure("ACTOR_COLLECTION_CHANGED","Exact vector membership changed");}
        Header();
        var expected=new byte[snapshot.Layout.ActiveBytes];var actual=new byte[expected.Length];int offset=0;
        foreach(var chunk in snapshot.Chunks)
        {
            check();var bytes=read(chunk.Address,chunk.Expected.Length);check();
            if(bytes.Length!=chunk.Expected.Length)throw new ProbeFailure("ACTOR_VECTOR_READ_INCOMPLETE","Short active vector read");
            chunk.Expected.CopyTo(expected,offset);bytes.CopyTo(actual,offset);offset+=bytes.Length;
        }
        Header();check();
        if(!expected.AsSpan().SequenceEqual(actual)&&!Pairs(expected,check).AsSpan().SequenceEqual(Pairs(actual,check)))Fail(snapshot.Layout.Begin,snapshot.Layout.ActiveBytes);
        check();
    }
    private static (ulong Object,ulong Control)[] Pairs(byte[] bytes,Action check)
    {
        if(bytes.Length%16!=0)throw new ProbeFailure("ACTOR_VECTOR_READ_INCOMPLETE","Unaligned active vector bytes");
        var result=new (ulong Object,ulong Control)[bytes.Length/16];
        for(int i=0;i<result.Length;i++){check();result[i]=(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i*16)),BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i*16+8)));}
        Array.Sort(result);check();return result;
    }
}
