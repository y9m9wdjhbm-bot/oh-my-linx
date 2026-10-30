using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class ShopRefreshReadRetrySelfTest
{
    internal static IEnumerable<OfflineCheck> Run()
    {
        var source=new SourceIdentity(1,2,"LC.exe","0x10000",3,4,"sha","session");
        ReadResult Capture(bool transient)=>new(source,new(){
            ["selfId"]=FieldReading.Observed(1,DateTimeOffset.UtcNow),
            ["position"]=FieldReading.Observed(new{x=10,y=10},DateTimeOffset.UtcNow),
            ["nearbyPlayers"]=transient?FieldReading.Unknown("ACTOR_COLLECTION_CHANGED"):FieldReading.Observed(new[]{"fresh"},DateTimeOffset.UtcNow),
            ["playerShopStates"]=transient?FieldReading.Unknown("ACTOR_COLLECTION_CHANGED"):FieldReading.Observed(new[]{"fresh"},DateTimeOffset.UtcNow)},"Reading","")
            {RetryableFields=transient?["nearbyPlayers","playerShopStates"]:[]};
        yield return new("refresh_same_source_transient_can_retry",ShopRefreshReadRetry.CanRetry(Capture(true),source,source),"");
        yield return new("refresh_changed_source_never_retries",!ShopRefreshReadRetry.CanRetry(Capture(true),source,source with {SessionKey="other"}),"");
        yield return new("refresh_missing_background_source_never_retries",!ShopRefreshReadRetry.CanRetry(Capture(true),source,null),"");
        var invalid=Capture(true);invalid.Fields["position"]=FieldReading.Unknown("RPM_INCOMPLETE");
        yield return new("refresh_unrelated_failure_never_retries",!ShopRefreshReadRetry.CanRetry(invalid,source,source),"");
        int attempts=0,notices=0;
        var result=ShopRefreshReadRetry.RunAsync(_=>Capture(++attempts==1),source,()=>source,_=>notices++,CancellationToken.None).GetAwaiter().GetResult();
        yield return new("refresh_recovery_uses_second_complete_capture",attempts==2&&notices==1&&result.Fields["nearbyPlayers"].Fresh(DateTimeOffset.UtcNow),"");
        attempts=0;
        result=ShopRefreshReadRetry.RunAsync(_=>{attempts++;return Capture(true);},source,()=>source,_=>{},CancellationToken.None).GetAwaiter().GetResult();
        yield return new("refresh_retry_exhausted_keeps_unknown_without_cached_values",attempts==3&&result.Fields["nearbyPlayers"].Value is null,"");
    }
}
