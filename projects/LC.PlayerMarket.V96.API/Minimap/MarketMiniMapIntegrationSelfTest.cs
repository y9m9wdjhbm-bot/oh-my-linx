using System.Net;
using System.Reflection;
using System.Text.Json;
using LocalMiniMap;
using MemoryHub;
namespace LcPlayerMarket;
internal static class MarketMiniMapIntegrationSelfTest
{
 internal static async Task<int> RunAsync(string output)
 {
  Directory.CreateDirectory(output);var checks=new List<OfflineCheck>();
  void Check(string name,bool pass){checks.Add(new(name,pass,pass?"通過（離線 fake backend）":"不符合接線要求"));if(!pass)throw new InvalidOperationException(name);}
  try
  {
   var origin=DateTimeOffset.FromUnixTimeMilliseconds(1800000000000);var now=origin;
   var source=new SourceIdentity(123,133000000000000000,@"C:\offline\LC.exe","0x140000000",1,500000,new string('A',64),"offline-v46-source");
   const ulong selfId=1;const int x=32610,y=32778;string ownerIdentity="owner-current";bool shopUnknown=false;int sourceCallbacks=0;
   HubSnapshot Hub()=>new(){Source=source,State="Reading",PublishedAt=now,Fields=new(){["selfId"]=FieldReading.Observed(selfId,now),["position"]=FieldReading.Observed(new{x,y},now),
    ["nearbyPlayers"]=FieldReading.Observed(new{complete=true,items=new[]{new{id=10,localIdentity=ownerIdentity,kind="Player",rawType=".?AVPlayer@lineage@@",positionState="Observed",position=new{x=x+6,y},sampledAt=now}}},now),
    ["playerShopStates"]=FieldReading.Observed(new{complete=true,items=new[]{new{id=10,localIdentity=ownerIdentity,actionType=".?AVShopAction@lineage@@",shopState=shopUnknown?"Unknown":"ShopActionObserved",shopStateSampledAt=now,shopStateEvidence="ExactPlayerId_ComponentOwner_DirectAction_PrimaryRtti_ActionBaseHierarchy_ABClosed"}}},now)}};
   object Wire()=>new{pid=source.ProcessId,creationFileTime=source.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),imagePath=source.ImagePath,sha256=source.DiskSha256,imageBase=source.ImageBase,imageSize=source.ImageSize,selfId=selfId.ToString()};
   var calls=new List<string>();bool omitPosition=false;int map=4;
   Task<PatrolNavigationReply> Fake(HttpMethod method,string path,object? payload,CancellationToken token)
   {
    token.ThrowIfCancellationRequested();calls.Add(method+" "+path);var p=JsonSerializer.SerializeToElement(payload,HubProtocol.Json);
    if(path=="/api/capture")return Task.FromResult(new PatrolNavigationReply(HttpStatusCode.OK,JsonSerializer.SerializeToElement(new{ok=true,inputSent=false,readOnly=true,captureId=p.GetProperty("captureId").GetString(),expectedSource=p.GetProperty("expectedSource"),source=Wire(),mapId=4,x,y,standing=true,sampledAtUtc=now.ToString("O"),terminal=true,cleanupConfirmed=true,state="Completed",accepted=true,error=""})));
    var d=new Dictionary<string,object?>{["ok"]=true,["source"]=Wire(),["mapId"]=map,["jobId"]="nav-job",["arrivalRadius"]=2,["waypoint"]=new{mapId=4,x=x+9,y=y-8},["sampledAtUtc"]=now.ToString("O"),["state"]="Walking",["terminal"]=false,["completed"]=false,["cleanupConfirmed"]=false,["standing"]=false,["standingSamples"]=0,["distance"]=9};
    if(!omitPosition){d["x"]=x;d["y"]=y;}return Task.FromResult(new PatrolNavigationReply(HttpStatusCode.OK,JsonSerializer.SerializeToElement(d,HubProtocol.Json)));
   }
   await using var client=new PatrolNavigationClient(()=>{sourceCallbacks++;return Hub();},Fake,()=>now,(d,t)=>{t.ThrowIfCancellationRequested();now+=d;return Task.CompletedTask;},readOnly:false);
   var provider=new LocalTerrainProvider(Path.Combine(AppContext.BaseDirectory,"Navigation","_internal","resources"));var host=new MarketMiniMapHost(provider,()=>now);var window=new ClientFrame((nint)0x101,-1200,100,1280,960,96);
   var targetSnapshot=new ShopTargetSnapshot{SnapshotId="target-snapshot",SourceSessionKey=source.SessionKey,SelfId=selfId,RefreshedAt=origin};
   string run="scan-a";int pages=0;bool running=true;
   MarketListing Listing(string page,double captured)=>new(){SourceSessionKey=source.SessionKey,RequestId="child-"+page,Owner=new(){Id=10,LocalIdentity="owner-current"},ViewerDirection=page,CapturedAt=origin.AddSeconds(captured),State="OwnerAndResponseMatched",Complete=true,OwnerAndResponseMatched=true};
   ShopCommandResponse Broker()=>new(){At=now,CurrentSourceSessionKey=source.SessionKey,TargetSnapshot=targetSnapshot,Scan=new(){JobId=run,SourceSessionKey=source.SessionKey,TargetSnapshotId=targetSnapshot.SnapshotId,Running=running,CurrentOwnerId=10},Listings=pages==0?[]:pages==1?[Listing("ViewerBuy",3.2)]:[Listing("ViewerBuy",3.2),Listing("ViewerSell",6.4)]};
   Task<MiniMapPresentation> Present(MiniMapSelection? selected=null)=>host.BuildAsync(client.DisplaySnapshot,Hub(),Broker(),window,selected,default);
   var idle=await Present();Check("idle no Nav evidence waits",idle.View is null&&idle.Status.Contains("等待"));Check("idle display starts no navigation or terrain IO",calls.Count==0&&sourceCallbacks==0&&provider.FileReads==0);
   var observation=await client.CaptureAsync(default);Check("actual client accepted fake capture cached",client.DisplaySnapshot is{} pose&&pose.MapId==4&&pose.X==x&&pose.Y==y&&pose.SampledAt==now);Check("one requested capture only",calls.SequenceEqual(["POST /api/capture"]));
   var first=await Present();Check("actual capture cache to adapter to renderer",first.View?.Available==true&&first.Frame?.Scope.Map==4);Check("bounded local terrain from actual Nav catalog",first.Terrain?.Cells.Length==16641&&provider.LoadedChunks is>0 and<=16);Check("right upper slot negative physical origin",first.View!.Slot==new Rectangle(-252,112,320,240));Check("unsearched merchant red first blink phase",first.View.Dots.Single().Color==Color.Red);
   int transportBefore=calls.Count,sourceBefore=sourceCallbacks,filesBefore=provider.FileReads;
   for(int i=0;i<5;i++)await Present();Check("HUD updates add zero capture poll commands or source reads",calls.Count==transportBefore&&sourceCallbacks==sourceBefore);Check("unchanged local patch cache zero additional disk reads",provider.FileReads==filesBefore);
   var request=new PatrolNavigationRequest("nav-job",observation,new("point","fake target","4","map",x+9,y-8),2);await client.StartAsync(request,default);
   var walking=await Present();Check("actual validated navigate reply creates independent target",walking.View?.Target is not null);Check("no invented route absent protocol route",walking.View!.Route.Length==0);Check("private bridge does not invent scene",walking.Frame!.Scope.Scene is null);
   now=origin.AddSeconds(.3);await client.GetStateAsync(request.JobId,default);var blinking=await Present();Check("query owner red green flicker",blinking.View!.Dots.Single().Querying&&blinking.View.Dots.Single().Color==Color.LimeGreen);
   now=origin.AddSeconds(3.2);pages=1;await client.GetStateAsync(request.JobId,default);var buy=await Present();Check("one successful page not queried both",!buy.View!.Dots.Single().Complete);
   now=origin.AddSeconds(6.4);pages=2;running=false;await client.GetStateAsync(request.JobId,default);var done=await Present();Check("sequential pages 3.2sec apart green via actual snapshot adapter",done.View!.Dots.Single().Complete&&done.View.Dots.Single().Color==Color.LimeGreen);Check("historical last query time distinct from current position",done.View.Dots.Single().LastQueriedAt==origin.AddSeconds(6.4));
   using(var bmp=Renderer.Bitmap(done.View,done.Terrain!,1,now)){bmp.Save(Path.Combine(output,"market46-connected-complete.png"));var p=done.View.Dots.Single().Point;var color=bmp.GetPixel((int)p.X,(int)p.Y);Check("actual host render green pixel",color.G>150&&color.R<100);}
   var selected=new MiniMapSelection(source.SessionKey,10,ownerIdentity,"user-selected-target");var selectedView=await Present(selected);Check("fresh selected owner creates independent target marker",selectedView.View!.Target!=done.View.Target);
   var unknownSelection=await Present(selected with{Owner=999});Check("unknown selected owner not plotted from saved world guess",unknownSelection.View!.Target==done.View.Target);
   now=origin.AddSeconds(8.401);var stale=await Present();Check("older than 2sec Nav map hides waits despite fresh Hub",stale.View is null&&stale.Status.Contains("等待"));Check("stale display triggers no read capture",calls.Count==5);
   now=origin.AddSeconds(60);await client.GetStateAsync(request.JobId,default);var later=await Present();Check("fresh existing state restores same-run green after60sec",later.View!.Dots.Single().Complete&&later.View.Dots.Single().LastQueriedAt==origin.AddSeconds(6.4));
   ownerIdentity="new-owner-instance";var reused=await Present();Check("owner instance change withdraws green",!reused.View!.Dots.Single().Complete);ownerIdentity="owner-current";var returned=await Present();Check("old owner identity cannot resurrect pre-change event",!returned.View!.Dots.Single().Complete);
   run="scan-b";var newRun=await Present();Check("new scan run clears old completion",!newRun.View!.Dots.Single().Complete);host.Reset();var cleared=await Present();Check("explicit host clear prevents old receipt resurrection",!cleared.View!.Dots.Single().Complete);
   shopUnknown=true;var unknown=await Present();Check("unknown shop state not valid marker",unknown.View!.Dots.Length==0);shopUnknown=false;
   var changedHub=Hub() with{Source=source with{CreationFileTime=source.CreationFileTime+1}};var rejected=await host.BuildAsync(client.DisplaySnapshot,changedHub,Broker(),window,null,default);Check("same pid different creation source rejected",rejected.View is null);
   omitPosition=true;await client.GetStateAsync(request.JobId,default);Check("actual state missing position does not use control fallback",client.DisplaySnapshot is null);omitPosition=false;map=99999;await client.GetStateAsync(request.JobId,default);var unknownMap=await Present();Check("unknown current map never reuses map4 patch",unknownMap.View is null&&unknownMap.Status.Contains("地形"));
   Check("70percent transparency constant and noactivate styles",LocalOverlay.GlobalOpacity==.30&&(LocalOverlay.ExtendedStyles&(0x20|0x08000000))==(0x20|0x08000000));
   Check("fake client never owns live backend child",typeof(PatrolNavigationClient).GetField("process",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(client)is null);
   // Deterministic async cancellation fence: old terrain completion cannot restore a cleared display.
   var blockedTerrain=new BlockedTerrain(first.Terrain!);var fencedHost=new MarketMiniMapHost(blockedTerrain,()=>now);
   var pending=fencedHost.BuildAsync(new(source,selfId,4,x,y,now),Hub(),Broker(),window,null,default);
   await blockedTerrain.Entered.Task;fencedHost.Reset();blockedTerrain.Release.TrySetResult();var afterReset=await pending;
   Check("reset while terrain awaits cannot restore old frame",afterReset.View is null&&afterReset.Status.Contains("新的"));
   // A duplicate/malformed manifest must not publish a half-parsed table.
   string badRoot=Path.Combine(output,"manifest-atomic");Directory.CreateDirectory(badRoot);
   string resource=Path.Combine(AppContext.BaseDirectory,"Navigation","_internal","resources"),key=$"{x/64+0x7e00:x4}{y/64+0x7e00:x4}";
   using(var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(resource,"terrain_catalog_manifest_v1.json"))))
   {
    var row=manifest.RootElement.GetProperty("chunks").EnumerateArray().First(r=>r.GetProperty("success").GetBoolean()&&r.GetProperty("mapDirectoryId").GetInt32()==4&&r.GetProperty("chunk").GetString()==key).Clone();
    File.WriteAllText(Path.Combine(badRoot,"terrain_catalog_manifest_v1.json"),JsonSerializer.Serialize(new{chunks=new[]{row,row}}));
    var atomic=new LocalTerrainProvider(badRoot);Check("duplicate manifest rejected without half table",await atomic.GetAsync(4,x,y,default)is null&&typeof(LocalTerrainProvider).GetField("entries",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(atomic)is null);
    string localChunk=Path.Combine(badRoot,"terrain_catalog_v1","4");Directory.CreateDirectory(localChunk);File.Copy(Path.Combine(resource,"terrain_catalog_v1","4",key+".attributes.u16le"),Path.Combine(localChunk,key+".attributes.u16le"));
    File.WriteAllText(Path.Combine(badRoot,"terrain_catalog_manifest_v1.json"),JsonSerializer.Serialize(new{chunks=new[]{row}}));
    Check("fixed manifest reparsed atomically on next attempt",await atomic.GetAsync(4,x,y,default)is not null&&atomic.FileReads==3);
   }
   File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=checks.Count,Failed=0,Checks=checks,FakeTransport=calls,NavSourceCallbackCount=sourceCallbacks,TerrainFileReads=provider.FileReads,TerrainLoadedChunks=provider.LoadedChunks,TerrainPatchBuilds=provider.PatchBuilds,GuiOpened=false,FormsCreated=false,LcReads=0,GameInputs=0,UserDataTouched=false,PreciseViewportVerified=false,Clock="deterministic synthetic event time; no live latency claim"},HubProtocol.Json));return 0;
  }
  catch(Exception e){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=checks.Count(c=>c.Passed),Failed=1,Checks=checks,Error=e.ToString(),GuiOpened=false,LcReads=0,GameInputs=0,UserDataTouched=false},HubProtocol.Json));return 1;}
 }
 private sealed class BlockedTerrain(TerrainPatch patch):ILocalTerrainProvider
 {
  public string? Error=>null;
  internal TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public async Task<TerrainPatch?> GetAsync(int map,int x,int y,CancellationToken token){Entered.TrySetResult();await Release.Task.WaitAsync(token);return patch;}
 }
}
