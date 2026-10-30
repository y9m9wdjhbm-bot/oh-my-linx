using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LcNativeWorker.V18;
using LcPlayerMarket;

namespace MemoryHub.Service;

internal static class ResidentNativeHostSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new("resident_host_" + name, true, "Fake pipe frames/reservations/native receipts only; no LC/process launch/native/game input.")); }
            catch (Exception error) { checks.Add(new("resident_host_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        static async Task Refuses(Func<Task> body)
        { bool refused = false; try { await body(); } catch { refused = true; } Need(refused, "Invalid fixture was accepted."); }
        WorkerFrame Hello() => new() { Kind = "hello", SessionNonce = new('A',64), WorkerId = new('C',32),
            WorkerProcessId = 900001, WorkerCreationFileTime = 123456, IdleAck = true, NativeLifecycleSettled = true };
        await Check("hello_requires_exact_os_peer_and_session", async () =>
        {
            ShopResidentNativeClient.RequireHello(Hello(), new('A',64), 900001, 123456);
            foreach (var bad in new[] { Hello() with { WorkerProcessId = 900002 }, Hello() with { WorkerCreationFileTime = 123457 },
                Hello() with { SessionNonce = new('B',64) }, Hello() with { Kind = "idle" }, Hello() with { WorkerProcessExited = true },
                Hello() with { NativeLifecycleSettled = false }, Hello() with { Sequence = 1 } })
                await Refuses(() => { ShopResidentNativeClient.RequireHello(bad, new('A',64), 900001, 123456); return Task.CompletedTask; });
        });
        await Check("fake_pipe_valid_frame_uses_strict_shared_reader", async () =>
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Hello(),WorkerProtocol.Json)+"\n"));
            using var reader = new StreamReader(stream,new UTF8Encoding(false,true));
            Need(await ShopResidentNativeClient.ReadFrameAsync(reader,default)==Hello(),"Frame roundtrip differs.");
        });
        foreach (string invalid in new[] { "duplicate", "unknown", "partial", "oversized" })
        await Check("fake_pipe_"+invalid+"_is_rejected", async () =>
        {
            string raw = JsonSerializer.Serialize(Hello(),WorkerProtocol.Json);
            raw = invalid switch
            {
                "duplicate" => "{\"schema\":\"duplicate\","+raw[1..]+"\n",
                "unknown" => "{\"unrecognized\":true,"+raw[1..]+"\n",
                "partial" => raw,
                _ => new string('A',WorkerProtocol.MaximumFrameCharacters+1)+"\n"
            };
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw)); using var reader = new StreamReader(stream);
            await Refuses(async()=>{await ShopResidentNativeClient.ReadFrameAsync(reader,default);});
        });
        await Check("resident_and_legacy_work_share_one_reservation", async () =>
        {
            var flight = new ShopNativeHelperFlight(); var pending = new TaskCompletionSource<bool>(); int residentStarts=0, legacyStarts=0;
            var run = flight.RunResidentOperationAsync(()=>{residentStarts++;return pending.Task;},r=>r,()=>Task.CompletedTask,TimeSpan.FromSeconds(5));
            Need(flight.IsActive&&residentStarts==1,"Request was not reserved.");
            await Refuses(()=>flight.RunAsync(()=>{legacyStarts++;return new FakeExitedHelper();},TimeSpan.FromSeconds(1)));
            Need(legacyStarts==0,"Legacy native started beside resident work.");
            pending.SetResult(true);Need(await run&&!flight.IsActive,"Positive receipt did not release request reservation.");
        });
        await Check("negative_outcome_keeps_global_fence_even_after_worker_exit", async () =>
        {
            var flight=new ShopNativeHelperFlight();
            Need(!await flight.RunResidentOperationAsync(()=>Task.FromResult(false),r=>r,()=>Task.CompletedTask,TimeSpan.FromSeconds(5)),"Negative outcome changed.");
            await flight.WaitUntilExitAsync();
            Need(flight.IsActive,"Worker exit erased an unknown native request.");
            await Refuses(()=>{flight.RequireAvailable();return Task.CompletedTask;});
        });
        await Check("broken_pipe_does_not_restart_or_replay_request", async () =>
        {
            var flight=new ShopNativeHelperFlight();int starts=0;
            await Refuses(()=>flight.RunResidentOperationAsync<bool>(()=>{starts++;throw new EndOfStreamException("fake broken pipe");},r=>r,()=>Task.CompletedTask,TimeSpan.FromSeconds(5)));
            Need(starts==1&&flight.IsActive,"Broken pipe was replayed or reservation disappeared.");
        });
        await Check("outer_timeout_quarantines_late_positive_and_prevents_new_factory", async () =>
        {
            var flight = new ShopNativeHelperFlight();var pending = new TaskCompletionSource<bool>();int poisoned = 0, replacements = 0;
            await Refuses(()=>flight.RunResidentOperationAsync(()=>pending.Task,r=>r,()=>Task.CompletedTask,
                TimeSpan.FromMilliseconds(10),()=>poisoned++));
            Need(poisoned==1&&flight.IsActive,"Outer timeout did not retire the request.");
            pending.SetResult(true);await flight.WaitUntilResidentObservationAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Need(flight.IsActive,"Late positive completion removed the timed-out native fence.");
            await Refuses(()=>flight.RunAsync(()=>{replacements++;return new FakeExitedHelper();},TimeSpan.FromSeconds(1)));
            Need(replacements==0,"Timed-out request allowed a replacement helper.");
        });
        foreach(bool stopStuck in new[]{true,false})
        await Check("shutdown_"+(stopStuck?"stop_ignores_cancel":"actual_exit_unknown")+"_is_bounded",async()=>
        {
            var never=new TaskCompletionSource();int exitRequests=0;
            using var budget=new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            await Refuses(()=>ShopResidentShutdownObservation.StopAndObserveAsync(
                _=>stopStuck?never.Task:Task.CompletedTask,()=>{exitRequests++;return never.Task;},budget.Token));
            Need(budget.IsCancellationRequested&&exitRequests==(stopStuck?0:1),"Shared shutdown budget was ignored.");
        });
        await Check("full_resident18_receipt_accepted_without_process_exit_claim", async () =>
        {
            using var f=new ReceiptFixture(); var parsed=ShopResidentNativeOutcome.ParseResidentRequest(f.Evidence,f.Source,f.Image,f.Owner);
            Need(parsed.Entered&&parsed.Returned&&parsed.SafeToSettle&&parsed.SourceMatched
                && parsed.Detail.Contains("WorkerProcessExitClaimed=false"),parsed.Detail);
            Need(!ShopNativeOutcome.Parse(f.Text,f.Source,f.Image,f.Owner,0).SafeToSettle,
                "Legacy process-exit parser accepted the resident18 producer.");
        });
        await Check("known_noentry_candidate_requires_full_original_native_guard_proof",async()=>
        {
            using var f=new ReceiptFixture();var evidence=f.MakeGuardNoEntry();
            var parsed=ShopResidentNativeOutcome.ParseResidentRequest(evidence,f.Source,f.Image,f.Owner);
            Need(parsed.SafeToSettle&&parsed.NoEntryConfirmed&&parsed.SourceMatched&&!parsed.Entered&&!parsed.Returned
                && parsed.Failure?.Code=="NativeGuardRejected",parsed.Detail);
            await Task.CompletedTask;
        });
        await Check("noentry_flag_and_resource_closed_defaults_are_not_native_proof",async()=>
        {
            using var f=new ReceiptFixture();var evidence=f.MakeFlagOnlyNoEntry();
            Need(!ShopResidentNativeOutcome.ParseResidentRequest(evidence,f.Source,f.Image,f.Owner).SafeToSettle,
                "Code1/candidate flag was substituted for original native proof.");
            await Task.CompletedTask;
        });
        foreach(string mutation in new[]{"server_pid","server_creation","request_nonce","late_sequence","source","end_digest","idle_missing","lease","producer17"})
        await Check("receipt_"+mutation+"_cannot_settle",async()=>
        {
            using var f=new ReceiptFixture();var evidence=f.Evidence;
            evidence=mutation switch
            {
                "server_pid"=>evidence with{VerifiedServerProcessId=900002},
                "server_creation"=>evidence with{VerifiedServerCreationFileTime=123457},
                "request_nonce"=>evidence with{Idle=evidence.Idle with{RequestNonce=new('D',64)}},
                "late_sequence"=>evidence with{Idle=evidence.Idle with{Sequence=2}},
                "source"=>evidence with{Idle=evidence.Idle with{Source=evidence.Request.Source! with{CreationFileTime=evidence.Request.Source.CreationFileTime+1}}},
                "end_digest"=>evidence with{Result=evidence.Result with{EndLogSha256=new('E',64)},Idle=evidence.Idle with{EndLogSha256=new('E',64)}},
                "idle_missing"=>evidence with{Idle=evidence.Idle with{IdleAck=false}},
                _=>evidence
            };
            if(mutation=="lease")evidence=f.Mutate(row=>{if(row["Kind"]!.GetValue<string>()=="END")row["Data"]!["NativeResult"]!["LeaseReleaseCompleted"]=0;});
            if(mutation=="producer17")evidence=f.Mutate(row=>{if(row["Kind"]!.GetValue<string>() is "START" or "END")row["Data"]!["ManagedProgramVersion"]="17.0.1";});
            Need(!ShopResidentNativeOutcome.ParseResidentRequest(evidence,f.Source,f.Image,f.Owner).SafeToSettle,
                "Invalid/stale resident evidence settled the request.");
            await Task.CompletedTask;
        });
        return checks;
    }

    private sealed class FakeExitedHelper : IShopNativeHelperProcess
    { public bool HasExited=>true;public int ExitCode=>0;public Task WaitForExitAsync()=>Task.CompletedTask;public void Dispose(){} }

    private sealed class ReceiptFixture : IDisposable
    {
        internal readonly SourceIdentity Source;
        internal readonly ulong Image,Owner;
        internal readonly string PathName;
        internal readonly WorkerRequest Request;
        internal ShopResidentRequestEvidence Evidence;
        internal string Text=string.Empty;
        private readonly List<JsonObject> rows;
        private readonly string root;
        private bool noEntry;
        internal ReceiptFixture()
        {
            var assembly=typeof(ResidentNativeHostSelfTest).Assembly;
            string name=assembly.GetManifestResourceNames().Single(n=>n.EndsWith(".returned-v17-template.jsonl",StringComparison.Ordinal));
            using var stream=assembly.GetManifestResourceStream(name)!;using var copy=new MemoryStream();stream.CopyTo(copy);byte[] bytes=copy.ToArray();
            if(Convert.ToHexString(SHA256.HashData(bytes))!="4B969815ED3E37FC4FF004CD9C8A75A850E2F0DCDE34D3F77074C609F123C785")throw new InvalidDataException("RESIDENT_FIXTURE_PIN_CHANGED");
            rows=Encoding.UTF8.GetString(bytes).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>JsonNode.Parse(l)!.AsObject()).ToList();
            var identity=rows.Single(r=>r["Kind"]!.GetValue<string>()=="SOURCE")["Data"]!;
            static ulong Number(JsonNode n)=>n is JsonValue v&&v.TryGetValue<string>(out var t)?ulong.Parse(t.StartsWith("0x")?t[2..]:t,NumberStyles.HexNumber,CultureInfo.InvariantCulture):n.GetValue<ulong>();
            Image=Number(identity["Image"]!);Owner=Number(identity["TargetOwner"]!);
            Source=new(identity["Pid"]!.GetValue<int>(),identity["Creation"]!.GetValue<long>(),identity["Path"]!.GetValue<string>(),"0x"+Image.ToString("X"),
                checked((uint)Number(identity["Timestamp"]!)),checked((uint)Number(identity["ImageSize"]!)),identity["Sha256"]!.GetValue<string>(),"offline-resident-evidence");
            root=System.IO.Path.Combine(@"D:\lineage\LC_PlayerMarket_LOG\maintenance\20261030-v96-resident-host-fixtures",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);PathName=System.IO.Path.Combine(root,"native.jsonl");
            string mode=rows[0]["Data"]!["Mode"]!.GetValue<string>();
            Request=new(){SessionNonce=new('A',64),WorkerId=new('C',32),WorkerProcessId=900001,WorkerCreationFileTime=123456,
                Sequence=1,RequestId="offline-request",RequestNonce=new('B',64),Source=ShopResidentRequestEvidence.ToWire(Source),
                Hwnd=Number(identity["Hwnd"]!),PlayerShopSubsystem=Number(identity["Shop"]!),CurrentOwnerIdRaw=Number(identity["Owner"]!),
                TargetOwnerId=checked((uint)Owner),Direction=mode=="buy"?ShopPageModes.ViewerBuy:ShopPageModes.ViewerSell,NativeLogPath=PathName,ForceDispatch=true};
            foreach(var row in rows.Where(r=>r["Kind"]!.GetValue<string>() is "START" or "END"))
            {row["Data"]!["ManagedProgramVersion"]="18.0.0";row["Data"]!["ResidentBinding"]=JsonSerializer.SerializeToNode(Request);}
            Evidence=Write();
        }
        private ShopResidentRequestEvidence Write()
        {
            Text=string.Join("\n",rows.Select(r=>r.ToJsonString()))+"\n";byte[] bytes=new UTF8Encoding(false).GetBytes(Text);
            File.WriteAllBytes(PathName,bytes);string sha=Convert.ToHexString(SHA256.HashData(bytes));
            var result=new WorkerFrame{Kind="result",SessionNonce=Request.SessionNonce,WorkerId=Request.WorkerId,WorkerProcessId=Request.WorkerProcessId,
                WorkerCreationFileTime=Request.WorkerCreationFileTime,Sequence=1,RequestId=Request.RequestId,RequestNonce=Request.RequestNonce,Source=Request.Source,
                RequestExitCode=noEntry?1:0,NoEntryCandidate=noEntry,NativeLogPath=PathName,EndLogSha256=sha,RequestEndDurable=true,NativeLifecycleSettled=true,
                SourceFileIdentity="offline-volume-file-identity",SourceHashComputations=1};
            return new(Request,result,result with{Kind="idle",IdleAck=true},900001,123456,"offline-volume-file-identity");
        }
        internal ShopResidentRequestEvidence Mutate(Action<JsonObject> mutation)
        {foreach(var row in rows)mutation(row);return Write();}
        internal ShopResidentRequestEvidence MakeFlagOnlyNoEntry()
        {noEntry=true;rows.Single(r=>r["Kind"]!.GetValue<string>()=="END")["Data"]!["ExitCode"]=1;return Write();}
        internal ShopResidentRequestEvidence MakeGuardNoEntry()
        {
            noEntry=true;
            rows.RemoveAll(r=>r["Kind"]!.GetValue<string>() is "PACKET_CODE_CONFIRMED" or "BORROWED_SOURCE_VERIFIED");
            var end=rows.Single(r=>r["Kind"]!.GetValue<string>()=="END")["Data"]!.AsObject();
            var native=end["NativeResult"]!.DeepClone().AsObject();
            foreach(string key in new[]{"Entered","Returned","Active","Stage","Exception","ExceptionStage","ExceptionAddress",
                "ExceptionAccessAddress","ExceptionAccessKind","ExceptionCount","ReleaseExceptionCode","ReleaseExceptionAddress",
                "LeaseAcquired","LeaseReleaseAttempted","LeaseReleaseCompleted","RequestMetadataBefore","RequestMetadataAfter",
                "RequestUnchanged","DestructorCalls","PostCodeConfirmed"})native[key]=0;
            native["Status"]=-3;
            var eventRow=rows.Single(r=>r["Kind"]!.GetValue<string>()=="EVENT_RESULT");var result=eventRow["Data"]!.AsObject();
            result["State"]=native.DeepClone();result["NativeReturned"]=false;result["PostCodeConfirmed"]=false;
            result["PostProof"]=new JsonArray();result["PostCodeErrorCode"]="NativeResultUnconfirmed";
            result["PostCodeRecheck"]=new JsonObject{["Contract"]=1,["Attempts"]=0,["TemporaryUnavailableObservations"]=0,
                ["ElapsedMilliseconds"]=0,["Outcome"]="NotStarted",["ReadOnly"]=true,["NativeRequestsRetried"]=0};
            rows.Single(r=>r["Kind"]!.GetValue<string>()=="NATIVE_FINAL_STATE")["Data"]!["State"]=native.DeepClone();
            end["NativeResult"]=native.DeepClone();end["ExitCode"]=1;end["PostCodeConfirmed"]=false;
            end["NativeAlreadyReturned"]=false;end["BorrowedSourceContractVerified"]=false;
            rows.Insert(rows.IndexOf(eventRow)+1,new JsonObject{["At"]=eventRow["At"]!.DeepClone(),["Kind"]="ERROR",["DiskFlush"]=true,
                ["Data"]=new JsonObject{["Type"]="InvalidOperationException",["Code"]="Unknown",["Phase"]="Native",["Rva"]=null,
                    ["Message"]="Native event failed or did not return normally."}});
            for(int i=0;i<rows.Count;i++)rows[i]["Sequence"]=i+1;
            int durable=rows.Take(rows.Count-1).Count(r=>r["DiskFlush"]!.GetValue<bool>());
            end["LogRecordsBeforeEnd"]=rows.Count-1;end["LogDiskFlushesBeforeEnd"]=durable;end["LogBufferedRecordsBeforeEnd"]=rows.Count-1-durable;
            return Write();
        }
        public void Dispose() { /* Preserve generated fixture bytes with the test evidence. */ }
    }
}
