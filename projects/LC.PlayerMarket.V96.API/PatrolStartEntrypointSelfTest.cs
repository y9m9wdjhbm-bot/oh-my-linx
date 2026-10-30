using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class PatrolStartEntrypointSelfTest
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<object>();int failed=0;
        nint foreground=GetForegroundWindow();
        foreach(string entry in new[]{"home","route","reset-busy","invalid-route"})
        foreach(string previous in new[]{"Completed","Failed"})
        {
            string root=Path.Combine(output,entry+"-"+previous);Directory.CreateDirectory(root);
            var source=new FakeSource();var commands=new ForbiddenCommands();
            var store=new LocalStore(Path.Combine(root,"state"));store.SavePreferences(new(){LogEnabled=true,ShowNavigation=false,ShowSearchOverlay=false});
            // A real saved route is required before production can reach the intended source/busy refusal.
            var routes=new PatrolRouteStore(store.Root);
            if(!routes.TrySave([new PatrolWaypoint(Guid.NewGuid().ToString("N"),"offline fixture point","4","fixture map",32768,32768)],
                3,2,out var saved,out var saveError))throw new InvalidOperationException(saveError);
            var loaded=routes.Load();
            if(!loaded.Success||loaded.Snapshot is null||loaded.Snapshot.Points.Length!=1||loaded.Snapshot.Revision!=saved!.Revision)
                throw new InvalidDataException("Isolated saved route did not round-trip through the production store.");
            PatrolRouteRules.Validate(loaded.Snapshot);
            var backend=new StandaloneMarketBackend(Path.Combine(root,"fake-backend"),_=>source,(_,_,_)=>commands);
            SearchOverlayView? view=null;string reason="";string? observedException=null;bool childStarted=false;DateTimeOffset at=DateTimeOffset.UtcNow;
            try
            {
                using(var form=new MainForm(store,backend))
                {
                    Field("patrolOverlayView").SetValue(form,PatrolOverlayPresentation.Build(previous,"old fixture reason",at.AddMinutes(-1)));
                    // actionBusy alone is not the production SearchMutationAllowed gate; this case must still reject missing source.
                    if(entry=="route")Field("actionBusy").SetValue(form,true);
                    if(entry=="reset-busy")Field("resettingSearch").SetValue(form,true);
                    at=DateTimeOffset.UtcNow;
                    var method=typeof(MainForm).GetMethod(entry=="home"?"StartOrStopSavedPatrolAsync":"RunPatrolAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
                    try { ((Task)method.Invoke(form,entry=="home"?null:new object[]{entry=="invalid-route"?new PatrolRouteSnapshot():loaded.Snapshot,false})!).GetAwaiter().GetResult(); }
                    catch(InvalidOperationException error)when(entry!="invalid-route"){reason=error.Message;observedException=error.GetType().FullName;}
                    catch(InvalidDataException error)when(entry=="invalid-route"){reason=error.Message;observedException=error.GetType().FullName;}
                    view=(SearchOverlayView?)Field("patrolOverlayView").GetValue(form);
                    if(entry=="home")reason=MarketNativeCompatibility.Assess(null).Message;
                    var client=(PatrolNavigationClient)Field("patrolNavigation").GetValue(form)!;
                    childStarted=typeof(PatrolNavigationClient).GetField("process",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(client)is not null;
                    if(form.IsHandleCreated||form.Visible)throw new InvalidOperationException("Fixture unexpectedly showed or created a MainForm HWND.");
                }
                var logs=Directory.GetFiles(MarketLogPaths.Application(store.DiagnosticsRoot),"*.jsonl").SelectMany(File.ReadLines).Select(line=>JsonDocument.Parse(line).RootElement.Clone()).ToArray();
                bool logged=logs.Any(line=>line.GetProperty("kind").GetString()=="PATROL_START_PREPARATION_FAILED"&&line.GetProperty("message").GetString()!.Contains(reason));
                var hud=PatrolOverlayPresentation.Combine(new("unused scan","","","",false),view,false,false,false,false);
                bool presentationExact=view?.TransitionAt is { } transition
                    && view.Detail==$"{transition.ToLocalTime():HH:mm:ss} {reason}";
                bool expectedReason=(entry is not ("home" or "route")||reason==MarketNativeCompatibility.Assess(null).Message)
                    &&(entry!="reset-busy"||reason=="原巡迴工作仍在執行或尚未釋放，不能同時建立另一個巡迴。");
                bool passed=presentationExact&&expectedReason&&view?.PatrolState=="Failed"&&view.TransitionAt>=at&&view.Detail.Contains(reason)&&!view.Detail.Contains("old fixture reason")
                    &&hud.State=="巡查失敗"&&logged&&(entry!="invalid-route"||observedException==typeof(InvalidDataException).FullName)&&commands.Calls==0&&!childStarted&&GetForegroundWindow()==foreground;
                if(!passed)failed++;
                checks.Add(new{Entry=entry,Previous=previous,Passed=passed,Reason=reason,ObservedException=observedException,ValidSavedRouteRoundTripped=true,InvalidRouteNegative=entry=="invalid-route",PresentationExact=presentationExact,ExpectedReasonMatched=expectedReason,ActionBusyAloneCase=entry=="route",ResettingSearchGateCase=entry=="reset-busy",View=view,Hud=hud,FailureLogged=logged,
                    BackendCalls=commands.Calls,NavigationChildStarted=childStarted,MainFormShown=false,GameOpened=false, Predicates=new {FailedState=view?.PatrolState=="Failed",FreshTransition=view?.TransitionAt>=at,CurrentReason=view?.Detail.Contains(reason)==true,OldReasonAbsent=view?.Detail.Contains("old fixture reason")==false,HudLabel=hud.State=="\u5de1\u67e5\u5931\u6557",Logged=logged,ZeroCommands=commands.Calls==0,NoChild=!childStarted,SameForeground=GetForegroundWindow()==foreground}});
            }
            catch(Exception error){failed++;checks.Add(new{Entry=entry,Previous=previous,Passed=false,Error=error.ToString()});}
            finally{backend.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        }
        bool sameForeground=GetForegroundWindow()==foreground;
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Success=failed==0&&sameForeground,Passed=8-failed,Failed=failed,
            ActualHomeAndRouteMethodsInvoked=true,ValidSavedRoutePreconditions=true,InvalidRouteNegativeIncluded=true,IndependentResettingSearchGuardIncluded=true,ForegroundUnchanged=sameForeground,GameOpened=false,GameInputSent=false,NavigationBackendStarted=false,Checks=checks}));
        return failed==0&&sameForeground?0:1;
    }
    private static FieldInfo Field(string name)=>typeof(MainForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private sealed class FakeSource:ISlicedMemorySource
    {
        public void Configure(ReadSelection selection){}
        public ReadResult Read(CancellationToken token){token.ThrowIfCancellationRequested();return new(null,new(),"OfflineEntrypointFixture","Fake source; no LC enumeration or memory access.");}
        public ReadResult ReadSlice(ReadSelection fields,CancellationToken token)=>Read(token);
        public void Dispose(){}
    }
    private sealed class ForbiddenCommands:IStandaloneShopCommands
    {
        internal int Calls;
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request,CancellationToken token){Calls++;throw new InvalidOperationException("Entrypoint fixture forbids backend commands.");}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
