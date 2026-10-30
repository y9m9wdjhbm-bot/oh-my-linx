namespace MemoryHub;

/// <summary>
/// Consumer classification of a receipt already verified by the native parser.
/// This DTO predicate neither verifies native LOG evidence nor authorizes dispatch.
/// WorkSettled describes the entire scan and must not be substituted for the
/// parser's per-request no-entry and cleanup proof.
/// CurrentSourceSessionKey is a background observation that may temporarily be
/// null; it cannot invalidate the already bound request receipt. The parser and
/// independent evidence verifier still bind the complete original source.
/// </summary>
public static class ShopNotEnteredPageRules
{
    public const string FailureCode = "NativeGuardRejected";
    public const string FailurePhase = "NativeGuard";

    public static bool HasNotEnteredClaim(ShopCommandResponse response) => response is not null
        && (response.FailureCode == FailureCode || response.FailurePhase == FailurePhase);

    public static bool IsNotEnteredResponse(ShopCommandResponse response) => response is not null
        && response.Schema == ShopCommands.Schema
        && response.State == "NativeRejected" && response.FailureCode == FailureCode
        && response.FailureScope == "Page" && response.FailurePhase == FailurePhase
        && response.FailureRva is null
        && !string.IsNullOrWhiteSpace(response.RequestId) && response.RequestId.Length <= 256
        && response.At != default && !string.IsNullOrWhiteSpace(response.Detail)
        && !response.NativeEntered && !response.NativeReturned && !response.ResponseVerified
        && !response.EmptyPageObserved && response.EmptyPageObservation is null && !response.SessionBlocked;
}
