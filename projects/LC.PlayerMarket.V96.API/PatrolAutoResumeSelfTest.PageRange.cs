using MemoryHub;
namespace LcPlayerMarket;

internal static partial class PatrolAutoResumeSelfTest
{
    internal static async Task<OfflineCheck[]> RunPageRangeRecoveryAsync()
    {
        var checks=new List<OfflineCheck>();
        async Task Check(string name,Func<Task> body){try{await body();checks.Add(new("page_range_recovery_"+name,true,"Production controller and fake owned ports; parser proof is checked separately; no LC/input."));}catch(Exception e){checks.Add(new("page_range_recovery_"+name,false,e.ToString()));}}
        PatrolController Controller(Fixture f,IPatrolMarket market)=>new(f.Nav,market,()=>f.Clock,(interval,token)=>
        {token.ThrowIfCancellationRequested();if(interval>=TimeSpan.FromSeconds(2)){f.Cooldowns.Add(interval.TotalSeconds);f.OnCooldown?.Invoke(interval.TotalSeconds);}return Task.CompletedTask;},TimeSpan.FromMilliseconds(250),forceDispatch:true,checkpointStore:f.Store,autoResume:true,continuousRecovery:true);
        await Check("three_verified_refusals_continue_original_plan",async()=>
        {
            var f=new Fixture(points:1,retries:3);f.Market.PreflightFailures=3;
            var result=await Controller(f,new PageRangeScanMarket(f.Market)).RunAsync(f.Route);
            Need(result.State=="Completed"&&f.Market.Scans==1&&f.Market.Continues==3&&f.Cooldowns.Count==3,result.Detail);
            Need(f.Market.Commands.Where(r=>r.Command==ShopCommands.ContinueSavedScan).All(r=>r.ExpectedScanPlanId=="plan-1"),"Plan changed or submitted pages replayed");
        });
        foreach(string mutation in new[]{"proof","source","uncertain"})
        await Check("unproven_"+mutation+"_does_not_retry",async()=>
        {
            var f=new Fixture(points:1,retries:0);f.Market.PreflightFailures=20;
            var result=await Controller(f,new PageRangeScanMarket(f.Market){Mutation=mutation}).RunAsync(f.Route);
            Need(result.State!="Completed"&&f.Market.Continues==0&&f.Cooldowns.Count==0,"Unproven page-range failure retried");
        });
        var baseline=new ShopCommandResponse{WorkSettled=true,CurrentSourceSessionKey=Source.SessionKey,
            Scan=new(){SourceSessionKey=Source.SessionKey,FailureCode="NativePreflightPageChanged",FailureScope="Global",FailurePhase="Preflight",PreflightNotEnteredConfirmed=true,PreflightNoEntrySource=Source},
            SavedScan=new(){SourceSessionKey=Source.SessionKey,UncertainPages=0}};
        await Check("full_settled_source_gate_positive",()=>{Need(PatrolController.IsSettledPageRangeFailure(baseline,Source),"Closed gate refused");return Task.CompletedTask;});
        var invalid=new Dictionary<string,ShopCommandResponse>{
            ["work_unsettled"]=baseline with{WorkSettled=false},["worker_running"]=baseline with{Scan=baseline.Scan with{Running=true}},
            ["session_blocked"]=baseline with{SessionBlocked=true},["entered"]=baseline with{NativeEntered=true},["returned"]=baseline with{NativeReturned=true},
            ["response_verified"]=baseline with{ResponseVerified=true},["missing_proof"]=baseline with{Scan=baseline.Scan with{PreflightNotEnteredConfirmed=false}},
            ["missing_source"]=baseline with{Scan=baseline.Scan with{PreflightNoEntrySource=null}},
            ["changed_source_creation"]=baseline with{Scan=baseline.Scan with{PreflightNoEntrySource=Source with{CreationFileTime=Source.CreationFileTime+1}}},
            ["changed_source_path"]=baseline with{Scan=baseline.Scan with{PreflightNoEntrySource=Source with{ImagePath="other.exe"}}},
            ["changed_current_source"]=baseline with{CurrentSourceSessionKey="other"},["changed_scan_source"]=baseline with{Scan=baseline.Scan with{SourceSessionKey="other"}},
            ["unknown_saved_page"]=baseline with{SavedScan=baseline.SavedScan! with{UncertainPages=1}},["saved_source_changed"]=baseline with{SavedScan=baseline.SavedScan! with{SourceSessionKey="other"}}};
        foreach(var item in invalid)await Check("gate_reject_"+item.Key,()=>{Need(!PatrolController.IsSettledPageRangeFailure(item.Value,Source),"Incomplete/changed no-entry proof authorized retry");return Task.CompletedTask;});
        return checks.ToArray();
    }
    private sealed class PageRangeScanMarket(FakeMarket inner):IPatrolMarket
    {
        internal string? Mutation;
        public async Task<ShopCommandResponse> SendAsync(ShopCommandRequest request,CancellationToken token)
        {
            var value=await inner.SendAsync(request,token);
            if(value.Scan.FailureCode!="NativeCodeUnavailable")return value;
            var scan=value.Scan with{FailureCode="NativePreflightPageChanged",PreflightNotEnteredConfirmed=Mutation!="proof",PreflightNoEntrySource=Mutation=="source"?Source with{CreationFileTime=Source.CreationFileTime+1}:Source};
            var saved=value.SavedScan;
            if(Mutation=="uncertain"&&saved is not null)saved=saved with{UncertainPages=1};
            return value with{Scan=scan,SavedScan=saved,FailureCode="NativePreflightPageChanged"};
        }
    }
}
