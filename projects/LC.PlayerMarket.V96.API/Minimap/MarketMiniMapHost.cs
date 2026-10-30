using System.Text.Json;
using LocalMiniMap;
using MemoryHub;
using MapView=LocalMiniMap.View;
namespace LcPlayerMarket;
internal sealed record MiniMapPresentation(string Status,MapView? View,TerrainPatch? Terrain,Frame? Frame);
internal sealed record MiniMapSelection(string SourceSessionKey,ulong Owner,string Identity,string Token);
/// <summary>Pure data coordinator. Only disk terrain work is asynchronous; no GUI, LC or backend methods.</summary>
internal sealed class MarketMiniMapHost(ILocalTerrainProvider terrain,Func<DateTimeOffset>? clock=null)
{
 private readonly ExistingSnapshotAdapter adapter=new();private readonly Func<DateTimeOffset> now=clock??(()=>DateTimeOffset.UtcNow);
 private int generation;
 internal int Generation=>Volatile.Read(ref generation);
 internal bool IsCurrentGeneration(int captured)=>captured==Generation;
 internal Ledger? LedgerForPresentation=>adapter.Ledger;
 internal void Reset(){Interlocked.Increment(ref generation);adapter.Clear();}
 internal async Task<MiniMapPresentation> BuildAsync(VerifiedDisplayPose? pose,HubSnapshot? hub,ShopCommandResponse? broker,
    ClientFrame? client,MiniMapSelection? selection,CancellationToken token)
 {
  MiniMapPresentation Wait(string reason)=>new(reason,null,null,null);
  token.ThrowIfCancellationRequested();var epoch=Volatile.Read(ref generation);var at=now();
  if(pose is null||!pose.Fresh(at))return Wait("等待地圖資料");
  if(hub?.Source!=pose.Source){if(hub?.Source is not null)adapter.Clear();return Wait("等待同來源地圖資料");}
  if(client is null||client.Width<=0||client.Height<=0||client.Dpi==0)return Wait("等待 LC 視窗資料");
  // HUD joins need fresh identity/position/nearby/shop fields and receipt metadata, never product rows.
  var fields=new Dictionary<string,FieldReading>();
  foreach(string key in new[]{"selfId","position","nearbyPlayers","playerShopStates"})
   if(hub.Fields.TryGetValue(key,out var reading))fields.Add(key,reading);
  JsonElement h=JsonSerializer.SerializeToElement(new{hub.Source,hub.PublishedAt,hub.State,Fields=fields},HubProtocol.Json);
  JsonElement b=JsonSerializer.SerializeToElement(broker is null?null:new{
   broker.At,broker.CurrentSourceSessionKey,
   TargetSnapshot=broker.TargetSnapshot is{} snapshot?new{snapshot.SnapshotId,snapshot.SourceSessionKey,snapshot.SelfId,snapshot.RefreshedAt}:null,
   Scan=broker.Scan is{} scan?new{scan.JobId,scan.SourceSessionKey,scan.TargetSnapshotId,scan.Running,scan.Paused,scan.PauseRequested,scan.CurrentOwnerId}:null,
   Listings=broker.Listings.Take(8193).Select(listing=>new{listing.SourceSessionKey,listing.RequestId,Owner=new{listing.Owner.Id,listing.Owner.LocalIdentity},listing.ViewerDirection,listing.CapturedAt,listing.State,listing.Complete,listing.OwnerAndResponseMatched}).ToArray()
  },HubProtocol.Json);
  var frame=adapter.AdaptBoundPose(pose.Bound(),h,b,new(client.X,client.Y,client.Width,client.Height),client.Dpi/96.0,at);
  if(frame is null)return Wait(adapter.Status);
  var patch=await terrain.GetAsync(frame.Scope.Map,(int)frame.Self.X,(int)frame.Self.Y,token).ConfigureAwait(false);
  token.ThrowIfCancellationRequested();if(epoch!=Volatile.Read(ref generation))return Wait("等待新的地圖資料");if(patch is null)return Wait(terrain.Error??"等待周邊地形資料");
  if(!pose.Fresh(now()))return Wait("等待地圖資料");
  if(selection is{} selected&&selected.SourceSessionKey==frame.Scope.Token&&!string.IsNullOrWhiteSpace(selected.Token))
  {
   var merchant=frame.Merchants.SingleOrDefault(m=>m.Id==selected.Owner&&m.Identity==selected.Identity&&m.ShopObserved&&Ledger.Fresh(m.At,now())&&Ledger.Fresh(m.ShopAt,now()));
   if(merchant is not null)frame=frame with{Target=new(frame.Scope,merchant.Position,selected.Token,merchant.At)};
  }
  var view=Projection.Build(frame,frame.Scope,patch,adapter.Ledger!,now());
  return view.Available?new(view.Reason,view,patch,frame):Wait(view.Reason);
 }
}
