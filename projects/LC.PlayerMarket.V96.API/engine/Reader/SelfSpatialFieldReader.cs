namespace MemoryHub.Reader;

/// <summary>
/// Reads position only. Closes the short MyPlayer discovery first, then retains
/// the selected self's ownership chain through the final source fence.
/// </summary>
internal static class SelfSpatialFieldReader
{
    internal static int Run(ReadSession memory,Journal journal,Budget budget)
    {
        if(!journal.Enabled("position"))return 0;

        // Self() closes actor discovery before BindSelf; no value or closure is
        // published on ACTOR_DISCOVERY_CHANGED. Retain that transaction boundary
        // explicitly so a failed graph can never leak into the second attempt.
        int checkpoint=memory.MetadataCheckpoint(),attempts=0;
        var sourceClosure=journal.SourceClosure;
        var priorSelfId=journal.SelfId;
        var priorSampleAt=journal.SampleAt;
        ProbeFailure? retryReason=null;
        try
        {
            var found=SelfSpatialDiscoveryRetry.Run(
                ()=>
                {
                    attempts++;
                    var fresh=new CurrentTypedGraph(memory,journal,budget,"SELF_SPATIAL");
                    return(Graph:fresh,Self:fresh.Self());
                },
                (ProbeFailure error)=>(error.Code,error.NativeError),
                error=>
                {
                    retryReason=error;
                    if(!ReferenceEquals(journal.SourceClosure,sourceClosure)||journal.SelfId!=priorSelfId||journal.SampleAt!=priorSampleAt)
                        throw new ProbeFailure("SELF_SPATIAL_RETRY_STATE_CHANGED","失敗的自身查找已改變共用採樣狀態，不能重試。");
                    memory.RollbackMetadataCandidate(checkpoint);
                    memory.Verify();sourceClosure?.Invoke();budget.Check();
                    journal.Write("SELF_SPATIAL_DISCOVERY_RETRY",new{Attempt=2,Reason=error.Code,Detail=error.Message,
                        RemainingMs=budget.RemainingMilliseconds,RequestedBytes=memory.RequestedBytes,SameReadSessionAndBudget=true});
                });
            var graph=found.Graph;
            var self=found.Self;
            // Transform has no fixed ProfileSymbols anchor. Component() still
            // validates its complete current RTTI, unique membership and owner.
            ulong transform=graph.Component(self.Actor,".?AVTransformComponent@lineage@@");
            var positionAt=DateTimeOffset.UtcNow;
            byte[] coordinates=graph.Track(transform+0x40,8);
            int x=CurrentTypedGraph.I32(coordinates);
            int y=CurrentTypedGraph.I32(coordinates,4);
            graph.Need(x is >0 and <=1_000_000&&y is >0 and <=1_000_000,
                "COORDINATE_RANGE","自身座標值超出已驗範圍。");

            graph.Close();
            graph.AttachSourceClosure();
            journal.Set("position",new{X=x,Y=y},positionAt,
                "SelectedSelfTransform;CurrentTypesAndOwnerGraphClosed;ValueABStable");
            if(retryReason is not null)
                journal.Write("SELF_SPATIAL_DISCOVERY_RECOVERED",new{Attempts=attempts,Reason=retryReason.Code,
                    FreshGraphAndValueClosed=true,FinalSourceFenceStillRequired=true,
                    RemainingMs=budget.RemainingMilliseconds,RequestedBytes=memory.RequestedBytes});
            return 1;
        }
        catch(ProbeFailure error)
        {
            if(retryReason is not null)
                journal.Write("SELF_SPATIAL_DISCOVERY_FAILED",new{Attempts=attempts,InitialReason=retryReason.Code,
                    Reason=error.Code,Detail=error.Message,error.NativeError,NoFurtherRetry=true,
                    RemainingMs=budget.RemainingMilliseconds,RequestedBytes=memory.RequestedBytes});
            throw;
        }
    }
}
