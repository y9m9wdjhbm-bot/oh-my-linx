using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class PatrolReadOnlySmoke
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    internal static async Task<int> RunAsync(string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("唯讀巡迴驗證輸出已存在，未覆寫。");
        Directory.CreateDirectory(output);
        var errors = new List<string>(); var samples = new List<PatrolObservation>();
        nint foreground = GetForegroundWindow(); bool stopped = false, moveRejected = false;
        HubSnapshot? latest = null;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var backend = new StandaloneMarketBackend(Path.Combine(output, "standalone-state"));
        var navigation = new PatrolNavigationClient(Path.Combine(output, "navigation-runtime"),
            () => Volatile.Read(ref latest), readOnly: true);
        Task watcher = Task.Run(async () =>
        {
            try { await foreach (var item in backend.WatchAsync(lifetime.Token)) Volatile.Write(ref latest, item); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        try
        {
            while (!new LiveContext(Volatile.Read(ref latest), DateTimeOffset.UtcNow).Valid)
                await Task.Delay(100, lifetime.Token);
            for (int index = 0; index < 3; index++)
            {
                samples.Add(await navigation.CaptureAsync(lifetime.Token));
                await Task.Delay(120, lifetime.Token);
            }
            if (samples.Any(item => item.Source != samples[0].Source || item.SelfId != samples[0].SelfId || item.MapId != samples[0].MapId))
                throw new InvalidDataException("三輪定位點來源／角色／地圖不同。");
            var observed = samples[0];
            try
            {
                await navigation.StartAsync(new(Guid.NewGuid().ToString("N"), observed,
                    new(Guid.NewGuid().ToString("N"), "唯讀拒絕測試", observed.MapId.ToString(System.Globalization.CultureInfo.InvariantCulture), "", observed.X, observed.Y)), lifetime.Token);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("唯讀驗證模式", StringComparison.Ordinal)) { moveRejected = true; }
            if (!moveRejected) throw new InvalidOperationException("唯讀模式沒有在送出前拒絕導航。");
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        finally
        {
            try { await navigation.DisposeAsync(); }
            catch (Exception error) { errors.Add("導航關閉：" + error); }
            lifetime.Cancel();
            try { await watcher; } catch (Exception error) { errors.Add("讀取工作：" + error); }
            try { await backend.DisposeAsync(); stopped = true; }
            catch (Exception error) { errors.Add("商店讀取關閉：" + error); }
        }
        bool foregroundUnchanged = GetForegroundWindow() == foreground;
        var result = new
        {
            Version = typeof(Program).Assembly.GetName().Version?.ToString(), Success = errors.Count == 0 && samples.Count == 3 && stopped && moveRejected && foregroundUnchanged,
            PassedRounds = samples.Count, ReadOnly = true, IsAdministrator = true, NavigationReadOnly = true,
            GameInput = 0, NativeRequests = 0, MoveRejectedBeforeSend = moveRejected,
            ForegroundUnchanged = foregroundUnchanged, Stopped = stopped, Samples = samples, Errors = errors,
            LivePatrolVerified = false
        };
        using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, result, HubProtocol.Json); file.Flush(true);
        return result.Success ? 0 : 1;
    }
}
