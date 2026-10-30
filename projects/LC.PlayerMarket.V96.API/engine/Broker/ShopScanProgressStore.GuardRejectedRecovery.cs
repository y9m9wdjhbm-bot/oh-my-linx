using System.Text.Json;

namespace MemoryHub.Service;

internal sealed record NativeGuardRejectedRecoveryEvidence(SourceIdentity Source,string PlanId,string RequestId,
    ulong OwnerId,string OwnerIdentity,string Direction,int OriginalSequence,string OriginalEventSha256,
    string NativeSha256,string SessionSha256,string RequestSha256,string ResponseSha256,
    string ForceArchiveSha256,string SingleArchiveSha256,string InputManifestSha256);

internal sealed partial class ShopScanProgressStore
{
    internal void NativeGuardRejected(string requestId,string detail)
    {
        lock(sync)
        {
            if(!requests.ContainsKey(requestId))return;
            AppendKnown(requestId,"NativeGuardRejected",detail);
            try { if(unresolved.TryGetValue(requestId,out var intent))SettleIndex(intent,false); }
            catch(Exception failure){error="已確認未進入的結清索引保存失敗；保留鎖："+failure.Message;throw;}
        }
    }

    internal void RecoverGuardRejected(NativeGuardRejectedRecoveryEvidence proof,string detail)
    {
        lock(sync)
        {
            RequireHealthy();
            if(plan is null || !requests.TryGetValue(proof.RequestId,out var key))throw Failure("Guard recovery request missing.");
            ValidateGuardRecovery(proof,key);
            var state=pages[key];
            if(state.Settled)
            {
                var matches=Directory.EnumerateFiles(EventsDirectory(plan.PlanId),"*.json")
                    .Select(file=>Read<ScanProgressEvent>(file,64*1024))
                    .Where(item=>item.RequestId==proof.RequestId && item.Kind=="NativeGuardRejectedRecovered").ToArray();
                if(matches.Length!=1 || matches[0].GuardRejectedRecovery!=proof || state.State!="NativeGuardRejected"
                    || state.SettledReturned)throw Failure("Guard recovery committed event differs.");
                if(unresolved.TryGetValue(proof.RequestId,out var prior))SettleIndex(prior,false);
                return;
            }
            if(sequence>=MaximumEvents)throw Failure("Guard recovery event limit reached.");
            var item=new ScanProgressEvent{Sequence=sequence+1,PlanId=plan.PlanId,Kind="NativeGuardRejectedRecovered",
                RequestId=proof.RequestId,OwnerId=key.OwnerId,OwnerIdentity=key.OwnerIdentity,Direction=key.Direction,
                At=NextTimestamp(updatedAt),Detail=detail,GuardRejectedRecovery=proof};
            ValidateEvent(item);
            WriteNew(Path.Combine(EventsDirectory(plan.PlanId),EventFileName(item.Sequence)),item);
            Apply(item);
            if(unresolved.TryGetValue(proof.RequestId,out var intent))SettleIndex(intent,false);
        }
    }

    private void ValidateGuardRecovery(NativeGuardRejectedRecoveryEvidence proof,SavedScanPage key)
    {
        if(plan is null || proof.Source!=plan.Source || proof.PlanId!=plan.PlanId || proof.OwnerId!=key.OwnerId
            || proof.OwnerIdentity!=key.OwnerIdentity || proof.Direction!=key.Direction || proof.OriginalSequence<1)
            throw Failure("Guard recovery source/page differs.");
        foreach(string value in new[]{proof.OriginalEventSha256,proof.NativeSha256,proof.SessionSha256,proof.RequestSha256,
            proof.ResponseSha256,proof.ForceArchiveSha256,proof.SingleArchiveSha256,proof.InputManifestSha256})
            if(value is not {Length:64} || !value.All(Uri.IsHexDigit))throw Failure("Guard recovery evidence hash missing.");
        string file=Path.Combine(EventsDirectory(plan.PlanId),EventFileName(proof.OriginalSequence));
        var original=Read<ScanProgressEvent>(file,64*1024);
        if(ShopNativeGuardRejectedReconciliation.Hash(file)!=proof.OriginalEventSha256
            || original.PlanId!=plan.PlanId || original.RequestId!=proof.RequestId || original.OwnerId!=key.OwnerId
            || original.OwnerIdentity!=key.OwnerIdentity || original.Direction!=key.Direction || original.Kind!="Result"
            || !original.SessionBlocked || original.NativeEntered || original.NativeReturned || original.ResponseVerified)
            throw Failure("Guard recovery original failed event differs.");
    }
}
