namespace MemoryHub.Reader;

/// <summary>Validates only the already completed capture. Missing identity never authorizes dispatch.</summary>
internal static class ShopDispatchSourceValidation
{
    private static readonly string[] RequiredFields =
        ["selfId", "position", "nearbyPlayers", "playerShopStates", "personalShopListing"];

    internal static void RequireCurrent(ReadResult current, SourceIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(expected);
        if (current.Source is not null && current.Source != expected)
            throw new InvalidOperationException("SHOP_SOURCE_CHANGED：同輪採樣無法核對原 LC 完整來源；不重試、不送出。");
        if (current.NoSourceRead)
            throw new InvalidOperationException("SHOP_DISPATCH_SOURCE_UNAVAILABLE：本輪未完成來源讀取；不重試、不送出。");
        if (current.ReadFailureCode is not null || current.ReadFailureNativeError is not null)
        {
            string evidence = $"ReadFailureCode={current.ReadFailureCode ?? "Unknown"}；NativeError={current.ReadFailureNativeError?.ToString() ?? "Unknown"}；{current.Detail}";
            bool timeout = current.Source is null && current.State == "Unknown"
                && current.ReadFailureCode == "TIME_LIMIT" && current.ReadFailureNativeError == 0
                && RequiredFields.All(key => current.Fields.TryGetValue(key, out var field)
                    && field.State == "Unknown" && field.Value is null && field.SampledAt is null);
            if (timeout)
                throw new ShopDispatchReadTimeoutException(expected, 0, 0,
                    "SHOP_DISPATCH_READ_TIMEOUT：本輪唯讀採樣已結束並達期限，完整原來源尚未核對；本頁尚未送出。" + evidence);
            throw new InvalidOperationException(
                "SHOP_DISPATCH_READ_FAILED：本輪唯讀採樣失敗，尚未核對完整原來源；不重試、不送出。" + evidence);
        }
        if (current.Source is null)
            throw new InvalidOperationException("SHOP_DISPATCH_SOURCE_UNAVAILABLE：本輪未取得完整原來源；不重試、不送出。");
    }
}
