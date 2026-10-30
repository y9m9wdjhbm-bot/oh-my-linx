namespace MemoryHub.Reader;

// Early failure saves leaf work. The original final object/source closure remains
// mandatory: this check cannot turn a changing cross-process collection atomic.
internal static class ActorMembershipFence
{
    internal static void Verify(IEnumerable<(ulong Address,byte[] Expected)> proofs,
        Func<ulong,int,byte[]> read,Action check,Action<ulong,int> changed)
    {
        foreach(var proof in proofs)
        {
            check();var actual=read(proof.Address,proof.Expected.Length);check();
            if(!actual.AsSpan().SequenceEqual(proof.Expected))
            {
                changed(proof.Address,proof.Expected.Length);
                throw new ProbeFailure("ACTOR_COLLECTION_CHANGED","角色集合在逐項資料讀取前已變動；跳過失效集合的後續工作。");
            }
        }
    }
}
