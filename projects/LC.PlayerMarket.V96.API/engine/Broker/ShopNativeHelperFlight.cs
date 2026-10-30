using System.Diagnostics;

namespace MemoryHub.Service;

/// <summary>
/// Tracks the process we actually launched, independently of an individual
/// caller's wait budget. A wait timeout never proves process/native completion.
/// This class has no journal, request-settlement, retry or process-kill API.
/// </summary>
internal sealed partial class ShopNativeHelperFlight
{
    private readonly object gate = new();
    private Flight? current;

    internal bool IsActive { get { lock (gate) return current is not null || residentOperation is not null; } }

    internal void RequireAvailable()
    {
        lock (gate)
            if (current is not null || residentOperation is not null)
                throw new ShopNativeHelperBusyException();
    }

    internal Task<int> RunAsync(Func<IShopNativeHelperProcess> start, TimeSpan waitBudget)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (waitBudget < TimeSpan.Zero && waitBudget != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(waitBudget));
        Flight flight;
        lock (gate)
        {
            if (current is not null || residentOperation is not null) throw new ShopNativeHelperBusyException();
            // Reserve and start under one lock. A rejected caller cannot invoke
            // the factory, even when several brokers share this tracker.
            var process = start() ?? throw new IOException("原生 helper 未能啟動。");
            current = flight = new(process);
        }
        _ = ObserveExitAsync(flight);
        return WaitWithBudgetAsync(flight, waitBudget);
    }

    internal Task WaitUntilExitAsync()
    {
        lock (gate) return (Task?)current?.Exited.Task ?? residentOperation?.ActualWorkerExit() ?? Task.CompletedTask;
    }

    private static async Task<int> WaitWithBudgetAsync(Flight flight, TimeSpan waitBudget)
    {
        try { return await flight.Exited.Task.WaitAsync(waitBudget).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            // Do not cancel the exit observer, dispose the process handle, parse
            // a partial/late receipt, clear a pending file or retry the request.
            throw new ShopNativeHelperWaitTimeoutException(error);
        }
    }

    private async Task ObserveExitAsync(Flight flight)
    {
        try
        {
            await flight.Process.WaitForExitAsync().ConfigureAwait(false);
            if (!flight.Process.HasExited)
                throw new IOException("helper 退出等待已返回，但無法確認程序已退出。");
            int exitCode = flight.Process.ExitCode;
            // Keep the flight through handle disposal. If observation or local
            // cleanup fails, retain the flight and the original unknown intent.
            flight.Process.Dispose();
            lock (gate)
            {
                if (!ReferenceEquals(current, flight))
                    throw new InvalidOperationException("helper 執行追蹤身分不一致。");
                current = null;
            }
            flight.Exited.TrySetResult(exitCode);
        }
        catch (Exception error)
        {
            // Faulted monitoring is not exit evidence. Keeping current also
            // prevents another broker in this process from bypassing the fault.
            flight.Exited.TrySetException(new ShopNativeHelperExitUnconfirmedException(error));
            // Mark the fault observed even if every bounded waiter timed out.
            _ = flight.Exited.Task.Exception;
        }
    }

    private sealed class Flight(IShopNativeHelperProcess process)
    {
        internal IShopNativeHelperProcess Process { get; } = process;
        internal TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Only the owned helper process is observed; this never opens LC.</summary>
internal interface IShopNativeHelperProcess : IDisposable
{
    Task WaitForExitAsync();
    bool HasExited { get; }
    int ExitCode { get; }
}

internal sealed record ShopNativeHelperStage(string Stage, int? HelperProcessId,
    DateTimeOffset ObservedAt, double ElapsedMilliseconds, int? ExitCode = null, string? ErrorType = null);

internal sealed class ShopNativeHelperProcess(Process process, Action<ShopNativeHelperStage>? observe,
    Stopwatch lifetime) : IShopNativeHelperProcess
{
    private readonly int processId = process.Id;
    public async Task WaitForExitAsync()
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (process.HasExited)
            Observe(observe, new("Exited", processId, DateTimeOffset.UtcNow, lifetime.Elapsed.TotalMilliseconds, process.ExitCode));
    }
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public void Dispose() => process.Dispose();

    internal static IShopNativeHelperProcess Start(ProcessStartInfo start, Action<ShopNativeHelperStage>? observe = null)
    {
        var lifetime = Stopwatch.StartNew();
        Observe(observe, new("ProcessStartBeginning", null, DateTimeOffset.UtcNow, 0));
        try
        {
            var process = Process.Start(start) ?? throw new IOException("原生 helper 未能啟動。");
            Observe(observe, new("ProcessStartReturned", process.Id, DateTimeOffset.UtcNow, lifetime.Elapsed.TotalMilliseconds));
            return new ShopNativeHelperProcess(process, observe, lifetime);
        }
        catch (Exception error)
        {
            Observe(observe, new("ProcessStartFailed", null, DateTimeOffset.UtcNow,
                lifetime.Elapsed.TotalMilliseconds, ErrorType: error.GetType().Name));
            throw;
        }
    }

    private static void Observe(Action<ShopNativeHelperStage>? observe, ShopNativeHelperStage stage)
    {
        // Diagnostics cannot turn an observed exit into an unknown lifetime or
        // alter the original exception. No command-line or argument data is emitted.
        try { observe?.Invoke(stage); } catch { }
    }
}

internal sealed class ShopNativeHelperBusyException() : InvalidOperationException(
    "同程序前一個原生 helper 尚未確認退出；本頁未啟動另一個 helper，也未重送原請求。");

internal sealed class ShopNativeHelperWaitTimeoutException(Exception inner) : TimeoutException(
    "helper 超過等待上限；保留原程序追蹤直到確認退出，停止後續頁面，原請求仍未確認且不重送。", inner);

internal sealed class ShopNativeHelperExitUnconfirmedException(Exception inner) : IOException(
    "無法確認原 helper 已退出；保留執行鎖與原請求未確認狀態，不啟動另一個 helper。", inner);
