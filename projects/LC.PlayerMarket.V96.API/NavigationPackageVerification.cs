using System.Diagnostics;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class NavigationPackageVerification
{
    internal static int Verify(string output)
    {
        var timer = Stopwatch.StartNew();
        int count = 0; string? error = null;
        try { count = NavigationBundleContract.Verify(Path.Combine(AppContext.BaseDirectory, "Navigation", "MarketNavigationBackend.exe")); }
        catch (Exception e) { error = e.ToString(); }
        Save(output, new { Version = "83.0.0", Success = error is null, RequiredFiles = NavigationBundleContract.ExpectedFileCount,
            VerifiedFiles = count, ExpectedManifestSha256 = NavigationBundleContract.ManifestSha256,
            BaseDirectory = AppContext.BaseDirectory, WorkingDirectory = Environment.CurrentDirectory,
            ElapsedMs = timer.Elapsed.TotalMilliseconds, Error = error, ChildStarted = false });
        return error is null ? 0 : 1;
    }

    internal static async Task<int> StartupAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("驗證輸出已存在，未覆寫。");
        Directory.CreateDirectory(output);
        var rounds = new List<object>(); var errors = new List<string>();
        bool preCancelledRejected = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // This exercises the real child bootstrap, authentication and shutdown only.
        // No market reader, capture, navigate, game input, user configuration or foreground API is used.
        for (int round = 0; round < 2 && errors.Count == 0; round++)
        {
            var watch = Stopwatch.StartNew();
            var client = new PatrolNavigationClient(Path.Combine(output, "round-" + round), () => null, readOnly: true);
            Process? child = null; int? childId = null; bool ready = false, shutdown = false, probe = false; int? exitCode = null;
            string? childPath = null;
            double? readyMs = null;
            try
            {
                childId = await client.InitializeReadOnlyAsync(deadline.Token);
                child = Process.GetProcessById(childId.Value);
                childPath = child.MainModule?.FileName;
                if (!string.Equals(childPath, Path.Combine(AppContext.BaseDirectory, "Navigation", "MarketNavigationBackend.exe"),
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("啟動的導航程式不屬於本發布包。");
                ready = !child.HasExited; readyMs = watch.Elapsed.TotalMilliseconds;
                if (!ready) throw new InvalidDataException("導航後端在驗證 ready 後退出。");
                await client.ProbeReadOnlyReadyAsync(deadline.Token); probe = true;
            }
            catch (Exception e) { errors.Add(e.ToString()); }
            finally
            {
                try
                {
                    await client.DisposeAsync();
                    shutdown = child is not null && child.HasExited;
                    if (shutdown) exitCode = client.ChildExitCode;
                }
                catch (Exception e) { errors.Add("正常清理失敗：" + e); }
                if (child is not null && !child.HasExited)
                {
                    // Only our never-dispatched read-only child; failure remains a failure in the report.
                    errors.Add("唯讀測試子程序未正常停止，正在終止本測試擁有的子程序。");
                    child.Kill(); await child.WaitForExitAsync();
                }
                child?.Dispose();
            }
            if (!ready || !probe || !shutdown || exitCode != 0) errors.Add("真導航後端就緒／正常退出條件未全部通過。");
            rounds.Add(new { Round = round + 1, ChildId = childId, Ready = ready, ReadyMs = readyMs,
                ChildPath = childPath, AuthenticatedProbe = probe,
                ShutdownConfirmed = shutdown, ExitCode = exitCode, TotalMs = watch.Elapsed.TotalMilliseconds });
        }
        if (errors.Count == 0)
        {
            await using var cancelled = new PatrolNavigationClient(Path.Combine(output, "cancelled"), () => null, readOnly: true);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await cancelled.InitializeReadOnlyAsync(stop.Token); }
            catch (OperationCanceledException) { preCancelledRejected = !Directory.Exists(Path.Combine(output, "cancelled")); }
            if (!preCancelledRejected) errors.Add("取消未在啟動子程序前生效。");
        }
        bool success = errors.Count == 0 && rounds.Count == 2 && preCancelledRejected;
        Save(Path.Combine(output, "result.json"), new { Version = "83.0.0", Success = success,
            BaseDirectory = AppContext.BaseDirectory, WorkingDirectory = Environment.CurrentDirectory,
            ReadOnlyChildBootstrap = true, Rounds = rounds, PreCancelledRejected = preCancelledRejected,
            CapturesRequested = 0, NavigationsRequested = 0, LivePatrolVerified = false, Errors = errors });
        return success ? 0 : 1;
    }

    private static void Save(string path, object report)
    {
        using var file = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
    }
}
