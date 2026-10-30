using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Pure presentation checks; no controller, backend, process or game operation.</summary>
internal static class SearchOverlayCurrentRequestVerification
{
    internal static IReadOnlyList<OfflineCheck> Run()
    {
        var results = new List<OfflineCheck>();
        var scan = new MarketScanState { JobId="old-job", SourceSessionKey="same-source", TargetSnapshotId="old-target",
            Running=false, Total=88, Completed=88, Succeeded=66, Failed=22, TotalShops=44, CompletedShops=44 };
        var refused = new ShopCommandResponse { RequestId="new-request", State="Rejected", CurrentSourceSessionKey="same-source",
            WorkSettled=true, FailureCode="NativeCodeUnavailable", FailureScope="Global", FailurePhase="BeforePublish",
            FailureRva="0x76BEB0", Detail="Current exact readonly preflight did not pass.", Scan=scan };
        void Check(string name, Action body)
        { try { body(); results.Add(new("overlay_current_request_"+name,true,"Pure presentation; original source and assertions preserved.")); }
          catch(Exception error) { results.Add(new("overlay_current_request_"+name,false,error.Message)); } }
        static void Need(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
        static SearchOverlayView Build(ShopCommandResponse value,bool force=false,bool pending=false) =>
            SearchOverlayPresentation.Build(value,true,"",pending,forceDispatch:force);

        foreach(bool force in new[]{false,true})
            Check("settled_beforepublish_overrides_old_complete_force_"+force,()=>
            {
                var value=Build(refused,force);
                Need(value.State=="本次請求未受理" && value.Player=="" && !value.Busy,"Old completed scan became new request success");
                Need(value.Progress.StartsWith("上一批搜尋紀錄：") && value.Progress.Contains("88/88"),"Previous progress not identified as historical");
                Need(value.Detail.Contains("NativeCodeUnavailable") && value.Detail.Contains("0x76BEB0")
                    && value.Detail.Contains(refused.Detail) && value.Detail.Contains("沒有送出新的查詢"),"Current refusal details were lost");
                Need(!value.Detail.Contains("未釋放") && !value.Detail.Contains("開啟一家玩家商店"),"Unproved cleanup or initialization instruction");
            });
        Check("old_partial_progress_does_not_replace_refusal",()=>
        {
            var value=Build(refused with{Scan=scan with{Completed=20,Succeeded=18,Failed=2}});
            Need(value.State=="本次請求未受理" && value.Progress.StartsWith("上一批搜尋紀錄：") && value.Progress.Contains("20/88"),"Old partial result replaced current refusal");
        });
        Check("old_unknown_reason_does_not_replace_current_reason",()=>
        {
            var value=Build(refused with{Scan=scan with{StopReason="OldUnknown",FailureCode="OldUnknown",FailureDetail="old unrelated reason"}});
            Need(value.State=="本次請求未受理" && !value.Detail.Contains("OldUnknown") && value.Detail.Contains(refused.Detail),"Mixed causes from different requests");
        });
        Check("rejected_without_code_is_not_complete",()=>
        {
            var value=Build(refused with{FailureCode=null,FailureRva=null});
            Need(value.State=="本次請求未受理" && value.Detail.Contains(refused.Detail),"Missing code converted explicit refusal to success");
        });
        Check("explicit_failure_code_overrides_old_complete",()=>
        {
            var value=Build(refused with{State="Failed",FailurePhase="Unknown"});
            Need(value.State=="本次請求未完成" && !value.Detail.Contains("沒有送出新的查詢"),"Failure inferred a no-entry proof");
        });
        foreach(string uncertainty in new[]{"unsettled","entered","returned","verified","phase"})
            Check("incomplete_evidence_"+uncertainty+"_never_claims_no_entry",()=>
            {
                var response=uncertainty switch {
                    "unsettled"=>refused with{WorkSettled=false},"entered"=>refused with{NativeEntered=true},
                    "returned"=>refused with{NativeReturned=true},"verified"=>refused with{ResponseVerified=true},
                    _=>refused with{FailurePhase="Unknown"} };
                var value=Build(response);
                Need(value.State=="本次請求未完成" && !value.Detail.Contains("沒有送出新的查詢"),"Incomplete evidence was upgraded to before-publish rejection");
            });
        Check("different_source_never_copies_old_counts",()=>
        {
            var value=Build(refused with{CurrentSourceSessionKey="new-source"});
            Need(value.State=="本次請求未受理" && !value.Progress.Contains("88/88") && value.Player=="","Foreign source supplied progress");
        });
        Check("no_previous_job_never_invents_historical_counts",()=>
        {
            var value=Build(refused with{Scan=scan with{JobId=null}});
            Need(value.State=="本次請求未受理" && !value.Progress.Contains("88/88"),"Unidentified scan supplied history");
        });
        Check("actual_current_batch_is_not_labelled_previous",()=>
        {
            var value=Build(refused with{State="Failed",FailurePhase="AfterPublish",Scan=scan with{JobId="new-request"}});
            Need(value.State=="本次請求未完成" && value.Progress.StartsWith("本次批次進度："),"Current job mislabeled as prior request");
        });
        Check("previous_running_worker_is_still_busy",()=>
        {
            var value=Build(refused with{WorkSettled=false,Scan=scan with{Running=true,Completed=20}});
            Need(value.State=="本次請求未完成" && value.Busy && value.Progress.StartsWith("既有批次進度（非本次請求）："),"Earlier active worker hidden by current refusal");
        });
        Check("native_pending_and_pending_request_priority_unchanged",()=>
        {
            var native=refused with{SessionBlocked=true,FailureCode="NativeOutcomeUnknown",FailureScope="Session",WorkSettled=false};
            Need(Build(native,true,true).State=="原生工作待恢復","Native pending recovery priority changed");
            Need(Build(refused,true,true).State=="指令已送出" && Build(refused,false,true).State=="等待確認","Request-in-flight display priority changed");
            Need(Build(refused with{SessionBlocked=true},true).State=="上次操作結果未確認","Existing blocked-session presentation changed");
        });
        Check("normal_completed_state_stays_completed",()=>
        {
            var normal=refused with{State="State",FailureCode=null,FailureScope=null,FailurePhase=null,FailureRva=null};
            var value=Build(normal);
            Need(value.State=="搜尋完成" && !value.Progress.StartsWith("上一批") && value.Detail.Contains("確認 66／未讀到商品 0（方向未確認）／失敗或未確認 22"),"Legal completed observation was changed");
        });
        Check("normal_partial_and_uncertain_states_stay_stopped",()=>
        {
            var normal=refused with{State="State",FailureCode=null,FailureScope=null,FailurePhase=null,FailureRva=null};
            Need(Build(normal with{Scan=scan with{Completed=20}}).State=="已停止","Legal partial observation was changed");
            Need(Build(normal with{Scan=scan with{StopReason="NativeOutcomeUnknown"}}).State=="已停止","Legal stopped/uncertain observation was changed");
            Need(Build(normal with{Scan=new(){SourceSessionKey="same-source"}}).State=="已停止","Unknown zero-page state was changed");
        });
        Check("state_and_stopping_failure_fields_remain_batch_observations",()=>
        {
            foreach(string state in new[]{"State","Stopping"})
            {
                var value=Build(refused with{State=state,Scan=scan with{StopReason="NativeCodeUnavailable"}});
                Need(value.State=="已停止" && !value.Progress.StartsWith("上一批"),"Historical batch failure became a new rejected command");
            }
        });
        return results;
    }
}
