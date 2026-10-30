using MemoryHub;
namespace LcPlayerMarket;
internal static class PatrolForegroundGateSelfTest
{
    internal static IEnumerable<OfflineCheck> Run()
    {
        var source = new SourceIdentity(42, 123, "LC.exe", "0", 0, 0, "", "session");
        var results = new List<OfflineCheck>();
        void Test(string name, Action run) { try { run(); results.Add(new(name, true, "No game input; mocked window API")); } catch(Exception e){results.Add(new(name,false,e.ToString()));} }
        void Need(bool value) { if(!value) throw new Exception("Assertion failed"); }

        void Run(Fake f, Func<SourceIdentity?>? current = null, CancellationToken token = default) => PatrolForegroundGate.TryOnce(source, current ?? (()=>source), f, token);
        void Fail(Fake f, Func<SourceIdentity?>? current = null) { try { Run(f,current); } catch(InvalidOperationException) { return; } throw new Exception("Expected closed gate"); }
        Test("foreground_already_verified",()=>{var f=new Fake{Foreground=true};Run(f);Need(f.Requests==0&&f.Restores==0);});
        Test("foreground_minimized_restore",()=>{var f=new Fake{Minimized=true,Allow=true};Run(f);Need(f.Restores==1&&f.Requests==1);});
        Test("foreground_normal_not_restored",()=>{var f=new Fake{Allow=true};Run(f);Need(f.Restores==0&&f.Requests==1);});
        Test("foreground_os_denied_continues_without_wait",()=>{var f=new Fake();Run(f);Need(f.Requests==1);});
        Test("foreground_missing_main_window",()=>{var f=new Fake{Window=0};Fail(f);Need(f.Requests==0);});
        Test("foreground_relogin_identity_rejected",()=>{var f=new Fake{Allow=true};Fail(f,()=>source with{CreationFileTime=124});Need(f.Requests==0);});
        Test("foreground_wrong_owner_rejected",()=>{var f=new Fake{Valid=false};Fail(f);Need(f.Requests==0);});
        Test("foreground_cancel_before_window_action",()=>{var f=new Fake();try{Run(f,token:new CancellationToken(true));throw new Exception("Expected cancellation");}catch(OperationCanceledException){}Need(f.Requests==0&&f.Restores==0);});
        Test("foreground_repeat_verified_click",()=>{var f=new Fake{Allow=true};Run(f);Run(f);Need(f.Requests==1);});
        Test("foreground_changed_during_request",()=>{var f=new Fake{Allow=true,InvalidateOnRequest=true};Fail(f);Need(f.Requests==1);});
        Test("foreground_repeat_denied_one_per_click",()=>{var f=new Fake();Run(f);Run(f);Need(f.Requests==2);});
        return results;
    }
    private sealed class Fake : IPatrolForegroundWindow
    {
        internal nint Window=1; internal bool Valid=true,Foreground,Minimized,Allow,InvalidateOnRequest;internal int Requests,Restores;
        public nint Resolve(SourceIdentity s)=>Window;
        public bool Matches(nint w,SourceIdentity s)=>Valid;
        public bool IsForeground(nint w,SourceIdentity s)=>Foreground;
        public bool IsMinimized(nint w)=>Minimized;
        public void Restore(nint w){Restores++;Minimized=false;}
        public void RequestForeground(nint w){Requests++;Foreground=Allow;if(InvalidateOnRequest)Valid=false;}
    }
}
