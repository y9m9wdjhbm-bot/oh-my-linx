namespace MemoryHub.Reader;

/// <summary>One fresh discovery only for the known local actor-snapshot race.</summary>
internal static class SelfSpatialDiscoveryRetry
{
    internal static bool Eligible(string code,int nativeError)=>
        (code is "SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED" or "SELF_SPATIAL_ACTOR_DISCOVERY_CONTROL")&&nativeError==0;

    // A pending field token is derived from the actual capture failure, never
    // from a consumer's Unknown text. ReadProfile still has to close its entire
    // source before any token can leave the reader in a ReadResult.
    internal static bool RetryableField(string group,string field,string code,int nativeError)=>
        group=="自身座標" && field=="position" && Eligible(code,nativeError);

    // The generic failure type keeps this decision testable without constructing
    // a process reader. Production supplies ProbeFailure and its exact code.
    internal static TResult Run<TResult,TFailure>(Func<TResult> discover,
        Func<TFailure,(string Code,int NativeError)> classify,Action<TFailure> prepareRetry)where TFailure:Exception
    {
        try{return discover();}
        catch(TFailure error)when(Eligible(classify(error).Code,classify(error).NativeError))
        {
            prepareRetry(error);
        }
        // Deliberately outside the catch: every second failure escapes unchanged.
        return discover();
    }
}
