using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal readonly record struct LiveStartupIdentityObservation(SourceIdentity? Source,ulong? FreshSelfId)
{
    internal PatrolStartupReadinessContext? Context=>Source is not null&&FreshSelfId is >0
        ?new(Source,FreshSelfId.Value):null;
    internal bool DefinitelyDifferent(PatrolStartupReadinessContext expected)=>
        Source is not null&&Source!=expected.Source || FreshSelfId is >0&&FreshSelfId!=expected.SelfId;
}

/// <summary>Startup policy binds source/self only; position remains a separate navigation/dispatch proof.</summary>
internal static class LiveStartupIdentityContext
{
    internal static LiveStartupIdentityObservation Read(HubSnapshot? snapshot,DateTimeOffset now)
    {
        ulong? self=null;
        // Retain the production snapshot connection/time gates; only position
        // is independent of startup identity. Never renew old field timestamps.
        if(snapshot is null || snapshot.State == "Disconnected" || snapshot.PublishedAt == default
            || snapshot.PublishedAt > now || now-snapshot.PublishedAt > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            return new(snapshot?.Source, null);
        var field=snapshot?.Fields.GetValueOrDefault("selfId");
        if(field is not null&&field.Fresh(now)&&field.Value is{ValueKind:JsonValueKind.Number} value
            &&value.TryGetUInt64(out ulong observed)&&observed>0)self=observed;
        return new(snapshot?.Source,self);
    }
}
