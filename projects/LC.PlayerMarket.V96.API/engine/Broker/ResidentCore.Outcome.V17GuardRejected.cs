using System.Globalization;
using System.Text.Json;
using static MemoryHub.Service.ShopResidentB47CContract;

namespace MemoryHub.Service;

internal sealed partial record ShopResidentNativeOutcome
{
    // Published transport is not a native query. This is deliberately narrower
    // than Entered == 0 and does not explain which source_guard clause failed.
    private static bool ConfirmedV17GuardRejected(string text, SourceIdentity source, ulong image,
        ulong owner, JsonElement native, JsonElement end, int requestOutcomeCode)
    {
        if (requestOutcomeCode != 1 || native.GetProperty("Status").GetInt32() != -3) return false;
        var rows = Rows(text);
        Need(ConfirmedV17CurrentEvidence(text, source, image, owner, native, false)
            && ConfirmedV15ResultReceipt(text, source, image, owner, native, end, false)
            && V15NoEntryContractMatches(native), "Guard refusal lacks complete original receipt");
        Need(B(end,"EventResultDurable") && B(end,"CleanupConfirmed") && !B(end,"SessionBlocked")
            && !B(end,"PostCodeConfirmed") && !B(end,"NativeAlreadyReturned")
            && !B(end,"BorrowedSourceContractVerified"), "Guard refusal terminal differs");
        foreach (string key in new[]{"Entered","Returned","Active","Stage","Exception","ExceptionStage",
            "ExceptionAddress","ExceptionAccessAddress","ExceptionAccessKind","ExceptionCount",
            "ReleaseExceptionCode","ReleaseExceptionAddress","LeaseAcquired","LeaseReleaseAttempted",
            "LeaseReleaseCompleted","RequestMetadataBefore","RequestMetadataAfter","RequestUnchanged",
            "DestructorCalls","PostCodeConfirmed"}) Need(N(native,key)==0,"Guard refusal contains work: "+key);
        Need(N(native,"Completed")==1 && N(native,"Claimed")==1 && N(native,"Sequence")==1
            && N(native,"Callback")>=0x10000 && N(native,"Module")>=0x10000 && N(native,"Tick")>0,
            "Guard refusal callback identity missing");
        Need(!B(V17Data(rows,"NATIVE_FINAL_STATE"),"DisarmedBeforeClaim"),"Claimed callback was disarmed");
        Need(!rows.Any(r=>T(r,"Kind") is "PACKET_CODE_CONFIRMED" or "BORROWED_SOURCE_VERIFIED"),
            "Guard refusal claims successful query proof");
        var errorRow=V17One(rows,"ERROR"); var error=F(errorRow,"Data");
        Need(T(error,"Type")=="InvalidOperationException" && T(error,"Code")=="Unknown"
            && T(error,"Phase")=="Native" && F(error,"Rva").ValueKind==JsonValueKind.Null
            && T(error,"Message")=="Native event failed or did not return normally.","Guard refusal error differs");
        ulong sequence(string kind)=>N(V17One(rows,kind),"Sequence");
        Need(sequence("ERROR")==sequence("EVENT_RESULT")+1
            && sequence("NATIVE_FINAL_STATE")==sequence("ERROR")+1
            && sequence("JOURNAL_SETTLED")==sequence("CLEANUP")+1
            && sequence("SINGLE_TRIAL_JOURNAL_SETTLED")==sequence("JOURNAL_SETTLED")+1
            && sequence("END")==sequence("SINGLE_TRIAL_JOURNAL_SETTLED")+1,"Guard refusal tail order differs");
        Need(B(errorRow,"DiskFlush") && !B(V17One(rows,"JOURNAL_SETTLED"),"DiskFlush")
            && !B(V17One(rows,"SINGLE_TRIAL_JOURNAL_SETTLED"),"DiskFlush"),"Guard refusal durability differs");
        Need(GuardResolvedPath(T(end,"PendingJournal"),T(V17Data(rows,"JOURNAL_SETTLED"),"Path"))
            && GuardResolvedPath(T(end,"SingleTrialPendingJournal"),T(V17Data(rows,"SINGLE_TRIAL_JOURNAL_SETTLED"),"Path")),
            "Guard refusal journal archive path differs");
        var result=V17Data(rows,"EVENT_RESULT");var recheck=F(result,"PostCodeRecheck");
        Need(N(recheck,"Contract")==1 && N(recheck,"Attempts")==0 && N(recheck,"TemporaryUnavailableObservations")==0
            && N(recheck,"ElapsedMilliseconds")==0 && T(recheck,"Outcome")=="NotStarted"
            && B(recheck,"ReadOnly") && N(recheck,"NativeRequestsRetried")==0,"Guard refusal post work differs");
        DateTimeOffset last=default;
        foreach(var row in rows)
        {
            Need(DateTimeOffset.TryParse(T(row,"At"),CultureInfo.InvariantCulture,DateTimeStyles.None,out var at)
                && at!=default && (last==default || at>=last),"Guard refusal timestamps differ");last=at;
        }
        return true;
    }

    internal static bool IsGuardRejected(ShopResidentNativeOutcome value) => value.SafeToSettle && value.NoEntryConfirmed
        && value.SourceMatched && !value.Entered && !value.Returned && value.LogPurpose==ShopNativeLogPurpose.NativeRequest
        && value.Failure is {Code:"NativeGuardRejected",Scope:"Page",Phase:"NativeGuard",Rva:null};

    internal static bool GuardResolvedPath(string pending,string resolved)
    {
        const string suffix=".pending.json";
        if(!Path.IsPathFullyQualified(pending) || !pending.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))return false;
        string prefix=pending[..^suffix.Length]+".resolved-";
        return resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && resolved.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)
            && resolved.Length==prefix.Length+32+6 && Guid.TryParseExact(resolved.AsSpan(prefix.Length,32),"N",out _);
    }
}
