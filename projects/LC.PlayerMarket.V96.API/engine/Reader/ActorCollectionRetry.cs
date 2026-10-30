namespace MemoryHub.Reader;

internal sealed record ActorRetryEvent(string Phase,int Attempt,string Reason,double ElapsedMs,double RemainingMs);

// A retry always invokes a fresh whole capture; failed values are never published.
internal static class ActorCollectionRetry
{
    internal static void Run(Action capture,Action verifySource,Func<int> checkpoint,Action<int> rollback,
        Func<double> elapsedMilliseconds,Func<double> remainingMilliseconds,Action<ActorRetryEvent> log)
    {
        int mark=checkpoint();
        try{capture();return;}
        catch(ProbeFailure error)when(error.Code=="ACTOR_COLLECTION_CHANGED"&&error.NativeError==0)
        {
            rollback(mark);
            double firstElapsed=elapsedMilliseconds(),remaining=remainingMilliseconds();
            // Reserve the cost of another whole capture plus final source closure.
            // A 200 ms wall-clock veto rejected recoverable reads even with >1 s left.
            double required=Math.Max(250,firstElapsed+150);
            if(!double.IsFinite(firstElapsed)||!double.IsFinite(remaining)||remaining<required)
            {
                log(new("ACTOR_COLLECTION_RETRY_SKIPPED",1,"InsufficientEstimatedCaptureAndClosureBudget",firstElapsed,remaining));
                throw;
            }
            // Rebind neither process nor character: the original fence must still hold.
            verifySource();
            remaining=remainingMilliseconds();
            double retryElapsed=elapsedMilliseconds();
            if(!double.IsFinite(remaining)||remaining<required)
            {
                log(new("ACTOR_COLLECTION_RETRY_SKIPPED",1,"BudgetAfterSourceValidation",retryElapsed,remaining));
                throw;
            }
            log(new("ACTOR_COLLECTION_RETRY",2,error.Code,elapsedMilliseconds(),remaining));
        }
        // CaptureOnce creates all dynamic proofs, item lists and timestamps afresh.
        mark=checkpoint();
        try{capture();}
        catch(ProbeFailure error)when(error.Code=="ACTOR_COLLECTION_CHANGED"&&error.NativeError==0)
        {
            rollback(mark);
            log(new("ACTOR_COLLECTION_RETRY_EXHAUSTED",2,error.Code,elapsedMilliseconds(),remainingMilliseconds()));
            throw;
        }
        log(new("ACTOR_COLLECTION_RECOVERED",2,"CompleteFreshCapture",elapsedMilliseconds(),remainingMilliseconds()));
    }
}
