using System.Globalization;

namespace LcNativeWorker.V18;

public sealed class WorkerSession(string sessionNonce, string workerId, int workerPid, long workerCreation)
{
    private long sequence;
    private readonly HashSet<string> requests = new(StringComparer.Ordinal);
    private readonly HashSet<string> nonces = new(StringComparer.Ordinal);
    private WorkerSourceIdentity? bound;
    public bool InFlight { get; private set; }
    public bool Retired { get; private set; }
    public static bool Hex(string? text, int length) => text is not null && text.Length == length && text.All(Uri.IsHexDigit);
    public void Admit(WorkerRequest request)
    {
        try
        {
            Need(!Retired && !InFlight, "WORKER_NOT_IDLE");
            Need(request.Schema == WorkerProtocol.Schema && request.SessionNonce == sessionNonce
                && request.WorkerId == workerId && request.WorkerProcessId == workerPid
                && request.WorkerCreationFileTime == workerCreation, "WORKER_IDENTITY_MISMATCH");
            Need(request.Kind is "execute" or "stop", "WORKER_COMMAND_UNSUPPORTED");
            Need(request.Sequence == sequence + 1 && request.Sequence <= WorkerProtocol.MaximumRequestCount, "WORKER_SEQUENCE_MISMATCH");
            Need(request.RequestId is { Length: > 0 and <= 256 } && Hex(request.RequestNonce, 64)
                && requests.Add(request.RequestId) && nonces.Add(request.RequestNonce), "WORKER_DUPLICATE_OR_INVALID_REQUEST");
            sequence = request.Sequence;
            if (request.Kind == "stop") { Retired = true; return; }
            var source = request.Source;
            Need(source is not null && source.ProcessId > 0 && source.CreationFileTime > 0
                && source.ImagePath is { Length: > 0 and <= 32768 } && source.SessionKey is { Length: > 0 and <= 2048 }
                && source.PeTimestamp == 0x6AE08E04 && source.ImageSize == 0x3F86000
                && source.DiskSha256 == "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC", "WORKER_SOURCE_UNSUPPORTED");
            Need(bound is null || bound == source, "WORKER_SOURCE_CHANGED_RETIRED");
            Need(source!.ImageBase.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(source.ImageBase.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong image)
                && image >= 0x10000 && (image & 4095) == 0, "WORKER_IMAGE_BASE_INVALID");
            Need(request.Hwnd >= 0x10000 && request.PlayerShopSubsystem >= 0x10000 && (request.PlayerShopSubsystem & 7) == 0
                && request.TargetOwnerId <= int.MaxValue && (request.Direction == "ReadOnlyPreflight" || request.TargetOwnerId > 0)
                && request.Direction is "ViewerBuy" or "ViewerSell" or "ReadOnlyPreflight", "WORKER_REQUEST_INVALID");
            bound = source; InFlight = true;
        }
        catch { Retired = true; throw; }
    }
    public void Complete(bool lifecycleSettled)
    {
        Need(InFlight, "WORKER_NO_ACTIVE_REQUEST"); InFlight = false;
        if (!lifecycleSettled) Retired = true;
    }
    public void Retire() => Retired = true;
    private static void Need(bool value, string code) { if (!value) throw new InvalidOperationException(code); }
}

public static class WorkerReceiptPolicy
{
    public static bool Matches(WorkerRequest request, WorkerFrame frame, string kind) =>
        frame.Schema == WorkerProtocol.Schema && frame.Kind == kind && frame.SessionNonce == request.SessionNonce
        && frame.WorkerId == request.WorkerId && frame.WorkerProcessId == request.WorkerProcessId
        && frame.WorkerCreationFileTime == request.WorkerCreationFileTime && frame.Sequence == request.Sequence
        && frame.RequestId == request.RequestId && frame.RequestNonce == request.RequestNonce && frame.Source == request.Source
        && string.Equals(frame.NativeLogPath, request.NativeLogPath, StringComparison.OrdinalIgnoreCase)
        && !frame.WorkerProcessExited;
    public static bool IdlePair(WorkerRequest request, WorkerFrame result, WorkerFrame idle) =>
        Matches(request, result, "result") && Matches(request, idle, "idle")
        && result.RequestExitCode == idle.RequestExitCode
        && ((result.RequestExitCode == 0 && !result.NoEntryCandidate && !idle.NoEntryCandidate)
            || (result.RequestExitCode == 1 && result.NoEntryCandidate && idle.NoEntryCandidate))
        && result.RequestEndDurable && idle.RequestEndDurable
        && result.NativeLifecycleSettled && idle.NativeLifecycleSettled && !result.IdleAck && idle.IdleAck
        && WorkerSession.Hex(result.EndLogSha256, 64) && result.EndLogSha256 == idle.EndLogSha256
        && result.Error is null && idle.Error is null;
}
