using MemoryHub;
namespace MemoryHub.Service;

internal static class ShopRefreshReadRetry
{
    private static readonly string[] Fields=["selfId","position","nearbyPlayers","playerShopStates"];
    internal static bool CanRetry(ReadResult result,SourceIdentity expected,SourceIdentity? current)
        => current==expected && result.Source==expected && !result.NoSourceRead
        && result.RetryableFields.Length>0
        && Fields.All(key=>result.Fields.TryGetValue(key,out var value)
            && (value.Fresh(DateTimeOffset.UtcNow) || (result.RetryableFields.Contains(key)
                && value.State=="Unknown" && value.Value is null && value.SampledAt is null)));

    internal static async Task<ReadResult> RunAsync(Func<CancellationToken,ReadResult> read,
        SourceIdentity expected,Func<SourceIdentity?> current,Action<int> retry,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        ReadResult? result=null;
        for(int attempt=0;attempt<3;attempt++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            result=read(deadline.Token);
            if(attempt==2 || !CanRetry(result,expected,current()))return result;
            retry(attempt+2);
            await Task.Delay(50,deadline.Token).ConfigureAwait(false);
        }
        return result!;
    }
}
