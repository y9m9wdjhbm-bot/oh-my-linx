using System.Diagnostics;
using System.Text.Json;
using LocalMiniMap;
using MemoryHub;
namespace LcPlayerMarket;
internal static class MarketMiniMapDeliverySelfTest
{
 internal static async Task<int> RunAsync(string output)
 {
  Directory.CreateDirectory(output);var checks=new List<OfflineCheck>();
  void Check(string name,bool pass){checks.Add(new(name,pass,"offline synthetic fixture"));if(!pass)throw new InvalidOperationException(name);}
  try
  {
   var now=DateTimeOffset.FromUnixTimeMilliseconds(1800000000000);
   var source=new SourceIdentity(123,133000000000000000,"offline.exe","0x140000000",1,500000,new string('A',64),"offline-delivery");
   int map=4,x=32610,y=32778,count=1;string identity="owner-current";
   HubSnapshot Hub()=>new(){Source=source,State="Reading",PublishedAt=now,Fields=new(){
    ["selfId"]=FieldReading.Observed(1UL,now),["position"]=FieldReading.Observed(new{x,y},now),
    ["nearbyPlayers"]=FieldReading.Observed(new{complete=true,items=Enumerable.Range(0,count).Select(i=>new{id=10+i,localIdentity=identity+i,kind="Player",rawType=".?AVPlayer@lineage@@",positionState="Observed",position=new{x=x+6+i%4,y=y+i/4},sampledAt=now}).ToArray()},now),
    ["playerShopStates"]=FieldReading.Observed(new{complete=true,items=Enumerable.Range(0,count).Select(i=>new{id=10+i,localIdentity=identity+i,actionType=".?AVShopAction@lineage@@",shopState="ShopActionObserved",shopStateSampledAt=now,shopStateEvidence="ExactPlayerId_ComponentOwner_DirectAction_PrimaryRtti_ActionBaseHierarchy_ABClosed"}).ToArray()},now)}};
   VerifiedDisplayPose Pose()=>new(source,1,map,x,y,now,"target",x+9,y-8,4);
   var provider=new LocalTerrainProvider(Path.Combine(AppContext.BaseDirectory,"Navigation","_internal","resources"));
   var host=new MarketMiniMapHost(provider,()=>now);var window=new ClientFrame((nint)1,0,0,1280,960,96);
   var broker=new ShopCommandResponse();
   Task<MiniMapPresentation> Show(MiniMapSelection? selected=null)=>host.BuildAsync(Pose(),Hub(),broker,window,selected,default);
   var first=await Show(new(source.SessionKey,10,identity+0,"clicked-history-owner"));
   Check("history target uses exact fresh owner world coordinate",first.Frame?.Target?.Position==new World(x+6,y));
   var expected=Projection.Project(new(x+6+.5,y+.5),new(x+.5,y+.5),1);
   Check("history target world to exact renderer pixel",first.View?.Target==expected&&expected==new System.Drawing.PointF(184,120));
   var wrongSource=await Show(new("wrong-source",10,identity+0,"clicked"));
   Check("wrong selection source cannot substitute owner target",wrongSource.Frame?.Target?.Position==new World(x+9,y-8));
   var wrongOwner=await Show(new(source.SessionKey,10,"reused-wrong-owner-instance","clicked"));
   Check("wrong selection owner identity cannot substitute target",wrongOwner.Frame?.Target?.Position==new World(x+9,y-8));
   var unknownOwner=await Show(new(source.SessionKey,999,identity+0,"clicked"));
   Check("missing clicked owner cannot substitute saved coordinate",unknownOwner.Frame?.Target?.Position==new World(x+9,y-8));
   int generation=host.Generation;var projected=await Show();host.Reset();
   Check("completed projection rejected by final UI generation guard",projected.View is not null&&!host.IsCurrentGeneration(generation));
   map=1;x=32669;y=32802;var changed=await Show();
   Check("actual terrain map switch carries only current map",changed.Frame?.Scope.Map==1&&changed.Terrain?.Map==1&&host.LedgerForPresentation?.Scope.Map==1);
   Check("navigation waypoint from different map suppressed",changed.Frame?.Target is null&&changed.View?.Target is null);
   var switchedSelected=await Show(new(source.SessionKey,10,identity+0,"current-map-owner"));
   Check("current map selection uses fresh current map position",switchedSelected.Frame?.Target?.Scope.Map==1&&switchedSelected.Frame.Target.Position==new World(x+6,y));
   // Scope transition invalidates previous historical completion, tested on actual ledger.
   var ledger=host.LedgerForPresentation!;var merchant=switchedSelected.Frame!.Merchants.Single();
   foreach(var page in Enum.GetValues<Page>())ledger.MatchedListing(merchant,page,"snapshot","child"+page,now,now);
   Check("current scope completion fixture established",ledger.Complete(merchant,now));
   map=4;x=32610;y=32778;var back=await Show();Check("map return cannot resurrect prior map completion",!back.View!.Dots.Single().Complete);
   // One and bounded 32-owner/2048-product fixtures use actual host with warm terrain.
   var performance=new List<object>();
   foreach(int owners in new[]{1,32})
   {
    count=owners;var hub=Hub();
    var rich=new ShopCommandResponse{At=now,CurrentSourceSessionKey=source.SessionKey,Listings=Enumerable.Range(0,owners).Select(i=>new MarketListing{SourceSessionKey=source.SessionKey,Owner=new(){Id=(ulong)(10+i),LocalIdentity=identity+i},Rows=Enumerable.Range(0,64).Select(j=>new PersonalShopRow(j,"Observed",true,"offline-product-"+j,"123",123,"unit",null,"Unverified","fixture")).ToArray()}).ToArray()};
    await host.BuildAsync(Pose(),hub,rich,window,null,default);int reads=provider.FileReads;long allocated=GC.GetTotalAllocatedBytes(true);var timer=Stopwatch.StartNew();
    for(int i=0;i<16;i++){var view=await host.BuildAsync(Pose(),hub,rich,window,null,default);if(view.View?.Available!=true)throw new InvalidOperationException("performance fixture unavailable");}
    timer.Stop();allocated=GC.GetTotalAllocatedBytes(true)-allocated;
    performance.Add(new{Owners=owners,ProductRows=owners*64,Iterations=16,AllocatedBytes=allocated,AllocatedBytesPerUpdate=allocated/16.0,MonotonicTotalMs=timer.Elapsed.TotalMilliseconds,MonotonicMsPerUpdate=timer.Elapsed.TotalMilliseconds/16,TerrainAdditionalReads=provider.FileReads-reads});
    Check("warm "+owners+" owner updates add zero terrain reads",provider.FileReads==reads);
   }
   var cache=(System.Collections.IDictionary)typeof(LocalTerrainProvider).GetField("cache",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(provider)!;
   Check("terrain chunk cache bounded to 24",cache.Count<=24);
   var adapter=(ExistingSnapshotAdapter)typeof(MarketMiniMapHost).GetField("adapter",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(host)!;
   var ownersSeen=(Dictionary<ulong,(string Identity,DateTimeOffset At)>)typeof(ExistingSnapshotAdapter).GetField("ownerInstances",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(adapter)!;
   ownersSeen.Clear();for(ulong id=1000;id<9192;id++)ownersSeen[id]=("old-owner",now.AddSeconds(-60));
   await Show();Check("owner capacity reset rebuilds complete current frame",ownersSeen.Count==32&&ownersSeen.Keys.All(id=>id>=10&&id<42));
   host.Reset();Check("explicit reset clears tracked owner identities",ownersSeen.Count==0);await Show();
   ownersSeen[999]=("prior-map-owner",now);map=1;x=32669;y=32802;await Show();
   Check("map change clears prior owner tracking",ownersSeen.Count==32&&!ownersSeen.ContainsKey(999));
   var mismatched=await host.BuildAsync(Pose(),Hub() with{Source=source with{CreationFileTime=source.CreationFileTime+1}},broker,window,null,default);
   Check("source change clears prior identities and display",ownersSeen.Count==0&&mismatched.View is null);
   await Show();now=now.AddMilliseconds(1);
   broker=new ShopCommandResponse{At=now,CurrentSourceSessionKey=source.SessionKey,TargetSnapshot=new(){SnapshotId="reset-proof",SourceSessionKey=source.SessionKey,SelfId=1,RefreshedAt=now},Scan=new(){JobId="reset-proof-run",SourceSessionKey=source.SessionKey,TargetSnapshotId="reset-proof"},Listings=new[]{"ViewerBuy","ViewerSell"}.Select(page=>new MarketListing{SourceSessionKey=source.SessionKey,RequestId="old-"+page,Owner=new(){Id=10,LocalIdentity=identity+0},ViewerDirection=page,CapturedAt=now,State="OwnerAndResponseMatched",Complete=true,OwnerAndResponseMatched=true}).ToArray()};
   var beforeClear=await Show();Check("same-time reset fixture starts genuinely queried green",beforeClear.View!.Dots.Single(d=>d.Id==10).Complete);
   host.Reset();var afterClear=await Show();Check("same timestamp old receipts cannot resurrect after reset",!afterClear.View!.Dots.Single(d=>d.Id==10).Complete);
   now=now.AddMilliseconds(1);broker=broker with{At=now,Listings=broker.Listings.Select(l=>l with{RequestId="new-"+l.RequestId,CapturedAt=now}).ToArray()};
   var queriedAgain=await Show();Check("new successful post-reset receipts restore green",queriedAgain.View!.Dots.Single(d=>d.Id==10).Complete);
   Check("HUD coordinator owns no app mutex or transport",typeof(MarketMiniMapHost).GetFields(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).All(f=>!typeof(Mutex).IsAssignableFrom(f.FieldType)&&!typeof(HttpClient).IsAssignableFrom(f.FieldType)));
   File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=checks.Count,Failed=0,Checks=checks,Performance=performance,GuiOpened=false,FormsCreated=false,LcReads=0,GameInputs=0,UserDataTouched=false,TransportCalls=0,AppMutexAcquisitions=0,Timing="real monotonic host computation; synthetic fixture timestamps, no live latency claim"},HubProtocol.Json));return 0;
  }catch(Exception e){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=checks.Count(c=>c.Passed),Failed=1,Checks=checks,Error=e.ToString(),GuiOpened=false,LcReads=0,GameInputs=0},HubProtocol.Json));return 1;}
 }
}

