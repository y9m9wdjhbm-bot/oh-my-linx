namespace MemoryHub.Service;

public sealed partial class ShopBroker
{
    // The helper uses shared native/journal resources. Keep the latch across
    // broker instances in this application, including a failed broker shutdown.
    // A new application cannot infer anything about a historical helper/intent.
    private static readonly ShopNativeHelperFlight nativeHelperFlight = new();

    private IShopNativeHelperProcess StartObservedNativeHelper(System.Diagnostics.ProcessStartInfo start,
        string purpose, string? probeId, string logPath) => ShopNativeHelperProcess.Start(start,
            stage => Audit("SHOP_NATIVE_HELPER_STAGE", "原 helper 程序生命週期觀測。",
                new { Purpose = purpose, ProbeId = probeId, NativeLog = logPath, Process = stage }));

    internal static bool NativeWorkSettled(bool otherWorkSettled, ShopNativeHelperFlight flight) =>
        otherWorkSettled && !flight.IsActive;

    private static void RequireNativeHelperAvailable()
    {
        try { nativeHelperFlight.RequireAvailable(); }
        catch (ShopNativeHelperBusyException error)
        { throw new ShopRequestFailure(ShopFailure.Global("NativeTransportUnavailable", error.Message)); }
    }

    internal static ShopFailure NativeInvocationFailure(Exception error) => error is
        ShopNativeHelperWaitTimeoutException or ShopNativeHelperExitUnconfirmedException or ShopNativeHelperBusyException or ShopResidentWorkerUncertainException
        // Preserve the unknown native outcome, but stop the entire forced batch:
        // ForceDispatch can ignore old Session failures; it must not ignore a
        // helper whose actual process lifetime has not been confirmed complete.
        ? ShopFailure.Unknown(error.Message) with { Scope = "Global" }
        : ShopFailure.Unknown(error.Message);
}
