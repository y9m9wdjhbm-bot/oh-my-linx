namespace MemoryHub.Reader;
// Actual final proof closure factored for deterministic fake-read regression.
internal static class ActorFinalProofClosure
{
    internal sealed record Proof(ulong Address,byte[] Expected,bool Source,bool Metadata,bool UnorderedMember,string Category);
    internal static void Verify(IEnumerable<Proof> proofs,Func<ulong,int,byte[]> read,Action check,Action<Proof> changed)
    {
        foreach(var proof in proofs)
        {
            // Source and metadata never inherit unordered membership semantics,
            // even if an unexpected address alias overlaps a member range.
            if(proof.UnorderedMember&&!proof.Source&&!proof.Metadata)continue;
            check();var after=read(proof.Address,proof.Expected.Length);check();
            if(!after.AsSpan().SequenceEqual(proof.Expected))
            {
                changed(proof);
                throw new ProbeFailure(ActorFieldsReader.ClosureChangeCode(proof.Source,proof.Metadata),"Actor final identity/source/type proof changed");
            }
        }
    }
}
