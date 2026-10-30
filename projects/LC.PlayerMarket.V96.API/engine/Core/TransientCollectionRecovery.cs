namespace MemoryHub;

internal static class TransientCollectionRecovery
{
    internal static bool Eligible(string key,ReadResult result,FieldReading attempted)
        => key is "actors" or "actorNames" or "actorPositions" or "nearbyMonsters" or "nearbyNpcs" or
            "nearbyPlayers" or "nearbyUnknown" or "playerShopStates"
        && result.Source is not null && !result.NoSourceRead && result.State is "Reading" or "Unknown"
        && result.RetryableFields.Contains(key,StringComparer.Ordinal)
        && attempted.State=="Unknown" && attempted.Value is null
        && attempted.Detail.StartsWith("ACTOR_COLLECTION_CHANGED：",StringComparison.Ordinal);

    internal static bool TryRetain(string key,ReadResult result,FieldReading attempted,FieldReading? previous,
        DateTimeOffset now,out FieldReading retained)
    {
        retained=attempted;
        if(!Eligible(key,result,attempted)||previous is null||previous.Value is null||!previous.Fresh(now))return false;
        // The field and every nested item's original timestamps/value are immutable.
        // Recovery cannot make an old observation younger or extend its original TTL.
        retained=previous with {Detail="本輪集合暫時變動；保留尚在原有效期限內的上次成功資料，正在重試。原因："+attempted.Detail};
        return true;
    }
}
