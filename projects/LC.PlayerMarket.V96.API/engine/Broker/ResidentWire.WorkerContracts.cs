using System.Text.Json;
using System.Text.Json.Serialization;

namespace LcNativeWorker.V18;

public static class WorkerProtocol
{
    public const string Schema = "LC.PlayerMarket.NativeWorker.V18.1";
    public const int MaximumFrameCharacters = 65536;
    public const int MaximumRequestCount = 10000;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record WorkerSourceIdentity(int ProcessId, long CreationFileTime, string ImagePath,
    string ImageBase, uint PeTimestamp, int ImageSize, string DiskSha256, string SessionKey);

public sealed record WorkerRequest
{
    public string Schema { get; init; } = WorkerProtocol.Schema;
    public string Kind { get; init; } = "execute";
    public string SessionNonce { get; init; } = "";
    public string WorkerId { get; init; } = "";
    public int WorkerProcessId { get; init; }
    public long WorkerCreationFileTime { get; init; }
    public long Sequence { get; init; }
    public string RequestId { get; init; } = "";
    public string RequestNonce { get; init; } = "";
    public WorkerSourceIdentity? Source { get; init; }
    public ulong Hwnd { get; init; }
    public ulong PlayerShopSubsystem { get; init; }
    public ulong CurrentOwnerIdRaw { get; init; }
    public uint TargetOwnerId { get; init; }
    // ViewerBuy / ViewerSell / ReadOnlyPreflight. No trade command exists.
    public string Direction { get; init; } = "";
    public string NativeLogPath { get; init; } = "";
    public bool ForceDispatch { get; init; } = true;
}

public sealed record WorkerFrame
{
    public string Schema { get; init; } = WorkerProtocol.Schema;
    public string Kind { get; init; } = "";
    public string SessionNonce { get; init; } = "";
    public string WorkerId { get; init; } = "";
    public int WorkerProcessId { get; init; }
    public long WorkerCreationFileTime { get; init; }
    public long Sequence { get; init; }
    public string RequestId { get; init; } = "";
    public string RequestNonce { get; init; } = "";
    public WorkerSourceIdentity? Source { get; init; }
    public int? RequestExitCode { get; init; }
    public string NativeLogPath { get; init; } = "";
    public string EndLogSha256 { get; init; } = "";
    public bool RequestEndDurable { get; init; }
    public bool NativeLifecycleSettled { get; init; }
    public bool IdleAck { get; init; }
    // Resource closure candidate only; host must prove original V17 positive no-entry.
    public bool NoEntryCandidate { get; init; }
    public bool WorkerProcessExited { get; init; }
    public bool SourceHashPinReused { get; init; }
    public string SourceFileIdentity { get; init; } = "";
    public long SourceHashComputations { get; init; }
    public double OperationElapsedMilliseconds { get; init; }
    public string? Error { get; init; }
}
