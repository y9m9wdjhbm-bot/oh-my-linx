using MemoryHub;
namespace LcPlayerMarket;

internal sealed record LiveSpeedSampleDecision(bool StopAtPausedBoundary, ShopCommandRequest? PauseRequest);
internal static class LiveSpeedSamplePolicy
{
    internal const int CompletedPageThreshold = 24;
    internal static LiveSpeedSampleDecision Decide(int threshold, ShopCommandRequest owned, ShopCommandResponse response, bool pauseRequested)
    {
        if (threshold is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (owned.Command != ShopCommands.ScanVisible || !owned.ForceDispatch || response.Scan.JobId != owned.RequestId || response.SessionBlocked)
            throw new InvalidDataException("SPEED_SAMPLE_SCAN_OWNERSHIP_OR_BLOCKED");
        if (response.Scan.Paused)
        {
            if (!pauseRequested) throw new InvalidDataException("SPEED_SAMPLE_UNOWNED_PAUSE");
            return new(true, null);
        }
        if (pauseRequested || response.Scan.Completed < threshold) return new(false, null);
        return new(false, new ShopCommandRequest
        {
            Command = ShopCommands.PauseScan, ForceDispatch = true,
            ExpectedSourceSessionKey = owned.ExpectedSourceSessionKey,
            ExpectedScanJobId = owned.RequestId, TargetSnapshotId = owned.TargetSnapshotId,
            ExpectedScanPlanId = response.SavedScan?.PlanId
        });
    }
    internal static OfflineCheck[] RunSelfTest()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action body) { try { body(); checks.Add(new(name, true, "Pure bounded scan/pause policy; no LC/native/input.")); } catch(Exception e) { checks.Add(new(name,false,e.ToString())); } }
        static void Need(bool ok) { if (!ok) throw new InvalidOperationException("Speed sample oracle failed."); }
        var owned = new ShopCommandRequest { RequestId="speed-owned", Command=ShopCommands.ScanVisible, ForceDispatch=true, ExpectedSourceSessionKey="source", TargetSnapshotId="saved-list" };
        ShopCommandResponse State(int done, bool paused=false) => new() { Scan=new() { JobId=owned.RequestId, Running=true, Completed=done, Paused=paused } };
        Check("speed_sample_before_threshold_never_pauses",()=>Need(Decide(24,owned,State(23),false)==new LiveSpeedSampleDecision(false,null)));
        Check("speed_sample_threshold_only_requests_owned_pause",()=> { var d=Decide(24,owned,State(24),false); Need(!d.StopAtPausedBoundary && d.PauseRequest is {Command:ShopCommands.PauseScan,ForceDispatch:true} p && p.ExpectedScanJobId==owned.RequestId && p.TargetSnapshotId==owned.TargetSnapshotId && p.ExpectedSourceSessionKey==owned.ExpectedSourceSessionKey); });
        Check("speed_sample_pending_pause_is_not_a_stopped_native",()=>Need(Decide(24,owned,State(25),true)==new LiveSpeedSampleDecision(false,null)));
        Check("speed_sample_stop_requires_actual_owned_paused_boundary",()=>Need(Decide(24,owned,State(25,true),true)==new LiveSpeedSampleDecision(true,null)));
        foreach(string fault in new[]{"wrong_job","blocked","unowned_pause","invalid_threshold"}) Check("speed_sample_refuses_"+fault,()=>
        {
            bool refused=false;try{Decide(fault=="invalid_threshold"?0:24,owned,fault switch {"wrong_job"=>State(24) with{Scan=new(){JobId="other",Running=true,Completed=24}},"blocked"=>State(24) with{SessionBlocked=true},"unowned_pause"=>State(24,true),_=>State(24)},false);}catch(Exception e)when(e is InvalidDataException or ArgumentOutOfRangeException){refused=true;}Need(refused);
        });
        return checks.ToArray();
    }
}
