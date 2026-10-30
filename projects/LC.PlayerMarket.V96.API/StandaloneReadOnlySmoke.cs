using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Bounded local-backend evidence. Only reads LC and refreshes targets; never opens or scans shops.</summary>
internal static class StandaloneReadOnlySmoke
{
    private static readonly string[] RequiredFields = ["selfId", "position", "nearbyPlayers", "playerShopStates", "personalShopListing"];

    internal static async Task<int> RunAsync(string outputDir)
    {
        string output = Path.GetFullPath(outputDir);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("唯讀驗證輸出已存在，拒絕覆寫：" + output);
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(new FileStream(Path.Combine(output, "read-only.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        object logGate = new();
        void Record(object value) { lock (logGate) log.WriteLine(JsonSerializer.Serialize(value, HubProtocol.Json)); }
        bool administrator;
        using (var identity = WindowsIdentity.GetCurrent()) administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (!administrator)
        {
            var failure = new { Success = false, State = "ADMIN_REQUIRED", IsAdministrator = false, OpenedGameProcess = false, NativeRequests = 0, LiveShopQueryVerified = false };
            Record(failure);
            WriteNew(Path.Combine(output, "result.json"), failure);
            return 2;
        }

        string profiles = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (!File.Exists(Path.Combine(profiles, "active.json")))
        {
            var failure = new { Success = false, State = "PROFILE_REQUIRED", IsAdministrator = true, OpenedGameProcess = false, Profiles = profiles, NativeRequests = 0, LiveShopQueryVerified = false };
            Record(failure);
            WriteNew(Path.Combine(output, "result.json"), failure);
            return 2;
        }
        var profileEvidence = new List<object>();
        if (Directory.Exists(profiles))
            foreach (var path in Directory.EnumerateFiles(profiles, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
                profileEvidence.Add(new { File = Path.GetFileName(path), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
        string stateRoot = Path.Combine(output, "standalone-state");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var samples = new List<object>();
        var errors = new List<string>();
        var receivedSnapshots = new ConcurrentQueue<HubSnapshot>();
        var sentCommands = new ConcurrentQueue<string>();
        CurrentLcSource? source = null;
        HubRuntime? runtime = null;
        ShopCommandResponse? refresh = null;
        SourceIdentity? bound = null;
        int passedRounds = 0;
        long snapshotCount = 0;
        bool transportDisabled = false, stopped = false, sourceAvailable = false, refreshSourceEqual = false;
        nint foreground = GetForegroundWindow();
        var firstSource = new TaskCompletionSource<HubSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StandaloneMarketBackend(stateRoot,
            logEntry =>
            {
                source = (CurrentLcSource)StandaloneMarketBackend.CreateDefaultSource(entry => { Record(entry); logEntry(entry); });
                return source;
            },
            (localRuntime, localSource, shops) =>
            {
                runtime = localRuntime;
                return new ReadOnlyCommands(new StandaloneShopCommands(new ShopBroker(localRuntime, (CurrentLcSource)localSource, shops)), sentCommands);
            });
        Task watcher = WatchAsync();
        try
        {
            HubSnapshot initial = await firstSource.Task.WaitAsync(deadline.Token);
            bound = initial.Source;
            sourceAvailable = bound is not null;
            transportDisabled = runtime is { TransportEnabled: false, ShopSearchOnlyEnabled: true };
            if (source is null || runtime is null || bound is null || !transportDisabled) throw new InvalidOperationException("本機來源、runtime 或停用 IPC 狀態不完整。");
            Record(new { Kind = "LOCAL_BACKEND_READY", IsAdministrator = administrator, TransportDisabled = transportDisabled, ConnectedToService = false, Source = bound, Profiles = profiles, ProfileEvidence = profileEvidence });
            var selected = new ReadSelection(RequiredFields);
            for (int index = 0; index < 3; index++)
            {
                ReadResult? slice = null;
                ShopDispatchSample? dispatch = null;
                SourceIdentity? before = runtime.Snapshot.Source;
                SourceIdentity? after = null;
                string? sampleError = null;
                DateTimeOffset at;
                bool equal = false, fresh = false;
                try
                {
                    slice = await Task.Run(() => source.ReadSlice(selected, deadline.Token), deadline.Token);
                    dispatch = await Task.Run(() => source.ResolveShopDispatchSource(bound, deadline.Token), deadline.Token);
                    at = DateTimeOffset.UtcNow;
                    after = runtime.Snapshot.Source;
                    equal = before == bound && slice.Source == bound && dispatch.Binding.Source == bound && dispatch.Observations.Source == bound && after == bound;
                    fresh = RequiredFields.All(key => dispatch.Observations.Fields.TryGetValue(key, out var field) && field.Fresh(at) && field.Value is not null);
                    if (!equal) sampleError = "完整 SourceIdentity 未相等；不只比較 SessionKey。";
                    else if (!fresh) sampleError = "同輪必要欄位有缺漏、Unknown 或超過 2 秒有效期限。";
                    if (sampleError is null) passedRounds++;
                }
                catch (Exception error) { sampleError = error.ToString(); }
                at = DateTimeOffset.UtcNow;
                var sample = new
                {
                    Index = index + 1, Success = sampleError is null, At = at,
                    BoundSource = bound, SourceBefore = before, SliceSource = slice?.Source,
                    BindingSource = dispatch?.Binding.Source, ObservationsSource = dispatch?.Observations.Source,
                    SourceAfter = after, FullSourceIdentityEqual = equal,
                    SameRoundFieldsFresh = fresh, BindingSampledAt = dispatch?.Binding.SampledAt,
                    Slice = slice is null ? null : new { slice.State, slice.Detail, slice.Reads, slice.ReadBytes, Fields = Summarize(slice, at) },
                    Dispatch = dispatch is null ? null : new { dispatch.Observations.State, dispatch.Observations.Detail, dispatch.Observations.Reads, dispatch.Observations.ReadBytes, Fields = Summarize(dispatch.Observations, at) },
                    Error = sampleError
                };
                samples.Add(sample); Record(sample);
                if (index < 2 && !deadline.IsCancellationRequested) await Task.Delay(150, deadline.Token);
            }
            if (runtime.Snapshot.Source == bound && !deadline.IsCancellationRequested)
            {
                refresh = await backend.SendAsync(new ShopCommandRequest
                {
                    Command = ShopCommands.RefreshTargets, RequestId = Guid.NewGuid().ToString("N"), ExpectedSourceSessionKey = bound.SessionKey, ForceDispatch = true
                }, deadline.Token);
                refreshSourceEqual = runtime.Snapshot.Source == bound;
                Record(new { Kind = "EXPLICIT_READ_ONLY_REFRESH", Response = refresh, FullSourceIdentityEqual = refreshSourceEqual });
            }
        }
        catch (Exception error) { errors.Add(error.ToString()); Record(new { Kind = "READ_ONLY_SMOKE_ERROR", Error = error.ToString() }); }
        finally
        {
            deadline.Cancel();
            try { await watcher; }
            catch (Exception error) { errors.Add("Watcher: " + error); }
            try { await backend.DisposeAsync(); stopped = runtime is null || !runtime.IsRunning; }
            catch (Exception error) { errors.Add("Dispose: " + error); }
        }

        int nativeIntents = 0, nativeEnds = 0;
        foreach (string path in Directory.Exists(stateRoot) ? Directory.EnumerateFiles(stateRoot, "hub-*.jsonl", SearchOption.AllDirectories) : [])
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var item = JsonDocument.Parse(line);
                if (!item.RootElement.TryGetProperty("kind", out var kind)) continue;
                if (kind.GetString() == "SHOP_NATIVE_INTENT") nativeIntents++;
                if (kind.GetString() == "SHOP_NATIVE_END") nativeEnds++;
            }
        }
        string[] nativeFiles = Directory.Exists(stateRoot)
            ? Directory.EnumerateDirectories(stateRoot, "native", SearchOption.AllDirectories).SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)).ToArray() : [];
        bool noNative = sentCommands.All(command => command == ShopCommands.RefreshTargets || command == ShopCommands.GetState)
            && nativeIntents == 0 && nativeEnds == 0 && nativeFiles.Length == 0 && refresh?.NativeEntered != true && refresh?.NativeReturned != true;
        bool refreshVerified = bound is not null && refreshSourceEqual && refresh is { State: "TargetsRefreshed", FailureCode: null, TargetSnapshot.CanDispatch: true }
            && refresh.CurrentSourceSessionKey == bound.SessionKey && refresh.TargetSnapshot.SourceSessionKey == bound.SessionKey
            && sentCommands.Count(command => command == ShopCommands.RefreshTargets) == 1;
        var report = new
        {
            Version = "83.0.0", ForceRefreshWithShoppingState = true, ReaderVersion = HubProtocol.Version,
            Success = passedRounds == 3 && samples.Count == 3 && errors.Count == 0 && noNative && transportDisabled && stopped && refreshVerified,
            IsAdministrator = administrator, ReadOnly = true, ConnectedToService = false, TransportDisabled = transportDisabled,
            SourceAvailable = sourceAvailable, BoundSource = bound, SourceIdentityComparison = "ProcessId/CreationFileTime/ImagePath/ImageBase/PeTimestamp/ImageSize/DiskSha256/SessionKey record equality",
            Samples = samples, PassedRounds = passedRounds, SnapshotCount = Interlocked.Read(ref snapshotCount),
            LastSnapshot = receivedSnapshots.LastOrDefault(), ManualRefresh = refresh, ManualRefreshVerified = refreshVerified, ManualRefreshSourceIdentityEqual = refreshSourceEqual,
            SentCommands = sentCommands.ToArray(), NativeIntentLogCount = nativeIntents, NativeEndLogCount = nativeEnds, NativeFiles = nativeFiles,
            NativeRequests = nativeIntents, NativeRequestAbsenceVerified = noNative, GameInput = 0, HooksInstalled = 0,
            LiveShopQueryVerified = false, ForegroundUnchanged = foreground == GetForegroundWindow(), Stopped = stopped,
            Profiles = profiles, ProfileEvidence = profileEvidence, Errors = errors,
            Limitation = "只驗證本機直接讀取與一次手動周遭刷新；未送查店命令，未驗證遊戲接受、商店頁面切換或完整批次查詢。"
        };
        WriteNew(Path.Combine(output, "result.json"), report);
        Record(new { Kind = "FINISHED", report.Success, report.NativeRequests, report.LiveShopQueryVerified, report.Stopped });
        Console.WriteLine(JsonSerializer.Serialize(new { report.Success, report.PassedRounds, report.NativeRequests, report.TransportDisabled, report.Stopped, Output = output }, HubProtocol.Json));
        return report.Success ? 0 : 1;

        async Task WatchAsync()
        {
            try
            {
                await foreach (var snapshot in backend.WatchAsync(deadline.Token))
                {
                    Interlocked.Increment(ref snapshotCount);
                    receivedSnapshots.Enqueue(snapshot);
                    while (receivedSnapshots.Count > 20) receivedSnapshots.TryDequeue(out _);
                    if (snapshot.Source is not null) firstSource.TrySetResult(snapshot);
                }
                firstSource.TrySetException(new InvalidOperationException("本機監看結束但尚未取得來源。"));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { firstSource.TrySetCanceled(deadline.Token); }
            catch (Exception error) { firstSource.TrySetException(error); throw; }
        }
    }

    private static object Summarize(ReadResult result, DateTimeOffset at) => RequiredFields.ToDictionary(key => key, key =>
    {
        var field = result.Fields.GetValueOrDefault(key) ?? FieldReading.Unknown("Missing");
        return new { field.State, field.SampledAt, AgeMilliseconds = (at - field.SampledAt)?.TotalMilliseconds, field.Detail, field.Evidence, field.Value };
    });

    private static void WriteNew(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions(HubProtocol.Json) { WriteIndented = true });
        stream.Flush(true);
    }

    private sealed class ReadOnlyCommands(IStandaloneShopCommands commands, ConcurrentQueue<string> requests) : IStandaloneShopCommands
    {
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            if (request.Command is not (ShopCommands.GetState or ShopCommands.RefreshTargets))
                throw new InvalidOperationException("唯讀驗證禁止傳送查店、掃描或其他命令。");
            requests.Enqueue(request.Command);
            return commands.SendAsync(request, token);
        }
        public ValueTask DisposeAsync() => commands.DisposeAsync();
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
