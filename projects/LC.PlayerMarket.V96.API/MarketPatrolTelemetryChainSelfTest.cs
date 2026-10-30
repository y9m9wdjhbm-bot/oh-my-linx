using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
namespace LcPlayerMarket;
internal static class MarketPatrolTelemetryChainSelfTest
{
    internal static async Task<int> RunAsync(string fixturePath, string output)
    {
        var fixture=JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        var before=fixture["before"]!.AsObject();var moving=fixture["moving"]!.AsObject();var completed=fixture["completed"]!.AsObject();
        var wire=before["source"]!.AsObject();
        var source=new SourceIdentity((int)wire["pid"]!,long.Parse((string)wire["creationFileTime"]!,CultureInfo.InvariantCulture),
            (string)wire["imagePath"]!,(string)wire["imageBase"]!,0,(uint)wire["imageSize"]!,(string)wire["sha256"]!,"offline-adapter-chain-session");
        ulong self=ulong.Parse((string)wire["selfId"]!,CultureInfo.InvariantCulture);
        DateTimeOffset now=DateTimeOffset.Parse((string)completed["sampledAtUtc"]!,CultureInfo.InvariantCulture);
        var expected=new PatrolObservation(source,self,4,(int)before["x"]!,(int)before["y"]!,DateTimeOffset.Parse((string)before["sampledAtUtc"]!),true);
        var point=new PatrolWaypoint("fixture-point","Fixture waypoint","4","Fixture map",20,10);
        var calls=new List<string>();var receipts=new List<PatrolNavigationState>();int statusReads=0;
        string ownedId="";
        HubSnapshot Snapshot()=>new() {Source=source,State="Reading",PublishedAt=now,Fields=new() {
            ["selfId"]=FieldReading.Observed(self,now),["position"]=FieldReading.Observed(new {x=10,y=10},now)}};
        Task Delay(TimeSpan amount,CancellationToken token){token.ThrowIfCancellationRequested();now+=amount;return Task.CompletedTask;}
        await using var client=new PatrolNavigationClient(Snapshot,(method,path,payload,token)=>
        {
            token.ThrowIfCancellationRequested();calls.Add(method.Method+" "+path);
            JsonObject body;
            if(path=="/api/navigate") {ownedId=JsonSerializer.SerializeToElement(payload).GetProperty("jobId").GetString()!;body=(JsonObject)before.DeepClone();}
            else if(path.StartsWith("/api/status?",StringComparison.Ordinal)) {statusReads++;body=(JsonObject)(statusReads==1?moving:completed).DeepClone();}
            else throw new InvalidOperationException("Unexpected backend call: "+path);
            body["jobId"]=ownedId;
            return Task.FromResult(new PatrolNavigationReply(HttpStatusCode.OK,JsonSerializer.SerializeToElement(body)));
        },()=>now,Delay,readOnly:false);
        var market=new ForbiddenMarket();var controller=new PatrolController(client,market,()=>now,Delay);
        var progress=new List<PatrolProgress>();var views=new List<SearchOverlayView>();
        controller.Progress+=value=>{progress.Add(value);views.Add(PatrolOverlayPresentation.Build(value.State,value.Detail,now,value));};
        var stateType=typeof(PatrolController).GetNestedType("RunState",BindingFlags.NonPublic)!;
        object run=Activator.CreateInstance(stateType,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,new object[]{"offline-chain-run"},null)!;
        stateType.GetField("Point",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(run,point);
        stateType.GetField("Origin",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(run,expected);
        stateType.GetField("Route",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(run,new PatrolRouteSnapshot {Points=[point],ArrivalRadius=1});
        var navigate=typeof(PatrolController).GetMethod("NavigateAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await ((Task)navigate.Invoke(controller,new object[]{run,expected,CancellationToken.None})!);
        var confirmed=views.SingleOrDefault(value=>value.PatrolState=="MovingConfirmed");
        bool clean=(bool)stateType.GetField("NavigationSettled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(run)!;
        bool passed=confirmed is not null&&confirmed.State=="角色正在行走"&&confirmed.Detail.Contains("11,10")
            &&progress.Any(value=>value.State=="Arriving")&&statusReads==2&&market.Calls==0&&clean;
        File.WriteAllText(output,JsonSerializer.Serialize(new {Success=passed,RealCompiledClient=true,RealCompiledController=true,
            RealHudPresentation=true,AdapterFixture=fixturePath,Progress=progress,Views=views,StatusReads=statusReads,Calls=calls,
            NavigationCleanupConfirmed=clean,MarketCalls=market.Calls,GameOpened=false,GameInputSent=false,GuiStarted=false,BackendStarted=false,MutexAcquired=false}));
        return passed?0:1;
    }
    private sealed class ForbiddenMarket:IPatrolMarket
    {
        internal int Calls;
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request,CancellationToken token)
        {Calls++;throw new InvalidOperationException("Telemetry chain must not send shop operations.");}
    }
}
