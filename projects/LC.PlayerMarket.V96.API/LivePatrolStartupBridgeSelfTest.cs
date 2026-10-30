using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Actual adapter and UI policy with fake clock/commands/start callback only.</summary>
internal static class LivePatrolStartupBridgeSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new(name, true, "Diagnostic startup adapter; fake commands/clock/controller only; no LC/helper/native/input.")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

        await Check("diagnostic_startup_first_prepare_then_only_check_and_separate_state", async () =>
        {
            var f = new Fixture { ReadyAt = 4 };
            var admitted = await f.Run();
            Need(admitted.Readiness.Ready && admitted.Readiness.Attempts == 4 && admitted.CommitCode == "Committed"
                && ReferenceEquals(admitted.Run, f.Controller.Task) && f.Starts == 1, "Original continuous wait or synchronous controller callback was bypassed.");
            Need(f.Requests.Count == 8 && f.Requests[0].Command == ShopCommands.PrepareNativeReadiness
                && f.Requests.Where(r=>r.Command==ShopCommands.PrepareNativeReadiness).Count()==1
                && f.Requests.Where((_,i)=>i%2==1).All(r=>r.Command==ShopCommands.GetState)
                && f.Requests.Where((_,i)=>i%2==0).Skip(1).All(r=>r.Command==ShopCommands.CheckNativeReadiness), "Readonly Prepare/Check/GetState sequence changed.");
            Need(f.Requests.Where(r=>r.Command!=ShopCommands.GetState).All(r=>r.WaitForNativeReadinessCompletion)
                && f.Progress.Count==3 && f.Progress[^1].NextCheckMilliseconds==2000
                && f.Elapsed==6000 && f.MaximumActiveProbes==1, "Original helper wait/cooldown/ownership was lost.");
        });
        await Check("diagnostic_startup_permit_is_consumed_only_by_real_start_callback", async () =>
        {
            var f = new Fixture(); var admitted = await f.Run();
            Need(f.Starts==1 && ReferenceEquals(admitted.Run,f.Controller.Task) && !f.Controller.Task.IsCompleted,
                "Adapter awaited a fake completion or deferred start outside the commit callback.");
            Need(!admitted.Readiness.Permit!.TryCommit(admitted.Readiness.FinalObservation!, f.Now, default,
                ()=>f.Starts++,out string code) && code=="AlreadyConsumed" && f.Starts==1,
                "Single-use startup permit could start twice.");
        });
        await Check("diagnostic_startup_slow_original_probe_remains_one_owned_probe", async () =>
        {
            var f=new Fixture { BlockFirst=true };
            f.OnDelay=()=>{if(f.Elapsed>=1000)f.ReleaseFirst();};
            var admitted=await f.Run();
            Need(admitted.Readiness.Ready && f.Probes==1 && f.Requests.Count==2 && f.MaximumActiveProbes==1
                && f.Elapsed < PatrolStartupReadinessPolicy.TotalBudgetMilliseconds && f.Starts==1,
                "Thirty seconds caused another Prepare/Check before the original probe ended.");
        });
        await Check("diagnostic_startup_f9_cancels_wait_without_later_probe_or_start", async () =>
        {
            var f=new Fixture { ReadyAt=20 };using var stop=new CancellationTokenSource();
            f.OnDelay=()=>stop.Cancel();var admitted=await f.Run(stop.Token);
            Need(admitted.Run is null && admitted.Readiness.Code=="Cancelled" && f.Probes==1 && f.Starts==0,
                "Cancelled startup issued another readonly probe or started controller.");
        });
        await Check("diagnostic_startup_brief_unknown_source_latch_cannot_revive", async () =>
        {
            var f=new Fixture { ReadyAt=20 };
            f.OnDelay=()=>{f.Latch.Observe(null);f.Current=f.Expected;};
            var admitted=await f.Run();
            Need(admitted.Run is null && admitted.Readiness.Code=="ContextChangedOrUnknown" && f.Probes==1 && f.Starts==0,
                "Restored source revived a start intent invalidated by an unknown observation.");
        });
        await Check("diagnostic_startup_different_source_cannot_retry_or_start", async () =>
        {
            var f=new Fixture { ReadyAt=20 };
            f.OnDelay=()=>f.Current=f.Expected with {Source=f.Expected.Source with {CreationFileTime=123457}};
            var admitted=await f.Run();
            Need(admitted.Run is null && f.Probes==1 && f.Starts==0,"Different LC creation reused an old startup intent.");
        });
        foreach(string mutation in new[]{"unsettled","state_id","state_source","native_request","deferred_mask","mismatch_code"})
            await Check("diagnostic_startup_"+mutation+"_prevents_commit",async()=>
            {
                var f=new Fixture { Mutation=mutation };var admitted=await f.Run();
                Need(admitted.Run is null && !admitted.Readiness.Ready && f.Probes==1 && f.Starts==0,
                    "Unclosed, changed, deferred, or non-readonly observation acquired admission.");
            });
        await Check("diagnostic_startup_request_id_mismatch_is_not_a_readiness_receipt",async()=>
        {
            var f=new Fixture {Mutation="probe_id"};var admitted=await f.Run();
            Need(admitted.Run is null && admitted.Readiness.Code=="ReadinessObservationFailed" && f.Starts==0,
                "Foreign response ID was accepted.");
        });
        await Check("diagnostic_startup_all_commands_remain_readonly_until_start",async()=>
        {
            var f=new Fixture {ReadyAt=3};var admitted=await f.Run();
            Need(admitted.Run is not null && f.Requests.All(r=>r.Command is ShopCommands.PrepareNativeReadiness
                or ShopCommands.CheckNativeReadiness or ShopCommands.GetState)
                && f.Requests.All(r=>r.ExpectedSourceSessionKey==f.Expected.Source.SessionKey),
                "Adapter submitted a query/navigation/owner command or changed source.");
        });
        checks.AddRange(LiveStartupIdentityContextSelfTest.Run());
        return checks;
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Epoch=new(2026,10,29,8,0,0,TimeSpan.Zero);
        internal readonly PatrolStartupReadinessContext Expected=new(new SourceIdentity(900001,123456,
            @"C:\offline\LC.exe","0x140000000",1793101316,66609152,
            "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC","offline-diagnostic-startup"),12345);
        internal PatrolStartupReadinessContext? Current;
        internal readonly PatrolStartupContextLatch Latch;
        internal readonly List<ShopCommandRequest> Requests=[];
        internal readonly List<PatrolStartupReadinessProgress> Progress=[];
        internal readonly TaskCompletionSource<PatrolRunResult> Controller=new();
        private readonly TaskCompletionSource<ShopCommandResponse> blocked=new();
        private ShopCommandRequest? firstRequest;
        internal int ReadyAt=1,Probes,Starts,ActiveProbes,MaximumActiveProbes;
        internal long Elapsed;
        internal string Mutation="";
        internal bool BlockFirst;
        internal Action? OnDelay;
        internal DateTimeOffset Now=>Epoch.AddMilliseconds(Elapsed);
        internal Fixture(){Current=Expected;Latch=new(Expected);}
        internal PatrolStartupReadinessContext? Context()=>Latch.Observe(Current)?Current:null;
        internal Task<LivePatrolStartupAdmission> Run(CancellationToken token=default)=>LivePatrolStartupBridge.WaitAndStartAsync(
            Expected,Send,Context,()=>ActiveProbes==0&&Starts==0,()=>Now,()=>Elapsed,Delay,token,
            ()=>{Starts++;return Controller.Task;},progress:Progress.Add);
        internal void ReleaseFirst(){if(firstRequest is not null)blocked.TrySetResult(Probe(firstRequest,true));}
        private Task Delay(TimeSpan wait,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Elapsed+=checked((long)wait.TotalMilliseconds);
            if(Elapsed>120000)throw new InvalidOperationException("Offline fixture wait exceeded bound.");
            OnDelay?.Invoke();token.ThrowIfCancellationRequested();return Task.CompletedTask;
        }
        private async Task<ShopCommandResponse> Send(ShopCommandRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Requests.Add(request);
            if(request.Command==ShopCommands.GetState)
                return new(){RequestId=Mutation=="state_id"?"foreign-state":request.RequestId,State="State",At=Now,
                    CurrentSourceSessionKey=Mutation=="state_source"?"foreign-source":Expected.Source.SessionKey,
                    WorkSettled=Mutation!="unsettled"};
            if(request.Command is not(ShopCommands.PrepareNativeReadiness or ShopCommands.CheckNativeReadiness))
                throw new InvalidOperationException("Non-readonly command reached startup fixture.");
            Probes++;ActiveProbes++;MaximumActiveProbes=Math.Max(MaximumActiveProbes,ActiveProbes);
            try
            {
                if(BlockFirst&&Probes==1){firstRequest=request;return await blocked.Task.WaitAsync(token);}
                return Probe(request,Probes>=ReadyAt);
            }
            finally{ActiveProbes--;}
        }
        private ShopCommandResponse Probe(ShopCommandRequest request,bool ready)
        {
            var proof=new ShopNativeReadiness{BuildCompatible=true,Ready=ready,
                Code=Mutation=="mismatch_code"?"NativeCodeMismatch":ready?"NativeReady":"NativeCodeUnavailable",
                FailureRva=ready?null:"0x73A0C0",Source=Expected.Source,ReadOnly=true,
                NativeRequests=Mutation=="native_request"?1:0,PendingMask=Mutation=="deferred_mask"?1u:0u,
                ProbeId="offline-probe-"+Probes,SampledAt=Now,ExpiresAt=Now.AddMilliseconds(HubProtocol.FreshMilliseconds),
                CurrentRuntimeCodeVerified=ready,EvidenceMode="CurrentRuntimeCode",
                ColdCodePolicyId=ShopNativeCurrentB47CPolicy.Id,ColdCodeCertificateSha256=ShopNativeCurrentB47CPolicy.CertificateSha256};
            return new(){RequestId=Mutation=="probe_id"?"foreign-readiness":request.RequestId,
                State=Mutation=="mismatch_code"?"Rejected":ready?"NativeReady":"Rejected",At=Now,
                CurrentSourceSessionKey=Expected.Source.SessionKey,NativeReadiness=proof,
                FailureCode=Mutation=="mismatch_code"?"NativeCodeMismatch":ready?null:"NativeCodeUnavailable",
                FailureScope=ready&&Mutation!="mismatch_code"?null:"Global",
                FailurePhase=ready&&Mutation!="mismatch_code"?null:"BeforePublish",
                FailureRva=ready&&Mutation!="mismatch_code"?null:"0x73A0C0"};
        }
    }
}
