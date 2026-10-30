using System.Drawing;
using System.Globalization;
using System.Text.Json;
namespace LocalMiniMap;
/// <summary>Already-produced Nav service.snapshot() and V44 HubSnapshot/ShopCommandResponse only.
/// No HTTP heartbeat, process access, disk history, socket, sampling or command.</summary>
public sealed class ExistingSnapshotAdapter
{
 public Ledger? Ledger{get;private set;}string? snapshotId,runKey;DateTimeOffset scopeStartedAt,runStartedAt;readonly HashSet<string> received=[];
 readonly Dictionary<ulong,(string Identity,DateTimeOffset At)> ownerInstances=[];
 const int MaximumTrackedOwners=8192;bool clearPending;DateTimeOffset? clearedAt;
 public void Clear(){Ledger?.Cancel();snapshotId=null;runKey=null;received.Clear();ownerInstances.Clear();clearPending=true;Status="等待即時導航地圖資料";}
 public string Status{get;private set;}="等待即時導航地圖資料";
 static JsonElement P(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object?e.EnumerateObject().FirstOrDefault(p=>p.Name.Equals(key,StringComparison.OrdinalIgnoreCase)).Value:default;
 static string? S(JsonElement e,string key){var p=P(e,key);return p.ValueKind==JsonValueKind.String?p.GetString():p.ValueKind==JsonValueKind.Number?p.ToString():null;}
 static bool B(JsonElement e,string key)=>P(e,key).ValueKind==JsonValueKind.True;
 static long? N(JsonElement e,string key)=>long.TryParse(S(e,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)?n:null;
 static DateTimeOffset? T(JsonElement e,string key)=>DateTimeOffset.TryParse(S(e,key),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var t)?t:null;
 static JsonElement[] A(JsonElement e,string key){var a=P(e,key);return a.ValueKind==JsonValueKind.Array?a.EnumerateArray().Take(8193).ToArray():[];}
 static World? W(JsonElement e)=>N(e,"x") is{} x&&N(e,"y") is{} y&&new World(x,y).Valid?new(x,y):null;
 static JsonElement Field(JsonElement hub,string key,DateTimeOffset now){var f=P(P(hub,"fields"),key);return S(f,"state")=="Observed"&&T(f,"sampledAt") is{} at&&LocalMiniMap.Ledger.Fresh(at,now)?P(f,"value"):default;}
 public Frame? Adapt(JsonElement nav,JsonElement hub,JsonElement broker,Rectangle client,double dpi,DateTimeOffset now)
 {
  try{return AdaptCore(nav,hub,broker,client,dpi,now);}catch(Exception e)when(e is InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException){Status="等待有效即時資料";Ledger?.Cancel();snapshotId=null;received.Clear();return null;}
 }
 public Frame? AdaptBoundPose(BoundPose pose,JsonElement hub,JsonElement broker,Rectangle client,double dpi,DateTimeOffset now)
 {
  try{return AdaptCore(default,hub,broker,client,dpi,now,pose);}catch(Exception e)when(e is InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException){Status="等待有效即時資料";Ledger?.Cancel();snapshotId=null;received.Clear();return null;}
 }
 Frame? AdaptCore(JsonElement nav,JsonElement hub,JsonElement broker,Rectangle client,double dpi,DateTimeOffset now,BoundPose? verified=null)
 {
  Frame? Missing(string reason){Status=reason;Ledger?.Cancel();snapshotId=null;received.Clear();return null;}
  var source=P(hub,"source");if(N(source,"processId") is not{} pid||N(source,"creationFileTime") is not{} start||S(source,"sessionKey") is not{Length:>0} token||T(hub,"publishedAt") is not{} published||!LocalMiniMap.Ledger.Fresh(published,now)||S(hub,"state")=="Disconnected")return Missing("等待同來源新鮮資料");
  World self;ulong selfId;DateTimeOffset positionAt;int map;Scope scope;
  if(verified is{} pose)
  {
   scope=pose.Scope;self=pose.Self;selfId=scope.Self;positionAt=pose.At;map=scope.Map;
   if(!scope.Valid||scope.Pid!=pid||scope.Start!=start||scope.Token!=token||!self.Valid||!LocalMiniMap.Ledger.Fresh(positionAt,now))return Missing("等待同來源導航地圖資料");
  }
  else
  {
  var pos=P(nav,"position");if(W(pos) is not{} observedPosition||S(pos,"source") is not{} worldSource)return Missing("等待即時導航角色位置");self=observedPosition;
  var parts=worldSource.Split(':');if(parts.Length!=5||!int.TryParse(parts[0],out var npid)||npid!=pid||!long.TryParse(parts[1],out var nstart)||nstart!=start||!ulong.TryParse(parts[2],out selfId)||selfId==0||!ulong.TryParse(parts[3],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var scene)||scene==0||!ulong.TryParse(parts[4],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var mapdata)||mapdata==0)return Missing("導航與商店來源不一致");
  if(!double.TryParse(S(pos,"sampledAt"),NumberStyles.Float,CultureInfo.InvariantCulture,out var seconds)||!double.IsFinite(seconds)||seconds<0||seconds>253402300799)return Missing("導航角色時間未知");
  positionAt=DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds*1000));if(!LocalMiniMap.Ledger.Fresh(positionAt,now))return Missing("導航角色位置已過期");
  var maps=A(nav,"maps");var candidates=maps.Select(v=>v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var n)?n:-1).Distinct().ToArray();if(candidates.Length==0||candidates.Any(v=>v<0))return Missing("等待導航確認當前地圖");
  map=N(pos,"mapField") is{} field&&field<=int.MaxValue&&candidates.Contains((int)field)?(int)field:candidates.Length==1?candidates[0]:-1;if(map<0)return Missing("當前地圖尚不唯一");
  scope=new Scope((int)pid,start,token,selfId,map,parts[3]+":"+parts[4]);
  }
  var sid=Field(hub,"selfId",now);if(sid.ValueKind!=JsonValueKind.Number||!sid.TryGetUInt64(out var observedSelf)||observedSelf!=selfId)return Missing("等待同角色新鮮身份");
  if(W(Field(hub,"position",now)) is not{} hubPosition||hubPosition!=self)return Missing("等待導航與商店位置一致");
  if(!scope.Valid)return Missing("來源身份無效");
  if(Ledger is null){Ledger=new(scope);scopeStartedAt=now;}else if(Ledger.Scope!=scope){Ledger.Change(scope);snapshotId=null;received.Clear();ownerInstances.Clear();scopeStartedAt=now;}
  if(clearPending){clearedAt=now;clearPending=false;}
  var nearby=Field(hub,"nearbyPlayers",now);var shops=Field(hub,"playerShopStates",now);List<Merchant> merchants=[];
  var nearRows=A(nearby,"items");var shopRows=A(shops,"items");
  if(B(nearby,"complete")&&B(shops,"complete")&&nearRows.Length<=8192&&shopRows.Length<=8192){var states=shopRows.Where(s=>N(s,"id")>0).GroupBy(s=>N(s,"id")!.Value).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single());
   foreach(var row in nearRows){if(N(row,"id") is not{} id||id<=0||S(row,"kind")!="Player"||S(row,"rawType")!=".?AVPlayer@lineage@@"||S(row,"localIdentity") is not{Length:>0} identity||S(row,"positionState")!="Observed"||W(P(row,"position")) is not{} point||T(row,"sampledAt") is not{} at||!LocalMiniMap.Ledger.Fresh(at,now)||!states.TryGetValue(id,out var shop)||S(shop,"localIdentity")!=identity||T(shop,"shopStateSampledAt") is not{} shopAt)continue;
    bool shopVerified=S(shop,"shopState")=="ShopActionObserved"&&S(shop,"actionType")==".?AVShopAction@lineage@@"&&S(shop,"shopStateEvidence")=="ExactPlayerId_ComponentOwner_DirectAction_PrimaryRtti_ActionBaseHierarchy_ABClosed";
    merchants.Add(new((ulong)id,identity,scope,point,at,shopVerified,shopAt));}}
  // Duplicates are never resolved by array order, including summary current-owner joins.
  merchants=merchants.GroupBy(m=>m.Id).Where(g=>g.Count()==1).Select(g=>g.Single()).ToList();
  if(ownerInstances.Count+merchants.Count(m=>!ownerInstances.ContainsKey(m.Id))>MaximumTrackedOwners)
  {Ledger.Cancel();received.Clear();ownerInstances.Clear();clearedAt=now;}
  foreach(var m in merchants){if(!ownerInstances.TryGetValue(m.Id,out var previous)||previous.Identity!=m.Identity){Ledger.ForgetOwner(m.Id);ownerInstances[m.Id]=(m.Identity,now);}}
  if(T(broker,"at") is{} bat&&LocalMiniMap.Ledger.Fresh(bat,now)&&S(broker,"currentSourceSessionKey")==token){var scan=P(broker,"scan");var targetSnapshot=P(broker,"targetSnapshot");string? snap=S(scan,"targetSnapshotId")??S(targetSnapshot,"snapshotId");
   bool bound=snap is{Length:>0}&&S(targetSnapshot,"snapshotId")==snap&&S(targetSnapshot,"sourceSessionKey")==token&&selfId<=long.MaxValue&&N(targetSnapshot,"selfId")== (long)selfId&&(S(scan,"sourceSessionKey") is null||S(scan,"sourceSessionKey")==token)&&T(targetSnapshot,"refreshedAt") is{} refreshed&&refreshed<=bat;
   if(bound){string nextRun=snap+"|"+(S(scan,"jobId")??"manual");if(runKey!=nextRun){Ledger.Cancel();snapshotId=snap;runKey=nextRun;received.Clear();runStartedAt=now;}
    foreach(var listing in A(broker,"listings")){var owner=P(listing,"owner");var merchant=merchants.SingleOrDefault(m=>m.Id==(ulong)(N(owner,"id")??0)&&m.Identity==S(owner,"localIdentity"));var direction=S(listing,"viewerDirection");var child=S(listing,"requestId");var captured=T(listing,"capturedAt");if(merchant is null||child is not{Length:>0}||captured is null||captured>now||S(listing,"sourceSessionKey")!=token||S(listing,"state")!="OwnerAndResponseMatched"||!B(listing,"complete")||!B(listing,"ownerAndResponseMatched")||direction is not("ViewerBuy" or "ViewerSell"))continue;
     if(captured.Value<scopeStartedAt||captured.Value<runStartedAt||captured.Value<ownerInstances[merchant.Id].At||captured.Value<T(targetSnapshot,"refreshedAt")||clearedAt is{} barrier&&captured.Value<=barrier)continue;
     string unique=snap+"|"+child+"|"+direction;if(received.Count>=4096){Ledger.Cancel();received.Clear();}if(received.Add(unique))Ledger.MatchedListing(merchant,direction=="ViewerBuy"?Page.Buy:Page.Sell,snap!,child,captured.Value,now);}
    if(B(scan,"running")&&!B(scan,"paused")&&!B(scan,"pauseRequested")&&N(scan,"currentOwnerId") is{} activeId){var active=merchants.SingleOrDefault(m=>m.Id==(ulong)activeId);if(active is not null)Ledger.ScanOwner(active,snap!,bat);else Ledger.ClearActive();}
    else Ledger.ClearActive();
   }else Ledger.Cancel();
  }else Ledger.ClearActive();
  Target? target=null;var destination=P(nav,"journey");if(N(destination,"targetMap")==map&&A(destination,"target") is{Length:2} xy&&xy[0].TryGetInt32(out var tx)&&xy[1].TryGetInt32(out var ty))target=new(scope,new(tx,ty),"nav-journey-target",positionAt);
  var route=A(nav,"route").Select(row=>row.ValueKind==JsonValueKind.Array&&row.GetArrayLength()==2&&row[0].TryGetInt32(out var rx)&&row[1].TryGetInt32(out var ry)?new World(rx,ry):new World(double.NaN,double.NaN)).ToArray();
  if(verified is{} bounded){target=bounded.Target;route=bounded.Route;}
  Status="周邊地圖（近似範圍）";return new(scope,positionAt,self,client,dpi,merchants.ToArray(),target,route,positionAt,scope);
 }
}
