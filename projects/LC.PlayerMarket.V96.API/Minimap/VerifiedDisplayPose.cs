using LocalMiniMap;
using MemoryHub;
namespace LcPlayerMarket;
/// <summary>Display-only cache of a receipt already accepted by the existing navigation client.
/// A private bridge receipt does not publish scene/mapdata pointers; Source + numeric map are used,
/// and the cache expires after 2s. It never substitutes a guessed scene, position or map.</summary>
internal sealed record VerifiedDisplayPose(SourceIdentity Source,ulong SelfId,int MapId,int X,int Y,
    DateTimeOffset SampledAt,string? JobId=null,int? TargetX=null,int? TargetY=null,int? TargetMapId=null)
{
 internal bool Fresh(DateTimeOffset now)=>SampledAt<=now&&now-SampledAt<=TimeSpan.FromSeconds(2)
    &&SelfId>0&&MapId>=0&&X is>=0 and<=65535&&Y is>=0 and<=65535;
 internal BoundPose Bound()=>new(new(Source.ProcessId,Source.CreationFileTime,Source.SessionKey,SelfId,MapId,null),
    new(X,Y),SampledAt,TargetMapId==MapId&&TargetX is{} x&&TargetY is{} y&&JobId is{Length:>0}
       ?new(new(Source.ProcessId,Source.CreationFileTime,Source.SessionKey,SelfId,MapId,null),new(x,y),JobId,SampledAt):null,[]);
}
