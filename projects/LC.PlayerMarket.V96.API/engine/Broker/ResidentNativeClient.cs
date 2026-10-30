using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LcNativeWorker.V18;
using MemoryHub.Reader;

namespace MemoryHub.Service;

internal sealed class ShopResidentWorkerUncertainException(string detail, Exception? inner = null) : IOException(detail, inner);
internal sealed record ShopResidentWarmReceipt(bool HelloVerified, double StartupMilliseconds,
    int WorkerPid, long WorkerCreationFileTime, bool SourceHashPinPending);

internal sealed class ShopResidentNativeClient(string directory, string logRoot, Action<string, object>? report = null)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string pipeName = "Lineage.PlayerMarket.NativeV18." + Guid.NewGuid().ToString("N");
    private Process? process;
    private Task? actualExit;
    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private WorkerFrame? hello;
    private SourceIdentity? bound;
    private long sequence, workerCreation;
    private int workerPid;
    private bool started, poisoned, disposed;
    private bool actualExitConfirmed;
    private string? fileIdentity;
    private IDisposable? packagePin;
    internal bool Poisoned => poisoned;
    internal bool ActualWorkerExitConfirmed => process is null || actualExitConfirmed;
    internal Task WaitForActualWorkerExitAsync() => actualExit ?? Task.CompletedTask;
    internal void RetireAfterHostTimeout() => Poison(new TimeoutException("HOST_RESIDENT_WAIT_TIMED_OUT_NO_REPLAY"));

    internal async Task<ShopResidentWarmReceipt> WarmAsync(SourceIdentity expected)
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var clock = Stopwatch.StartNew(); await EnsureStartedAsync(expected, deadline.Token).ConfigureAwait(false);
            return new(true, clock.Elapsed.TotalMilliseconds, workerPid, workerCreation, sequence == 0);
        }
        catch (Exception error) { Poison(error); throw new ShopResidentWorkerUncertainException("RESIDENT_WORKER_WARM_FAILED", error); }
        finally { serial.Release(); }
    }

    internal async Task<ShopNativeOutcome> ExecuteAsync(ShopActionSourceBinding binding, ulong hwnd, ulong owner,
        string direction, string nativeLog, string requestId, bool forceDispatch)
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await EnsureStartedAsync(binding.Source, deadline.Token).ConfigureAwait(false);
            if (File.Exists(nativeLog) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(nativeLog)), Path.GetFullPath(logRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RESIDENT_FRESH_REQUEST_LOG_REQUIRED");
            var request = Request("execute", requestId) with
            {
                Source = ShopResidentRequestEvidence.ToWire(binding.Source), Hwnd = hwnd,
                PlayerShopSubsystem = binding.PlayerShopSubsystem, CurrentOwnerIdRaw = binding.CurrentOwnerIdRaw,
                TargetOwnerId = checked((uint)owner), Direction = direction, NativeLogPath = Path.GetFullPath(nativeLog), ForceDispatch = forceDispatch
            };
            report?.Invoke("RESIDENT_REQUEST_SEND", new { request.RequestId, request.Sequence, request.RequestNonce,
                request.Source, request.NativeLogPath, WorkerProcessId = workerPid, WorkerCreation = workerCreation });
            await SendAsync(request, deadline.Token).ConfigureAwait(false);
            var result = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (!WorkerReceiptPolicy.Matches(request, result, "result")) throw new InvalidDataException("RESIDENT_RESULT_IDENTITY_OR_ORDER");
            var idle = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
            VerifyPeer();
            if (!WorkerReceiptPolicy.IdlePair(request, result, idle)) throw new InvalidDataException("RESIDENT_IDLE_PAIR_NOT_CLOSED");
            fileIdentity ??= result.SourceFileIdentity;
            var evidence = new ShopResidentRequestEvidence(request, result, idle, workerPid, workerCreation, fileIdentity);
            var outcome = ShopResidentNativeOutcome.ParseResidentRequest(evidence, binding.Source,
                ShopRuntimeContext.ImageBase(binding.Source), owner);
            report?.Invoke("RESIDENT_REQUEST_CLOSED", new { request.RequestId, request.Sequence, outcome.SafeToSettle,
                Result = result, Idle = idle, WorkerProcessExited = false, NativeLogSha256 = result.EndLogSha256 });
            if (!outcome.SafeToSettle) Poison(new InvalidDataException(outcome.Detail));
            return outcome;
        }
        catch (Exception error)
        {
            Poison(error);
            throw new ShopResidentWorkerUncertainException("RESIDENT_REQUEST_UNKNOWN_NO_REPLAY: " + error.Message, error);
        }
        finally { serial.Release(); }
    }

    private WorkerRequest Request(string kind, string id) => new()
    {
        Kind = kind, SessionNonce = nonce, WorkerId = hello!.WorkerId, WorkerProcessId = workerPid,
        WorkerCreationFileTime = workerCreation, Sequence = checked(++sequence), RequestId = id,
        RequestNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
    };
    private async Task EnsureStartedAsync(SourceIdentity expected, CancellationToken token)
    {
        if (poisoned || disposed) throw new InvalidOperationException("RESIDENT_SESSION_RETIRED_NO_REPLAY");
        if (bound is not null && bound != expected) throw new InvalidOperationException("RESIDENT_BOUND_SOURCE_CHANGED");
        bound ??= expected;
        if (started) { VerifyPeer(); return; }
        started = true; packagePin = ShopResidentWorkerPackage.Pin(directory);
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(directory, ShopResidentWorkerPackage.WorkerFile))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (string arg in new[] { "--resident", pipeName, nonce, parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFullPath(logRoot) })
            start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("RESIDENT_PROCESS_START_FAILED");
        _ = process.Handle; workerPid = process.Id; workerCreation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        actualExit = ObserveActualExitAsync(process);
        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(token).ConfigureAwait(false); VerifyPeer();
        reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        hello = await ReceiveAsync(token).ConfigureAwait(false);
        RequireHello(hello, nonce, workerPid, workerCreation);
        report?.Invoke("RESIDENT_WORKER_HELLO", new { workerPid, workerCreation, hello.WorkerId,
            HelloOnly = true, SourceHashPinPending = true, NativeRequests = 0, WorkerProcessExited = false });
    }
    private void VerifyPeer()
    {
        if (process is null || process.HasExited || process.Id != workerPid
            || process.StartTime.ToUniversalTime().ToFileTimeUtc() != workerCreation || pipe is null || !pipe.IsConnected
            || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != workerPid)
            throw new InvalidDataException("RESIDENT_OS_PIPE_SERVER_IDENTITY_MISMATCH");
    }
    private Task SendAsync(WorkerRequest request, CancellationToken token) =>
        writer!.WriteLineAsync(JsonSerializer.Serialize(request, WorkerProtocol.Json).AsMemory(), token);
    private Task<WorkerFrame> ReceiveAsync(CancellationToken token) => ReadFrameAsync(reader!, token);
    internal static async Task<WorkerFrame> ReadFrameAsync(StreamReader input, CancellationToken token)
    {
        var line = new StringBuilder(); var one = new char[1];
        while (await input.ReadAsync(one.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n')
            {
                using var doc = JsonDocument.Parse(line.ToString().TrimEnd('\r'));
                if (!Unique(doc.RootElement)) throw new InvalidDataException("RESIDENT_DUPLICATE_FRAME_PROPERTY");
                return doc.RootElement.Deserialize<WorkerFrame>(WorkerProtocol.Json) ?? throw new InvalidDataException("RESIDENT_EMPTY_FRAME");
            }
            if (line.Length >= WorkerProtocol.MaximumFrameCharacters) throw new InvalidDataException("RESIDENT_FRAME_TOO_LARGE");
            line.Append(one[0]);
        }
        throw new EndOfStreamException("RESIDENT_PIPE_CLOSED_OR_PARTIAL_FRAME_NO_REPLAY");
    }
    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in value.EnumerateObject()) if (!names.Add(p.Name) || !Unique(p.Value)) return false; }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (!Unique(item)) return false;
        return true;
    }
    internal static void RequireHello(WorkerFrame hello, string nonce, int workerPid, long workerCreation)
    {
        if (hello.Schema != WorkerProtocol.Schema || hello.Kind != "hello" || hello.SessionNonce != nonce
            || !WorkerSession.Hex(hello.WorkerId, 32) || hello.WorkerProcessId != workerPid || hello.WorkerCreationFileTime != workerCreation
            || hello.Sequence != 0 || hello.RequestId != "" || hello.RequestNonce != "" || hello.Source is not null
            || !hello.IdleAck || !hello.NativeLifecycleSettled || hello.WorkerProcessExited || hello.Error is not null
            || hello.NoEntryCandidate
            || hello.NativeLogPath != "" || hello.RequestExitCode is not null || hello.RequestEndDurable || hello.EndLogSha256 != ""
            || hello.SourceHashComputations != 0 || hello.SourceFileIdentity != "" || hello.SourceHashPinReused)
            throw new InvalidDataException("RESIDENT_HELLO_INVALID");
    }
    private void Poison(Exception error)
    {
        poisoned = true;
        report?.Invoke("RESIDENT_SESSION_QUARANTINED", new { Error = error.Message, RequestSequence = sequence,
            NativeRequestRetried = false, ProcessKilled = false });
        try { pipe?.Dispose(); } catch { }
    }
    private async Task ObserveActualExitAsync(Process owned)
    {
        await owned.WaitForExitAsync().ConfigureAwait(false);
        if (!owned.HasExited) throw new IOException("RESIDENT_ACTUAL_PROCESS_EXIT_UNCONFIRMED");
        actualExitConfirmed = true;
        report?.Invoke("RESIDENT_PROCESS_ACTUAL_EXIT", new { WorkerProcessId = workerPid, WorkerCreation = workerCreation, ActualExitCode = owned.ExitCode });
    }
    internal async Task StopAndWaitForActualExitAsync(CancellationToken outerToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        bool acquired = false;
        try
        {
            await serial.WaitAsync(deadline.Token).ConfigureAwait(false); acquired = true;
            if (disposed) { if (actualExit is not null) await actualExit.WaitAsync(deadline.Token).ConfigureAwait(false); return; }
            if (process is null) { disposed = true; packagePin?.Dispose(); return; }
            if (!poisoned && !process.HasExited)
            {
                VerifyPeer(); var stop = Request("stop", "stop-" + Guid.NewGuid().ToString("N"));
                await SendAsync(stop, deadline.Token).ConfigureAwait(false);
                var stopped = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (!WorkerReceiptPolicy.Matches(stop, stopped, "stopped") || stopped.RequestExitCode != 0
                    || !stopped.NativeLifecycleSettled || stopped.IdleAck || stopped.NoEntryCandidate || stopped.Error is not null)
                    throw new InvalidDataException("RESIDENT_STOP_RECEIPT_MISMATCH");
            }
            else { try { pipe?.Dispose(); } catch { } }
            if (actualExit is not null) await actualExit.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!process.HasExited) throw new IOException("RESIDENT_EXIT_WAIT_RETURNED_WITHOUT_EXIT");
            disposed = true; reader?.Dispose(); writer?.Dispose(); pipe?.Dispose(); process.Dispose(); packagePin?.Dispose();
        }
        catch (Exception error) { Poison(error); throw; }
        finally { if (acquired) serial.Release(); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
