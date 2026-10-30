using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

// Synthetic receipts, actual production policies and isolated durable stores.
// No helper process, LC read, native dispatch, game input or historical repair.
internal static class BrokerPendingVerification
{
    internal static int RunReport(string output)
    {
        var checks = RunAsync().GetAwaiter().GetResult();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed),
            checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true); return report.Success ? 0 : 1;
    }

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action action)
        { try { action(); checks.Add(new("broker_pending_" + name, true, "SYNTHETIC HISTORICAL38A7 policy/store grammar only; explicit current rejection case; no real recovery/helper/native/LC.")); }
          catch (Exception error) { checks.Add(new("broker_pending_" + name, false, error.ToString())); } }
        async Task CheckAsync(string name, Func<Task> action)
        { try { await action(); checks.Add(new("broker_pending_" + name, true, "SYNTHETIC cancellable recovery callback only; zero helper/native/LC.")); }
          catch (Exception error) { checks.Add(new("broker_pending_" + name, false, error.ToString())); } }
        foreach (var item in new[]
        {
            new ShopCommandResponse { SessionBlocked = true },
            new ShopCommandResponse { FailureScope = "Session", FailureCode = "NativeOutcomeUnknown" },
            new ShopCommandResponse { FailureScope = "Unknown" }
        }) Check("force_stops_" + (item.FailureScope ?? "blocked_boolean"), () =>
            Need(ShopScanPolicy.Decide(item, true, true) == ShopScanDisposition.StopBatch, "force skipped native uncertainty"));
        Check("settled_unverified_product_still_continues", () => Need(ShopScanPolicy.Decide(new()
            { NativeEntered = true, NativeReturned = true, ResponseVerified = false, FailureScope = "Target", FailureCode = "ShopResponseUnverified" },
            true, true) == ShopScanDisposition.Continue, "settled product absence became native pending"));
        Check("v15_file_presence_is_unknown_not_recovered", () =>
        {
            var f = new Fixture(); File.WriteAllText(f.SinglePending, "unknown");
            Need(ShopNativePendingPolicy.Observe(f.Source, f.Journal).State == ShopNativePendingState.Unknown, "V15 suffix ignored");
            Reject<ShopRequestFailure>(() => ShopNativePendingPolicy.RequireReady(ShopNativePendingPolicy.Observe(f.Source, f.Journal), false));
        });
        Check("typed_none_cannot_override_v15_pending", () =>
        {
            var f = new Fixture(); File.WriteAllText(f.SinglePending, "unknown");
            Need(ShopNativePendingPolicy.Observe(f.Source, f.Journal, s => new(s, ShopNativePendingState.None, null, "")).State
                == ShopNativePendingState.Unknown, "observer none erased a raw pending fence");
        });
        Check("foreign_typed_source_fails_closed", () =>
        {
            var f = new Fixture(); var status = ShopNativePendingPolicy.Observe(f.Source, f.Journal,
                s => new(s with { CreationFileTime = 22 }, ShopNativePendingState.None, null, ""));
            Need(status.State == ShopNativePendingState.Unknown, "foreign observation granted readiness");
        });
        Check("observer_exception_is_unknown", () =>
        {
            var f = new Fixture(); Need(ShopNativePendingPolicy.Observe(f.Source, f.Journal, _ => throw new IOException("fake failure")).State
                == ShopNativePendingState.Unknown, "observer failure granted readiness");
        });
        Check("current79d1_rejects_untagged_legacy_recovery", () =>
        {
            var f=new Fixture(useCurrentSource:true); bool refused=false;
            try { f.ReadProof(); }
            catch(InvalidDataException error) { refused=error.Message.Contains("tagged current producer",StringComparison.Ordinal); }
            Need(refused,"Current79D1 accepted an untagged legacy synthetic recovery");
        });
        Check("retained_committed_transaction_is_ready", () =>
        {
            var f = new Fixture(); f.WriteTransaction();
            Need(ShopPostCodeRecoveryProof.CommittedTransaction(f.Transaction, f.Source, f.Journal)
                && ShopNativePendingPolicy.Observe(f.Source, f.Journal).State == ShopNativePendingState.None,
                "retained committed transaction stays blocked");
        });
        Check("new_pending_after_committed_transaction_stays_blocked", () =>
        {
            var f = new Fixture(); f.WriteTransaction(); File.WriteAllText(f.SinglePending, "new distinct pending");
            Need(ShopNativePendingPolicy.Observe(f.Source, f.Journal).State == ShopNativePendingState.Unknown,
                "old committed transaction cleared a new native pending");
            string nextReceipt = Path.Combine(f.Root, "second-recovery.jsonl");
            Need(ShopPostCodeRecoveryOutput.ExpectedReceipt(f.Transaction, f.Source, nextReceipt, true, true) == nextReceipt,
                "second original pending selected the previous transaction receipt");
        });
        Check("incomplete_transaction_stays_blocked", () =>
        {
            var f = new Fixture(); f.WriteTransaction(Path.Combine(f.Root, "missing-receipt.jsonl"));
            Need(!ShopPostCodeRecoveryProof.CommittedTransaction(f.Transaction, f.Source, f.Journal)
                && ShopNativePendingPolicy.Observe(f.Source, f.Journal).State == ShopNativePendingState.Unknown,
                "prepared transaction without committed receipt granted readiness");
            Need(ShopPostCodeRecoveryOutput.ExpectedReceipt(f.Transaction, f.Source, Path.Combine(f.Root, "unused.jsonl"), true, false)
                == Path.Combine(f.Root, "missing-receipt.jsonl"), "resume selected a new receipt instead of the prepared transaction");
        });
        Check("only_typed_temporary_recovery_failure_can_wait", () =>
        {
            var error = JsonSerializer.SerializeToNode(new { VerifiedRecovered = false, Code = "PostCodeUnconfirmed", RetryableReadOnlyRecovery = true,
                NativeRequests = 0, GameInput = 0, OriginalRequestCompleted = false, ServerAcknowledgmentVerified = false })!;
            Need(ShopPostCodeRecoveryOutput.RetryableFailure(error.ToJsonString(), 1), "typed temporary readonly recovery was rejected");
            foreach (string field in new[] { "Code", "RetryableReadOnlyRecovery", "NativeRequests", "GameInput", "OriginalRequestCompleted", "ServerAcknowledgmentVerified" })
            {
                var bad = error.DeepClone().AsObject(); bad.Remove(field);
                Need(!ShopPostCodeRecoveryOutput.RetryableFailure(bad.ToJsonString(), 1), "missing " + field + " became retryable");
            }
            error["Code"] = "NativeOutcomeUnknown";
            Need(!ShopPostCodeRecoveryOutput.RetryableFailure(error.ToJsonString(), 1), "unknown became endless recovery");
        });
        await CheckAsync("same_original_waits_then_recovers_without_native_replay", async () =>
        {
            int attempts = 0, delays = 0;
            bool recovered = await ShopPostCodeRecoveryWait.RunAsync("original", () => true,
                _ => Task.FromResult(++attempts == 1 ? new ShopPostCodeRecoveryAttempt(false, true, null) : new(true, false, "original")),
                _ => { delays++; return Task.CompletedTask; }, CancellationToken.None);
            Need(recovered && attempts == 2 && delays == 1, "readonly wait did not retain one original until recovered");
        });
        await CheckAsync("generic_unknown_stops_without_recovery_loop", async () =>
        {
            int calls = 0;
            bool recovered = await ShopPostCodeRecoveryWait.RunAsync("original", () => true,
                _ => { calls++; return Task.FromResult(new ShopPostCodeRecoveryAttempt(false, false, null)); },
                _ => throw new InvalidOperationException("unknown must not wait"), CancellationToken.None);
            Need(!recovered && calls == 1, "unknown recovery kept retrying");
        });
        await CheckAsync("changed_original_stops_before_another_readonly_attempt", async () =>
        {
            int calls = 0; bool unchanged = true;
            bool recovered = await ShopPostCodeRecoveryWait.RunAsync("original", () => unchanged,
                _ => { calls++; return Task.FromResult(new ShopPostCodeRecoveryAttempt(false, true, null)); },
                _ => { unchanged = false; return Task.CompletedTask; }, CancellationToken.None);
            Need(!recovered && calls == 1, "changed original was retried");
        });
        await CheckAsync("wait_cancel_preserves_original_without_second_attempt", async () =>
        {
            using var stop = new CancellationTokenSource(); int calls = 0;
            await RejectAsync<OperationCanceledException>(() => ShopPostCodeRecoveryWait.RunAsync("original", () => true,
                _ => { calls++; return Task.FromResult(new ShopPostCodeRecoveryAttempt(false, true, null)); },
                _ => { stop.Cancel(); return Task.Delay(1000, stop.Token); }, stop.Token));
            Need(calls == 1, "stop sent another readonly attempt");
        });
        await CheckAsync("recovered_other_request_cannot_advance_current_page", async () =>
        {
            bool recovered = await ShopPostCodeRecoveryWait.RunAsync("original", () => true,
                _ => Task.FromResult(new ShopPostCodeRecoveryAttempt(true, false, "another")), _ => Task.CompletedTask, CancellationToken.None);
            Need(!recovered, "another request allowed next current page");
        });
        await CheckAsync("recovery_wait_preserves_captured_minimum_and_counts_recovery_time", async () =>
        {
            TimeSpan observed = TimeSpan.Zero; int calls = 0;
            await ShopPostCodePageWait.WaitRemainingMillisecondsAsync(5000, TimeSpan.FromSeconds(3), CancellationToken.None,
                (remaining, _) => { observed = remaining; calls++; return Task.CompletedTask; });
            Need(calls == 1 && observed == TimeSpan.FromSeconds(2), "recovery appended full wait or skipped captured minimum");
        });
        await CheckAsync("long_recovery_does_not_add_page_wait_again", async () =>
        {
            await ShopPostCodePageWait.WaitRemainingMillisecondsAsync(100, TimeSpan.FromSeconds(10), CancellationToken.None,
                (_, _) => throw new InvalidOperationException("recovery already satisfied captured page wait"));
        });
        await CheckAsync("cancel_remaining_page_wait_stops_without_next_page", async () =>
        {
            using var stop = new CancellationTokenSource();
            await RejectAsync<OperationCanceledException>(() => ShopPostCodePageWait.WaitRemainingMillisecondsAsync(5000, TimeSpan.FromSeconds(1), stop.Token,
                (_, _) => { stop.Cancel(); return Task.CompletedTask; }));
        });
        Check("transaction_wrong_owner_binding_stays_blocked", () =>
        {
            var f = new Fixture(); f.WriteTransaction();
            var tx = JsonNode.Parse(File.ReadAllText(f.Transaction))!; tx["IntendedReceipt"]!["TargetOwner"] = 123UL;
            File.WriteAllText(f.Transaction, tx.ToJsonString());
            Need(!ShopPostCodeRecoveryProof.CommittedTransaction(f.Transaction, f.Source, f.Journal), "transaction owner mismatch ignored");
        });
        Check("durable_proof_does_not_expire_or_limit_capture_duration", () =>
        {
            var f = new Fixture(); var r = f.ReceiptNode.DeepClone();
            r["FreshProof"]!["StartedAt"] = DateTimeOffset.UtcNow.AddDays(-1);
            r["FreshProof"]!["FinishedAt"] = DateTimeOffset.UtcNow.AddDays(-1).AddSeconds(10);
            r["OriginalEndAt"] = DateTimeOffset.UtcNow.AddDays(-2);
            File.WriteAllText(f.Receipt, r.ToJsonString()); f.ReadProof();
        });
        await CheckAsync("recovery_callback_return_does_not_settle_local_intent", async () =>
        {
            var f = new Fixture(); int calls = 0;
            await RejectAsync<ShopRequestFailure>(() => ShopNativePendingPolicy.RecoverAndRequireReadyAsync(f.Source,
                () => new(f.Source, ShopNativePendingState.None, null, ""), () => true,
                (_, _) => { calls++; return Task.CompletedTask; }, CancellationToken.None));
            Need(calls == 1, "recovery was launched repeatedly in one gate");
        });
        await CheckAsync("cancel_wait_keeps_original_pending", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
            Task gate = ShopNativePendingPolicy.RecoverAndRequireReadyAsync(f.Source,
                () => new(f.Source, ShopNativePendingState.ReturnedPostCodePending, "original", "pending"), () => true,
                (_, _) => { calls++; return waiting.Task; }, cancel.Token);
            cancel.Cancel(); await RejectAsync<OperationCanceledException>(() => gate);
            Need(calls == 1 && !waiting.Task.IsCompleted, "cancel modified the original recovery task"); waiting.SetResult();
        });
        await CheckAsync("verified_callback_rechecks_state_before_next_work", async () =>
        {
            var f = new Fixture(); bool pending = true; int calls = 0, observations = 0;
            await ShopNativePendingPolicy.RecoverAndRequireReadyAsync(f.Source,
                () => { observations++; return new(f.Source, pending ? ShopNativePendingState.ReturnedPostCodePending : ShopNativePendingState.None, pending ? "original" : null, ""); },
                () => pending, (_, _) => { calls++; pending = false; return Task.CompletedTask; }, CancellationToken.None);
            Need(calls == 1 && observations == 2, "gate did not recheck recovery state");
        });
        Check("force_store_start_continue_and_view_fence_actual_unknown", () =>
        {
            var f = new Fixture(localIntent: true);
            Reject<ShopRequestFailure>(() => f.Scans.StartNew(f.Source, f.Targets, true));
            Reject<ShopRequestFailure>(() => f.Scans.Continue(f.Scans.PlanId, f.Source, f.Targets, forceDispatch: true));
            var state = f.Scans.View(f.Source, f.Targets, false, true, true)!;
            Need(!state.CanContinue && state.ContinueBlockCode == "SessionBlocked" && state.UncertainPages == 1, "unknown became Ready");
        });
        Check("strict_receipt_binds_output_and_all_source_fields", () =>
        {
            var f = new Fixture(); var proof = f.ReadProof();
            string stdout = JsonSerializer.Serialize(new { VerifiedRecovered = true, RecoveryReceiptPath = f.Receipt,
                RecoveryReceiptSha256 = ShopPostCodeRecoveryProof.Hash(f.Receipt), Receipt = f.ReceiptNode });
            Need(ShopPostCodeRecoveryOutput.Read(stdout, f.Receipt, f.Source, f.Journal) == proof, "output did not bind receipt");
            Reject<InvalidDataException>(() => ShopPostCodeRecoveryProof.Read(f.Receipt, f.Source with { CreationFileTime = 1 }, f.Journal));
        });
        foreach (int mask in new[] { 1, 2, 3 }) Check("lifecycle_receipt_accepts_known_protected_mask_" + mask, () =>
        {
            var f = new Fixture(); f.UseLifecycle(mask); var proof = f.ReadProof();
            Need(proof.LifecycleOnly && proof.CompletionDetail.Contains("事後程式頁仍未確認", StringComparison.Ordinal),
                "lifecycle was represented as current code confirmed");
            string output = JsonSerializer.Serialize(new { VerifiedRecovered = true, RecoveryReceiptPath = f.Receipt,
                RecoveryReceiptSha256 = ShopPostCodeRecoveryProof.Hash(f.Receipt), Receipt = f.ReceiptNode });
            Need(ShopPostCodeRecoveryOutput.Read(output, f.Receipt, f.Source, f.Journal) == proof, "stdout rejected bound lifecycle proof");
            f.WriteTransaction(); Need(ShopPostCodeRecoveryProof.CommittedTransaction(f.Transaction, f.Source, f.Journal), "committed lifecycle remains blocked");
        });
        foreach (string mutation in new[] { "unknown_schema", "unsettled", "wrong_disposition", "current_claim", "missing_contract", "wrong_contract",
            "zero_mask", "unknown_mask", "fresh_current_claim", "all_bytes_claim", "no_deferred_claim", "guard_count", "extra_guard", "original_post_claim", "original_cleanup_missing", "ack_claim" })
            Check("lifecycle_rejects_" + mutation, () =>
            {
                var f = new Fixture(); f.UseLifecycle(); var r = f.ReceiptNode.DeepClone().AsObject();
                switch (mutation)
                {
                    case "unknown_schema": r["Schema"] = "UnknownLifecycleV1"; break;
                    case "unsettled": r["LifecycleSettled"] = false; break;
                    case "wrong_disposition": r["Disposition"] = "CurrentBytesVerified"; break;
                    case "current_claim": r["CurrentCreateCopyBytesVerified"] = true; break;
                    case "missing_contract": r["FreshProof"]!.AsObject().Remove("ReturnedLifecycleContract"); break;
                    case "wrong_contract": r["FreshProof"]!["ReturnedLifecycleContract"] = 2; break;
                    case "zero_mask": r["FreshProof"]!["UnavailableProtectedMask"] = 0; break;
                    case "unknown_mask": r["FreshProof"]!["UnavailableProtectedMask"] = 4; break;
                    case "fresh_current_claim": r["FreshProof"]!["CurrentCreateCopyBytesVerified"] = true; break;
                    case "all_bytes_claim": r["FreshProof"]!["AllCurrentBytesMatched"] = true; break;
                    case "no_deferred_claim": r["FreshProof"]!["NoDeferredBodies"] = true; break;
                    case "guard_count": r["FreshProof"]!["MetadataAndDispatchProofs"]!.AsArray().RemoveAt(0); break;
                    case "extra_guard": r["FreshProof"]!["MetadataAndDispatchProofs"]!.AsArray().Add(new JsonObject()); break;
                    case "original_post_claim": r["OriginalPostCodeConfirmed"] = true; break;
                    case "original_cleanup_missing": r.Remove("OriginalCleanupConfirmed"); break;
                    case "ack_claim": r["ServerAcknowledgmentVerified"] = true; break;
                }
                File.WriteAllText(f.Receipt, r.ToJsonString()); Reject<Exception>(() => f.ReadProof());
            });
        Check("strict_schema_does_not_accept_lifecycle_partial_proof", () =>
        {
            var f = new Fixture(); f.UseLifecycle(); f.ReceiptNode["Schema"] = "DelayedPostCodeRecoveryV1";
            File.WriteAllText(f.Receipt, f.ReceiptNode.ToJsonString()); Reject<InvalidDataException>(() => f.ReadProof());
        });
        foreach (bool rootField in new[] { false, true }) Check("strict_rejects_lifecycle_extra_flags_" + rootField, () =>
        {
            var f = new Fixture();
            if (rootField) f.ReceiptNode["LifecycleSettled"] = true;
            else f.ReceiptNode["FreshProof"]!["ReturnedLifecycleContract"] = 1;
            File.WriteAllText(f.Receipt, f.ReceiptNode.ToJsonString()); Reject<InvalidDataException>(() => f.ReadProof());
        });
        Check("prepared_strict_transaction_cannot_downgrade_to_lifecycle", () =>
        {
            var f = new Fixture(); f.WriteTransaction(); f.UseLifecycle();
            Need(!ShopPostCodeRecoveryProof.CommittedTransaction(f.Transaction, f.Source, f.Journal), "prepared strict proof was silently downgraded");
        });
        Check("prepared_lifecycle_transaction_can_upgrade_to_strict", () =>
        {
            var f = new Fixture(); var strict = f.ReceiptNode.DeepClone(); f.UseLifecycle(); f.WriteTransaction();
            File.WriteAllText(f.Receipt, strict.ToJsonString());
            Need(ShopPostCodeRecoveryProof.CommittedTransaction(f.Transaction, f.Source, f.Journal), "full fresh match cannot upgrade prepared lifecycle");
        });
        Check("lifecycle_preserves_original_result_and_unverified_page", () =>
        {
            var f = new Fixture(localIntent: true); string before = ShopPostCodeRecoveryProof.Hash(f.OriginalResult); f.UseLifecycle();
            var result = ShopPostCodeLocalRecovery.Apply(f.Local, f.Scans, f.Owners, f.ReadProof());
            var view = f.Scans.View(f.Source, f.Targets, false, false, true)!;
            Need(ShopPostCodeRecoveryProof.Hash(f.OriginalResult) == before && view.SearchedPages == 1 && view.ConfirmedPages == 0
                && view.CanContinue && view.RemainingPages == 1 && result.Detail.Contains("事後程式頁仍未確認", StringComparison.Ordinal),
                "lifecycle rewrote the original or claimed product/code confirmation");
        });
        foreach (string mutation in new[] { "native_input", "ack", "missing_cleanup", "wrong_source", "wrong_hash", "missing_proofs", "deferred", "pending_remains" })
            Check("receipt_rejects_" + mutation, () =>
            {
                var f = new Fixture(); var r = f.ReceiptNode.DeepClone().AsObject();
                switch (mutation)
                {
                    case "native_input": r["NativeRequests"] = 1; break;
                    case "ack": r["ServerAcknowledgmentVerified"] = true; break;
                    case "missing_cleanup": r["OriginalCleanupConfirmed"] = false; break;
                    case "wrong_source": r["Source"]!["Creation"] = 1; break;
                    case "wrong_hash": r["OriginalNativeLogSha256"] = new string('B', 64); break;
                    case "missing_proofs": r["FreshProof"]!["MetadataAndDispatchProofs"] = new JsonArray(); break;
                    case "deferred": r["FreshProof"]!["NoDeferredBodies"] = false; break;
                    case "pending_remains": File.WriteAllText(f.SinglePending, "still pending"); break;
                }
                string altered = Path.Combine(f.Root, mutation + ".json"); File.WriteAllText(altered, r.ToJsonString());
                Reject<InvalidDataException>(() => ShopPostCodeRecoveryProof.Read(altered, f.Source, f.Journal));
            });
        Check("append_only_recovery_marks_sent_unverified_and_never_resends", () =>
        {
            var f = new Fixture(localIntent: true); string originalHash = ShopPostCodeRecoveryProof.Hash(f.OriginalResult);
            var result = ShopPostCodeLocalRecovery.Apply(f.Local, f.Scans, f.Owners, f.ReadProof());
            Need(result.Matched == 1 && result.RequestId == f.RequestId, "original request not matched");
            Need(ShopPostCodeRecoveryProof.Hash(f.OriginalResult) == originalHash, "original Result was rewritten");
            var state = f.Scans.View(f.Source, f.Targets, false, false, true)!;
            Need(state.SearchedPages == 1 && state.ConfirmedPages == 0 && state.UncertainPages == 0 && state.RemainingPages == 1,
                "recovery claimed product confirmation or requeued original page");
            Need(state.CanContinue && state.ContinueBlockCode == "Ready", "recovered progress did not become Ready");
            Need(f.Owners.ShouldSkip(f.Owner.Id, ShopPageModes.ViewerBuy, true) && !f.Owners.BlocksSource(f.Source), "owner result did not remain Sent");
            Need(!File.Exists(f.LocalPending) && Directory.GetFiles(Path.GetDirectoryName(f.LocalPending)!, "*.resolved-postcode-*.json").Length == 1,
                "local pending was not archived exactly once");
            var reload = new ShopScanProgressStore(Path.Combine(f.Local, "scan-progress"));
            Need(reload.Error is null && !reload.BlocksSource(f.Source) && reload.PendingPages(f.Targets, true).Single().Direction == ShopPageModes.ViewerSell,
                "replay lost recovery event or retransmits original page");
        });
        foreach (string phase in new[] { "ProofDurable", "ScanRecovered", "OwnerRecovered" }) Check("crash_resume_" + phase, () =>
        {
            var f = new Fixture(localIntent: true); string originalHash = ShopPostCodeRecoveryProof.Hash(f.OriginalResult);
            Reject<IOException>(() => ShopPostCodeLocalRecovery.Apply(f.Local, f.Scans, f.Owners, f.ReadProof(), at => { if (at == phase) throw new IOException("synthetic crash"); }));
            var scans = new ShopScanProgressStore(Path.Combine(f.Local, "scan-progress"));
            var owners = new ShopSearchedOwnerStore(Path.Combine(f.Local, "searched-owners"), Path.Combine(f.Local, "scan-progress"));
            ShopPostCodeLocalRecovery.Apply(f.Local, scans, owners, f.ReadProof());
            Need(scans.Error is null && !scans.BlocksSource(f.Source) && !owners.BlocksSource(f.Source)
                && ShopPostCodeRecoveryProof.Hash(f.OriginalResult) == originalHash, "crash replay altered original or retained wrong state");
        });
        Check("fresh_session_without_original_local_intent_creates_no_success", () =>
        {
            var f = new Fixture(); var result = ShopPostCodeLocalRecovery.Apply(f.Local, f.Scans, f.Owners, f.ReadProof());
            Need(result.Matched == 0 && f.Scans.PlanId is null && !f.Owners.BlocksSource(f.Source), "fresh session invented original page");
        });
        Check("unrelated_unknown_intent_stays_blocked", () =>
        {
            var f = new Fixture(localIntent: true); var other = f.Scans.PendingPages(f.Targets, true).Single();
            f.Scans.BeginAttempt(other, "another-unknown"); f.Scans.NativeIntent("another-unknown");
            f.Scans.Result("another-unknown", new() { SessionBlocked = true, Detail = "unrelated original failure" });
            f.Owners.Begin(f.Source, f.Owner, ShopPageModes.ViewerSell, "another-unknown", true);
            ShopPostCodeLocalRecovery.Apply(f.Local, f.Scans, f.Owners, f.ReadProof());
            Need(f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source), "recovery cleared unrelated unknown intent");
        });
        Check("production_publish_paths_call_common_fence", () =>
        {
            var force = Calls(AsyncBody("OpenForcedAsync")).ToArray();
            Need(force.Count(m => m.Name == "EnsureUnblocked") >= 2 && force.Any(m => m.Name == "RecoverNativePendingBeforePublishAsync"), "forced open gates missing");
            Need(Calls(AsyncBody("StartScanAsync")).Any(m => m.Name == "RecoverNativePendingBeforePublishAsync"), "scan starts without recovery gate");
            Need(Calls(AsyncBody("ScanAsync")).Any(m => m.Name == "RecoverCurrentScanPageAsync"), "active scan never enters readonly pending wait");
            Need(Calls(AsyncBody("RecoverCurrentScanPageAsync")).Any(m => m.Name == "WaitAfterRecoveryAsync"), "recovered page ignores captured pacing");
            Need(Calls(typeof(ShopBroker).GetMethod("ControlScan", BindingFlags.NonPublic | BindingFlags.Instance)!).Any(m => m.Name == "ObserveNativePending"), "resume ignores pending");
        });
        Check("overlay_distinguishes_native_pending_from_stale_ui", () =>
        {
            var response = new ShopCommandResponse { SessionBlocked = true, FailureCode = "NativeOutcomeUnknown", FailureScope = "Session" };
            Need(SearchOverlayPresentation.Build(response, true, "", false, forceDispatch: true).State == "原生工作待恢復", "current native pending shown as ready");
            Need(SearchOverlayPresentation.Build(response with { FailureCode = null, FailureScope = null }, true, "", false, forceDispatch: true).State == "上次操作結果未確認",
                "old UI-only observation now grants a false authoritative native state");
        });
        return checks.ToArray();
    }

    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(TimeSpan.FromSeconds(2)); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static MethodInfo AsyncBody(string name) => typeof(ShopBroker).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        var ops = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        for (int at = 0; at < bytes.Length;)
        {
            ushort key = bytes[at++]; if (key == 0xfe) key = (ushort)(0xfe00 | bytes[at++]); var op = ops[key];
            int size = op.OperandType switch { OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(bytes, at)), _ => 4 };
            if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt))
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, at), method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments())!;
            at += size;
        }
    }

    private sealed class Fixture
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "LC-Market67-postcode-fake-" + Guid.NewGuid().ToString("N"));
        internal string Local => Path.Combine(Root, "local"); internal string Journal => Path.Combine(Root, "journal");
        internal readonly SourceIdentity Source; internal readonly MarketOwner Owner; internal readonly ShopTargetSnapshot Targets;
        internal readonly ShopScanProgressStore Scans; internal readonly ShopSearchedOwnerStore Owners;
        internal readonly JsonObject ReceiptNode; internal readonly string Receipt, SinglePending;
        internal string Transaction => Path.Combine(Journal, "4242-987654321.delayed-postcode-recovery-v1.transaction.json");
        internal string RequestId => "original-native-request";
        internal string LocalPending => ShopForceDispatchPolicy.PendingPath(Local, Source.ProcessId + "-" + Source.CreationFileTime, RequestId);
        internal string OriginalResult { get; } = "";
        internal Fixture(bool localIntent = false, bool useCurrentSource = false)
        {
            Directory.CreateDirectory(Root); Directory.CreateDirectory(Local); Directory.CreateDirectory(Journal);
            Directory.CreateDirectory(Path.Combine(Local, "sessions")); Directory.CreateDirectory(Path.Combine(Local, "requests"));
            Source = new(4242, 987654321, Path.Combine(Root, "synthetic-LC.exe"), "0x100000",
                useCurrentSource ? ShopNativePackage.SupportedTimestamp : ShopNativeHistoricalPackage.SupportedTimestamp,
                useCurrentSource ? ShopNativePackage.SupportedImageSize : ShopNativeHistoricalPackage.SupportedImageSize,
                useCurrentSource ? ShopNativePackage.SupportedSha : ShopNativeHistoricalPackage.SupportedSha, "synthetic-postcode-source");
            var now = DateTimeOffset.UtcNow;
            Owner = new() { Id = 99, LocalIdentity = "owner-99", Name = "synthetic shop", Position = new(100, 101), ObservedAt = now,
                ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@",
                ShopStateSampledAt = now, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            Targets = new() { SnapshotId = "synthetic-targets", SourceSessionKey = Source.SessionKey, RefreshedAt = now,
                SelfId = 11, SelfPosition = new(100, 100), Owners = [Owner], CanDispatch = true };
            Scans = new(Path.Combine(Local, "scan-progress")); Owners = new(Path.Combine(Local, "searched-owners"), Path.Combine(Local, "scan-progress"));
            string stem = Path.Combine(Journal, "4242-987654321"), force = stem + ".force-0123456789abcdef0123456789abcdef.pending.json";
            SinglePending = stem + ".v15-singletrial.pending.json";
            string forceArchive = force[..^".pending.json".Length] + ".resolved-postcode-a.json";
            string singleArchive = SinglePending[..^".pending.json".Length] + ".resolved-postcode-a.json";
            File.WriteAllText(forceArchive, "synthetic-force-original"); File.WriteAllText(singleArchive, "synthetic-single-original");
            string native = Path.Combine(Root, "0123456789abcdef0123456789abcdef.jsonl"); File.WriteAllText(native, "synthetic original native log; not a real call");
            var identity = new { Pid = Source.ProcessId, Creation = Source.CreationFileTime, Image = 0x100000UL,
                Path = Source.ImagePath, Sha256 = Source.DiskSha256, Source.PeTimestamp, Source.ImageSize };
            ReceiptNode = JsonSerializer.SerializeToNode(new { Schema = "DelayedPostCodeRecoveryV1", Phase = "Committed", TransactionId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root))),
                OriginalNativeLogPath = native, OriginalNativeLogSha256 = ShopPostCodeRecoveryProof.Hash(native), Source = identity,
                TargetOwner = Owner.Id, Mode = 1, NativeNonce = 12345UL, OriginalForcePendingPath = force,
                OriginalForcePendingSha256 = ShopPostCodeRecoveryProof.Hash(forceArchive), ForceResolvedPath = forceArchive,
                OriginalSingleTrialPendingPath = SinglePending, OriginalSingleTrialPendingSha256 = ShopPostCodeRecoveryProof.Hash(singleArchive), SingleTrialResolvedPath = singleArchive,
                OriginalEndAt = now.AddSeconds(-5), OriginalPostCodeConfirmed = false, OriginalNativeReturned = true, OriginalCleanupConfirmed = true,
                OriginalRequestCompletionVerified = false, ServerAcknowledgmentVerified = false, RemoteModuleUnloadProven = false, EntireHookTailExitProven = false,
                OnlyFutureDistinctRequestsMayProceed = true, NoAutomaticRetransmission = true, NativeRequests = 0, GameInput = 0,
                FreshProof = new { Identity = identity, StartedAt = now, FinishedAt = now.AddMilliseconds(10),
                    AllCurrentBytesMatched = true, NoDeferredBodies = true, ReadCalls = 7, ReadBytes = 700,
                    MetadataAndDispatchProofs = Enumerable.Range(0, 7).Select(i => new { Synthetic = true, Index = i }).ToArray() } })!.AsObject();
            Receipt = Path.Combine(Root, "recovery-receipt.jsonl"); File.WriteAllText(Receipt, ReceiptNode.ToJsonString());
            if (!localIntent) return;
            Scans.StartNew(Source, Targets, true); var page = Scans.PendingPages(Targets, true).First(p => p.Direction == ShopPageModes.ViewerBuy);
            Scans.BeginAttempt(page, RequestId); Scans.NativeIntent(RequestId);
            Owners.Begin(Source, Owner, ShopPageModes.ViewerBuy, RequestId, true);
            Scans.Result(RequestId, new() { SessionBlocked = true, NativeEntered = true, NativeReturned = false, Detail = "original postcode failure retained" });
            OriginalResult = Directory.GetFiles(Path.Combine(Local, "scan-progress", Scans.PlanId + ".events"), "*.json").Order(StringComparer.Ordinal).Last();
            var request = new ShopCommandRequest { RequestId = RequestId, Command = ShopCommands.OpenShop, ForceDispatch = true,
                ExpectedSourceSessionKey = Source.SessionKey, OwnerId = Owner.Id, ExpectedOwnerIdentity = Owner.LocalIdentity,
                ViewerDirection = ShopPageModes.ViewerBuy, TargetSnapshotId = Targets.SnapshotId };
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RequestId)));
            string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HubProtocol.Json))));
            File.WriteAllText(Path.Combine(Local, "requests", key + ".request.json"), JsonSerializer.Serialize(new
                { Request = request, Signature = signature, State = "ScanChild_NoAutomaticReplay", At = now }, HubProtocol.Json));
            File.WriteAllText(LocalPending, JsonSerializer.Serialize(new { State = "ForceNativeLaunchIntent_NoAutomaticReplay", RequestId,
                ForceDispatch = true, Source, Owner, TargetOwner = Owner.Id, ViewerDirection = ShopPageModes.ViewerBuy, NativeLog = native, At = now }, HubProtocol.Json));
        }
        internal ShopPostCodeRecoveredProof ReadProof() => ShopPostCodeRecoveryProof.Read(Receipt, Source, Journal);
        internal void UseLifecycle(int mask = 3)
        {
            ReceiptNode["Schema"] = "ReturnedLifecycleRecoveryV1"; ReceiptNode["LifecycleSettled"] = true;
            ReceiptNode["Disposition"] = "ReturnedAndCleaned_PostCodeUnavailable"; ReceiptNode["CurrentCreateCopyBytesVerified"] = false;
            ReceiptNode["FreshProof"]!["ReturnedLifecycleContract"] = 1; ReceiptNode["FreshProof"]!["UnavailableProtectedMask"] = mask;
            ReceiptNode["FreshProof"]!["CurrentCreateCopyBytesVerified"] = false;
            ReceiptNode["FreshProof"]!["AllCurrentBytesMatched"] = false; ReceiptNode["FreshProof"]!["NoDeferredBodies"] = false;
            File.WriteAllText(Receipt, ReceiptNode.ToJsonString());
        }
        internal void WriteTransaction(string? receipt = null) => File.WriteAllText(Transaction, JsonSerializer.Serialize(new
        {
            Schema = "DelayedPostCodeRecoveryTransactionV1", Phase = "Prepared", TransactionId = ReceiptNode["TransactionId"]!.GetValue<string>(),
            RecoveryReceiptPath = receipt ?? Receipt, IntendedReceipt = ReceiptNode,
            PendingFencesMayBeRemovedWithoutNativeReplay = true, NativeRequests = 0, GameInput = 0
        }));
    }
}
