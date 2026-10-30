using System.Diagnostics;
using System.Runtime.InteropServices;
using MemoryHub;

namespace LcPlayerMarket;

internal interface IPatrolForegroundWindow
{
    nint Resolve(SourceIdentity source);
    bool Matches(nint window, SourceIdentity source);
    bool IsForeground(nint window, SourceIdentity source);
    bool IsMinimized(nint window);
    void Restore(nint window);
    void RequestForeground(nint window);
}

internal static class PatrolForegroundGate
{
    internal static bool TryOnce(SourceIdentity expected, Func<SourceIdentity?> current,
        IPatrolForegroundWindow windows, CancellationToken token)
    {
        void Check(nint hwnd)
        {
            token.ThrowIfCancellationRequested();
            if (current() is not { } now || now.ProcessId != expected.ProcessId || now.CreationFileTime != expected.CreationFileTime
                || now.SessionKey != expected.SessionKey || !windows.Matches(hwnd, expected))
                throw new InvalidOperationException("LC 身分或主視窗已變更；巡迴尚未啟動。");
        }
        token.ThrowIfCancellationRequested();
        nint window = windows.Resolve(expected);
        if (window == 0) throw new InvalidOperationException("找不到唯一的 LC 主視窗；巡迴尚未啟動。");
        Check(window);
        try
        {
            if (windows.IsMinimized(window)) windows.Restore(window);
            if (!windows.IsForeground(window, expected)) windows.RequestForeground(window);
        }
        catch (Exception) { Check(window); return false; }
        Check(window);
        return windows.IsForeground(window, expected);
    }

}

internal sealed class PatrolForegroundWindow : IPatrolForegroundWindow
{
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    public nint Resolve(SourceIdentity source)
    {
        var candidates = new List<nint>();
        EnumWindows((hwnd, _) => { GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == source.ProcessId && IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == 0) candidates.Add(hwnd); return true; }, 0);
        return candidates.Count == 1 && Matches(candidates[0], source) ? candidates[0] : 0;
    }
    public bool Matches(nint window, SourceIdentity source)
    {
        try
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != source.ProcessId || !IsWindowVisible(window) || GetWindow(window, 4) != 0) return false;
            using var process = Process.GetProcessById(source.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == source.CreationFileTime
                && string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(source.ImagePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    public bool IsForeground(nint window, SourceIdentity source) => GetForegroundWindow() == window && Matches(window, source);
    public bool IsMinimized(nint window) => IsIconic(window);
    public void Restore(nint window) => ShowWindowAsync(window, 9);
    public void RequestForeground(nint window) => SetForegroundWindow(window);
}
