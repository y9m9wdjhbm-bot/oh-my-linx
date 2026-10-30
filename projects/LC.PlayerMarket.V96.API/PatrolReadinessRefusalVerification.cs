using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Two real controller points with fake ports; no helper, LC read or input.</summary>
internal static class PatrolReadinessRefusalVerification
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(1234, 5678, "offline-LC.exe", "0x140000000", 1, 65536, new string('A',64), "same-full-source");
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body().WaitAsync(TimeSpan.FromSeconds(10)); checks.Add(new("readiness_refusal_"+name,true,"Actual two-point controller/checkpoint; fake ports only; LC reads/native/input=0.")); }
            catch(Exception error) { checks.Add(new("readiness_refusal_"+name,false,error.ToString())); }
        }
        foreach(int retry in new[]{0,10})
        foreach(bool uncertain in new[]{false,true})
            await Check("next_point_definitive_rejection_retry"+retry+"_uncertain"+(uncertain?22:0),async()=>
            {
                var f=new Fixture("reject",retry,uncertain); var result=await f.Run(); var saved=f.Store.LoadLatest()!;
                Need(result.State=="Failed" && result.CleanupConfirmed && result.CompletedPoints==1 && result.RetriesUsed==0,result.Detail);
                Need(f.Scans.Count==2 && f.Continues==0 && f.Cancels.Count==0 && f.Cooldowns==0,"Rejection retried/cancelled an unaccepted ID");
                Need(saved.PointIndex==1 && saved.Phase==PatrolCheckpointPhase.Search && saved.ScanRequest is null && saved.PlanId is null,"New point inherited old plan or unknown pending");
                Need(f.Current.Scan.JobId==f.Scans[0].RequestId && f.Current.SavedScan!.PlanId=="plan-one","Previous accepted scan was overwritten");
                Need(f.PendingBeforeSecond,"New request was not checkpointed before sending");
                Need(f.Current.SavedScan is { TotalPages:88, ConfirmedPages:66, RemainingPages:0 }
                    && f.Current.SavedScan.SearchedPages==(uncertain?66:88)
                    && f.Current.SavedScan.UncertainPages==(uncertain?22:0),"Prior data outcome was rewritten or treated as native lifetime");
            });
        foreach(bool restart in new[]{false,true})
        foreach(bool uncertain in new[]{false,true})
            await Check((restart?"new_controller_manual_start_uses_new_id":"same_controller_manual_start_uses_new_id")+"_uncertain"+(uncertain?22:0),async()=>
            {
                var f=new Fixture("reject",0,uncertain); var first=await f.Run(); Need(first.CleanupConfirmed,first.Detail);
                string rejected=f.Scans[1].RequestId; f.AllowNext=true;
                var second=await (restart?f.NewController():f.Controller).RunAsync(f.Route);
                Need(second.State=="Completed" && second.CleanupConfirmed && second.CompletedPoints==2,second.Detail);
                Need(f.Scans.Count==3 && f.Scans[2].RequestId!=rejected && f.Scans.Select(s=>s.RequestId).Distinct().Count()==3
                    && f.Scans[2].Command==ShopCommands.ScanVisible && f.Scans[2].ExpectedScanPlanId is null
                    && f.Continues==0 && f.Cancels.Count==0,"Manual start replayed rejected request or old point plan");
                Need(f.Current.SavedScan!.PlanId=="plan-two","New point did not obtain a new plan");
            });
        foreach(string mutation in new[]{"schema","request","source","full_source","scope","phase","entered","returned","verified",
            "readiness_missing","readiness_ready","readiness_readwrite","readiness_native","readiness_probe","readiness_code","readiness_rva",
            "new_target","target_source","target_self","old_job","old_target","old_plan","counts","unsettled","running","session_blocked","uncertain"})
            await Check("mismatch_"+mutation+"_preserves_unknown",async()=>
            {
                var f=new Fixture(mutation,0); var result=await f.Run(); var saved=f.Store.LoadLatest()!;
                Need(result.State=="Blocked" && !result.CleanupConfirmed && result.CompletedPoints==1,result.Detail);
                Need(f.Scans.Count==2 && f.Continues==0 && saved.ScanRequest?.RequestId==f.Scans[1].RequestId && !saved.ScanSettled
                    && f.Cancels.Count==1 && f.Cancels[0].ExpectedScanJobId==f.Scans[1].RequestId,"Uncertain current request lost its durable identity");
                Need(f.Current.Scan.JobId==f.Scans[0].RequestId,"Foreign mutation replaced real old worker");
            });
        await Check("legacy_readiness_code_exact_refusal_still_supported",async()=>
        {
            var f=new Fixture("legacy_code",0); var result=await f.Run();
            Need(result.State=="Failed" && result.CleanupConfirmed && f.Cancels.Count==0 && f.Scans.Count==2,result.Detail);
        });
        foreach(string mutation in new[]{"uncertain_changed_balanced","unsettled","running"})
            await Check("nonzero_uncertain_"+mutation+"_preserves_unknown",async()=>
            {
                var f=new Fixture(mutation,0,true); var result=await f.Run(); var saved=f.Store.LoadLatest()!;
                Need(result.State=="Blocked" && !result.CleanupConfirmed && result.CompletedPoints==1,result.Detail);
                Need(f.Scans.Count==2 && f.Continues==0 && saved.ScanRequest?.RequestId==f.Scans[1].RequestId
                    && !saved.ScanSettled && f.Cancels.Single().ExpectedScanJobId==f.Scans[1].RequestId,"Uncertain request lost its durable identity");
                Need(f.Current.SavedScan is { TotalPages:88, SearchedPages:66, ConfirmedPages:66, UncertainPages:22 },"Previous broker state was overwritten");
            });
        await Check("lost_reply_old_state_never_proves_rejection",async()=>
        {
            var f=new Fixture("lost",0); var result=await f.Run();
            Need(!result.CleanupConfirmed && f.Store.LoadLatest()!.ScanRequest?.RequestId==f.Scans[1].RequestId
                && f.Scans.Count==2 && f.Cancels.Single().ExpectedScanJobId==f.Scans[1].RequestId,result.Detail);
            var second=await f.NewController().RunAsync(f.Route);
            Need(!second.CleanupConfirmed && f.Scans.Count==2,"Restart forgot unknown request and sent again");
        });
        await Check("cancel_after_exact_rejection_does_not_cancel_any_scan",async()=>
        {
            var f=new Fixture("cancel",0); var result=await f.Run();
            Need(result.CleanupConfirmed && f.Cancels.Count==0 && f.Scans.Count==2 && f.Store.LoadLatest()!.ScanRequest is null,result.Detail);
        });
        return checks.ToArray();
    }
    private static void Need(bool value,string detail){if(!value)throw new InvalidOperationException(detail);}

    private sealed class Fixture : IPatrolMarket
    {
        internal readonly string Mode;
        internal readonly bool NonzeroUncertain;
        internal readonly PatrolCheckpointStore Store=new(Path.Combine(Path.GetTempPath(),"LcReadinessRefusalOffline",Guid.NewGuid().ToString("N")));
        internal readonly PatrolRouteSnapshot Route;
        internal readonly FakeNavigation Nav=new();
        internal readonly CancellationTokenSource Stop=new();
        internal readonly List<ShopCommandRequest> Scans=[],Cancels=[];
        internal readonly PatrolController Controller;
        internal int Continues,Cooldowns,Refreshes;
        internal bool AllowNext,PendingBeforeSecond;
        internal ShopCommandResponse Current=new(){State="State",WorkSettled=true,CurrentSourceSessionKey=Source.SessionKey};
        internal Fixture(string mode,int retry,bool nonzeroUncertain=false)
        {
            Mode=mode; NonzeroUncertain=nonzeroUncertain;
            Route=new(){Revision=Guid.NewGuid().ToString("N"),SavedAt=Now,RoundCount=1,RetryLimit=retry,ArrivalRadius=1,
                Points=Enumerable.Range(0,2).Select(i=>new PatrolWaypoint(Guid.NewGuid().ToString("N"),"point-"+i,"4","offline",10+i*4,20)).ToArray()};
            Controller=NewController();
        }
        internal PatrolController NewController()=>new(Nav,this,()=>Now,(span,token)=>
            {if(span>=TimeSpan.FromSeconds(2))Cooldowns++;return Task.Delay(1,token);},TimeSpan.FromMilliseconds(200),forceDispatch:true,checkpointStore:Store,autoResume:true);
        internal Task<PatrolRunResult> Run()=>Controller.RunAsync(Route,Stop.Token);
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(request.Command==ShopCommands.RefreshTargets)
            {
                Current=Current with{TargetSnapshot=new(){SnapshotId="target-"+(++Refreshes),SourceSessionKey=Source.SessionKey,SelfId=11,RefreshedAt=Now,CanDispatch=true}};
                return Task.FromResult(Current with{RequestId=request.RequestId,State="TargetsRefreshed",WorkSettled=false});
            }
            if(request.Command==ShopCommands.ScanVisible)
            {
                Scans.Add(request);
                if(Scans.Count==1 || AllowNext)
                {
                    bool first=Scans.Count==1;
                    int total=first?88:2, uncertain=first && NonzeroUncertain?22:0;
                    int searched=total-uncertain, confirmed=first?66:2;
                    Current=new(){RequestId=request.RequestId,State="ScanStarted",CurrentSourceSessionKey=Source.SessionKey,WorkSettled=true,
                        TargetSnapshot=Current.TargetSnapshot,
                        Scan=new(){JobId=request.RequestId,SourceSessionKey=Source.SessionKey,TargetSnapshotId=request.TargetSnapshotId,
                            Running=false,Total=total,Completed=total,Succeeded=confirmed,TotalShops=first?44:1,CompletedShops=first?44:1},
                        SavedScan=new(){PlanId=Scans.Count==1?"plan-one":"plan-two",SourceSessionKey=Source.SessionKey,
                            TotalPages=total,SearchedPages=searched,ConfirmedPages=confirmed,UncertainPages=uncertain,ContinueBlockCode="Complete",CanContinue=false}};
                    return Task.FromResult(Current);
                }
                var pending=Store.LoadLatest()!;
                PendingBeforeSecond=pending.ScanRequest?.RequestId==request.RequestId && !pending.ScanSettled && pending.PlanId is null;
                if(Mode=="lost")throw new IOException("Synthetic lost command reply; old state is not a refusal");
                string code=Mode=="legacy_code"?"NativeInitializationRequired":"NativeCodeUnavailable";
                var response=Current with{RequestId=request.RequestId,State="Rejected",FailureCode=code,FailureScope="Global",FailurePhase="BeforePublish",FailureRva="0x76BEB0",
                    Detail="Current exact readonly preflight refused before creating this scan.",
                    NativeReadiness=new(){BuildCompatible=true,Ready=false,Code=code,FailureRva="0x76BEB0",Source=Source,
                        ProbeId=Guid.NewGuid().ToString("N"),ReadOnly=true,NativeRequests=0}};
                if(Mode=="cancel")Stop.Cancel();
                return Task.FromResult(Mutate(response,Mode));
            }
            if(request.Command==ShopCommands.ContinueSavedScan){Continues++;throw new InvalidOperationException("New point must not continue old plan");}
            if(request.Command==ShopCommands.CancelScan)
            {
                Cancels.Add(request);
                return Task.FromResult(Current with{RequestId=request.RequestId,State="Rejected",Detail="Original worker has a different ID; no cancellation"});
            }
            Need(request.Command==ShopCommands.GetState,"Unexpected command");
            return Task.FromResult(Current with{RequestId=request.RequestId,State="State"});
        }
        private static ShopCommandResponse Mutate(ShopCommandResponse r,string kind)=>kind switch
        {
            "schema"=>r with{Schema=999},"request"=>r with{RequestId="foreign"},"source"=>r with{CurrentSourceSessionKey="foreign"},
            "full_source"=>r with{NativeReadiness=r.NativeReadiness! with{Source=Source with{CreationFileTime=Source.CreationFileTime+1}}},
            "scope"=>r with{FailureScope="Page"},"phase"=>r with{FailurePhase="Preflight"},"entered"=>r with{NativeEntered=true},
            "returned"=>r with{NativeReturned=true},"verified"=>r with{ResponseVerified=true},"readiness_missing"=>r with{NativeReadiness=null},
            "readiness_ready"=>r with{NativeReadiness=r.NativeReadiness! with{Ready=true}},
            "readiness_readwrite"=>r with{NativeReadiness=r.NativeReadiness! with{ReadOnly=false}},
            "readiness_native"=>r with{NativeReadiness=r.NativeReadiness! with{NativeRequests=1}},
            "readiness_probe"=>r with{NativeReadiness=r.NativeReadiness! with{ProbeId=""}},
            "readiness_code"=>r with{NativeReadiness=r.NativeReadiness! with{Code="different"}},
            "readiness_rva"=>r with{NativeReadiness=r.NativeReadiness! with{FailureRva="different"}},
            "new_target"=>r with{TargetSnapshot=r.TargetSnapshot! with{SnapshotId="different"}},
            "target_source"=>r with{TargetSnapshot=r.TargetSnapshot! with{SourceSessionKey="different"}},
            "target_self"=>r with{TargetSnapshot=r.TargetSnapshot! with{SelfId=12}},
            "old_job"=>r with{Scan=r.Scan with{JobId="different"}},"old_target"=>r with{Scan=r.Scan with{TargetSnapshotId="different"}},
            "old_plan"=>r with{SavedScan=r.SavedScan! with{PlanId="different"}},
            "counts"=>r with{SavedScan=r.SavedScan! with{ConfirmedPages=1}},"unsettled"=>r with{WorkSettled=false},
            "running"=>r with{Scan=r.Scan with{Running=true}},"session_blocked"=>r with{SessionBlocked=true},
            "uncertain"=>r with{SavedScan=r.SavedScan! with{SearchedPages=1,ConfirmedPages=1,UncertainPages=1}},
            "uncertain_changed_balanced"=>r with{SavedScan=r.SavedScan! with{SearchedPages=65,ConfirmedPages=65,UncertainPages=23}},_=>r
        };
    }
    private sealed class FakeNavigation : IPatrolNavigation
    {
        private int x=10,y=20;
        private PatrolNavigationState current=new();
        public Task<PatrolObservation> CaptureAsync(CancellationToken token,PatrolObservation? expected=null)
        {token.ThrowIfCancellationRequested();return Task.FromResult(new PatrolObservation(Source,11,4,x,y,Now,false));}
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();x=request.Waypoint.X;y=request.Waypoint.Y;
            current=new(){JobId=request.JobId,Source=Source,SelfId=11,MapId=4,WaypointId=request.Waypoint.PointId,
                TargetX=x,TargetY=y,X=x,Y=y,ArrivalRadius=1,SampledAt=Now,State="Completed",Completed=true,Terminal=true,
                WorkSettled=true,CleanupConfirmed=true,PositionVerified=true};return Task.FromResult(current);
        }
        public Task<PatrolNavigationState> StopAsync(string id,CancellationToken token)
        {token.ThrowIfCancellationRequested();Need(current.JobId==id,"Wrong navigation stop");return Task.FromResult(current);}
        public Task<PatrolNavigationState> GetStateAsync(string id,CancellationToken token)
        {token.ThrowIfCancellationRequested();Need(current.JobId==id,"Wrong navigation state");return Task.FromResult(current);}
    }
}
