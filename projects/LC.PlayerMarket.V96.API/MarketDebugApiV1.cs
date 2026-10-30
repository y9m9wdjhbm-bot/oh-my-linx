using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LcPlayerMarket;

// Diagnostic requests only consume an already-published immutable JSON snapshot.
// This type has no reference to the reader, broker, controller or input services.
internal sealed class MarketDebugApiV1 : IDisposable
{
    internal const string PipeName = "Lineage.PlayerMarket.DebugApi.v1";
    private const int MaximumRequestBytes = 8192, MaximumResponseBytes = 262144;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> workers = [];
    private readonly string pipeName, sid;
    private readonly int session;
    private readonly Action<string> failure;
    private string snapshot = "{\"status\":\"Starting\"}";
    private int disposed;
    private long lastFailureTicks;
    internal MarketDebugApiV1(Action<string> failure, string? name = null)
    {
        this.failure = failure; pipeName = name ?? PipeName;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("ADMIN_REQUIRED");
        sid = identity.User?.Value ?? throw new UnauthorizedAccessException("SID_UNAVAILABLE");
        session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        // Each worker owns one pending listener or one connected client.
        for (int i = 0; i < 4; i++) workers.Add(Task.Run(ListenAsync));
    }
    internal void Publish(object value)
    {
        string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes - 2048)
            json = "{\"status\":\"SnapshotTooLarge\",\"partial\":true}";
        Volatile.Write(ref snapshot, json);
    }
    private async Task ListenAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 262144);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                // RunAsClient requires a message to have been read. Buffer only
                // bounded untrusted bytes first; authenticate before parsing a
                // request or exposing any cached product data.
                string? request = null;
                string? readError = null;
                try { request = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false); }
                catch (InvalidDataException) { readError = "REQUEST_TOO_LARGE"; }
                catch (DecoderFallbackException) { readError = "INVALID_UTF8"; }
                if (!Authorized(pipe)) continue;
                string response = readError is null ? Reply(request!) : Error(readError);
                byte[] bytes = Encoding.UTF8.GetBytes(response + "\n");
                if (bytes.Length > MaximumResponseBytes) throw new InvalidDataException("RESPONSE_TOO_LARGE");
                await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception error)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - Interlocked.Read(ref lastFailureTicks) > TimeSpan.FromSeconds(30).Ticks)
                {
                    Interlocked.Exchange(ref lastFailureTicks, now);
                    try { failure("DEBUG_API_LISTENER: " + error.GetType().Name); } catch { }
                }
                try { await Task.Delay(250, stopping.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }
    private bool Authorized(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid)) return false;
            using var client = System.Diagnostics.Process.GetProcessById(checked((int)clientPid));
            if (client.SessionId != session) return false;
            bool allowed = false;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(true);
                allowed = identity is not null && identity.User?.Value == sid
                    && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
            return allowed;
        }
        catch { return false; }
    }
    internal string Reply(string request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error("INVALID_REQUEST");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("protocol" or "operation")) return Error("INVALID_REQUEST");
            if (!root.TryGetProperty("protocol", out var version) || !version.TryGetInt32(out int protocol) || protocol != 1)
                return Error("UNSUPPORTED_PROTOCOL");
            if (!root.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String || operation.GetString() != "status")
                return Error("OPERATION_NOT_ALLOWED");
            using var state = JsonDocument.Parse(Volatile.Read(ref snapshot));
            return JsonSerializer.Serialize(new { protocol = 1, ok = true, product = "PlayerMarket", apiVersion = "1",
                capturedAt = DateTimeOffset.UtcNow, state = state.RootElement });
        }
        catch (JsonException) { return Error("INVALID_JSON"); }
        catch (InvalidOperationException) { return Error("INVALID_REQUEST"); }
    }
    private static string Error(string code) => JsonSerializer.Serialize(new { protocol = 1, ok = false, error = code });
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        byte[] next = new byte[1];
        while (buffer.Length < MaximumRequestBytes)
        {
            if (await stream.ReadAsync(next, cancellation).ConfigureAwait(false) != 1) throw new IOException("INCOMPLETE_REQUEST");
            if (next[0] == 10) return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            buffer.WriteByte(next[0]);
        }
        throw new InvalidDataException("REQUEST_TOO_LARGE");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        // Never block the UI on a slow diagnostic client. Workers own their handles.
        _ = Task.WhenAll(workers).ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    internal static int RunFixture(string readyPath, string stopPath)
    {
        if (File.Exists(readyPath) || File.Exists(stopPath)) return 2;
        string name = PipeName + ".fixture." + Environment.ProcessId;
        string logRoot = Path.Combine(MarketLogPaths.DefaultRoot, "debug-api");
        Directory.CreateDirectory(logRoot);
        string fixtureLog = Path.Combine(logRoot, "fixture-" + Guid.NewGuid().ToString("N") + ".jsonl");
        using var api = new MarketDebugApiV1(message => File.AppendAllText(fixtureLog, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, message }) + "\n"), name);
        api.Publish(new { fixture = true, version = "95.0.0-api", status = "FixtureReady", lcRead = false, nativeRequests = 0,
            scan = new { state = "SyntheticIdle" }, patrol = new { running = false }, error = (string?)null,
            logRoot = MarketLogPaths.DefaultRoot });
        File.WriteAllText(readyPath, JsonSerializer.Serialize(new { protocol = 1, pipe = name, processId = Environment.ProcessId, fixture = true }));
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!File.Exists(stopPath) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        return 0;
    }
}
