namespace MemoryHub.Reader;

internal readonly record struct ActorObservationKey(ulong Object,ulong Control,ulong Id);

// Owned by ONE ActorFieldsReader.Reader/CaptureOnce. Only component discovery
// and its original structural proofs belong here; never leaf values or timestamps.
internal sealed class ActorCaptureCache<T>
{
    private readonly Dictionary<ActorObservationKey,T> values=[];
    internal int Discoveries{get;private set;}
    internal int Reuses{get;private set;}
    internal T Get(ActorObservationKey key,Func<T> discover)
    {
        if(values.TryGetValue(key,out var value)){Reuses++;return value;}
        if(values.Count>=4096)throw new ProbeFailure("ACTOR_COMPONENT_CACHE_LIMIT","當次角色元件結構暫存超出有界限制。");
        value=discover();values.Add(key,value);Discoveries++;return value;
    }
    internal void Clear()=>values.Clear();
}
