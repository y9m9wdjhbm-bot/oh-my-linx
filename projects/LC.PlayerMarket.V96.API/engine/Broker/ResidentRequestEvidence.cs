using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LcNativeWorker.V18;

namespace MemoryHub.Service;

internal sealed record ShopResidentRequestEvidence(WorkerRequest Request, WorkerFrame Result, WorkerFrame Idle,
    int VerifiedServerProcessId, long VerifiedServerCreationFileTime, string SourceFileIdentity)
{
    private static readonly JsonSerializerOptions NativeBindingJson = new()
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal string ReadAndValidate(SourceIdentity expected, ulong owner)
    {
        if (!WorkerReceiptPolicy.IdlePair(Request, Result, Idle)
            || Request.Kind != "execute" || Request.Direction is not (ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell)
            || Request.Source != ToWire(expected) || Request.TargetOwnerId != owner
            || Request.WorkerProcessId != VerifiedServerProcessId || Request.WorkerCreationFileTime != VerifiedServerCreationFileTime
            || Result.SourceHashComputations != 1 || Idle.SourceHashComputations != 1
            || string.IsNullOrWhiteSpace(SourceFileIdentity) || Result.SourceFileIdentity != SourceFileIdentity
            || Idle.SourceFileIdentity != SourceFileIdentity || Result.SourceHashPinReused != Idle.SourceHashPinReused
            || !double.IsFinite(Result.OperationElapsedMilliseconds) || Result.OperationElapsedMilliseconds < 0
            || Result.OperationElapsedMilliseconds != Idle.OperationElapsedMilliseconds
            || Request.Sequence <= 0 || !WorkerSession.Hex(Request.RequestNonce, 64))
            throw new InvalidDataException("RESIDENT_REQUEST_RECEIPT_BINDING_FAILED");
        using var file = new FileStream(Request.NativeLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("RESIDENT_NATIVE_LOG_SIZE");
        using var copy = new MemoryStream(); file.CopyTo(copy); byte[] bytes = copy.ToArray();
        if (bytes.Length > 4 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != Result.EndLogSha256)
            throw new InvalidDataException("RESIDENT_END_DIGEST_MISMATCH");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        var rows = ShopResidentB47CContract.Rows(text);
        var start = rows.Single(r => ShopResidentB47CContract.T(r, "Kind") == "START").GetProperty("Data");
        var endRow = rows.Single(r => ShopResidentB47CContract.T(r, "Kind") == "END");
        var end = endRow.GetProperty("Data");
        if (!endRow.GetProperty("DiskFlush").GetBoolean()
            || start.GetProperty("ManagedProgramVersion").GetString() != "18.0.0"
            || end.GetProperty("ManagedProgramVersion").GetString() != "18.0.0"
            || start.GetProperty("ResidentBinding").Deserialize<WorkerRequest>(NativeBindingJson) != Request
            || end.GetProperty("ResidentBinding").Deserialize<WorkerRequest>(NativeBindingJson) != Request
            || end.GetProperty("ExitCode").GetInt32() != Result.RequestExitCode)
            throw new InvalidDataException("RESIDENT_NATIVE_LOG_REQUEST_BINDING_FAILED");
        return text;
    }
    internal static WorkerSourceIdentity ToWire(SourceIdentity source) => new(source.ProcessId, source.CreationFileTime,
        source.ImagePath, source.ImageBase, source.PeTimestamp, checked((int)source.ImageSize), source.DiskSha256, source.SessionKey);
}

internal sealed partial record ShopResidentNativeOutcome
{
    internal static ShopNativeOutcome ParseResidentRequest(ShopResidentRequestEvidence evidence, SourceIdentity source, ulong image, ulong owner)
    {
        try
        {
            string text = evidence.ReadAndValidate(source, owner);
            // This is an explicit resident REQUEST outcome, not a fabricated
            // process exit. Legacy oneshot Parse/exit validation is unchanged.
            var result = ParseRequestCore(text, source, image, owner, evidence.Result.RequestExitCode!.Value,
                ShopNativeLogPurpose.NativeRequest);
            bool noEntry = evidence.Result.NoEntryCandidate;
            if (noEntry && (evidence.Result.RequestExitCode != 1 || !result.SafeToSettle || !result.NoEntryConfirmed
                    || !result.SourceMatched || result.Entered || result.Returned)
                || !noEntry && (evidence.Result.RequestExitCode != 0 || !result.Entered || !result.Returned))
                throw new InvalidDataException("RESIDENT_NO_ENTRY_CANDIDATE_DID_NOT_PASS_FULL_NATIVE_PROOF");
            return new(result.Entered, result.Returned, result.SafeToSettle,
                "ResidentRequestEvidence18; WorkerProcessExitClaimed=false; " + result.Detail, result.Failure)
            { NoEntryConfirmed = result.NoEntryConfirmed, SourceMatched = result.SourceMatched, LogPurpose = result.LogPurpose };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            return new(false, false, false, "Resident request evidence unconfirmed: " + error.Message,
                ShopFailure.Unknown(error.Message) with { Scope = "Global" });
        }
    }
}
