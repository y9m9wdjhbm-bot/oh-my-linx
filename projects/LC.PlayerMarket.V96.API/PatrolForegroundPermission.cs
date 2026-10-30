using System.Runtime.InteropServices;

namespace LcPlayerMarket;

internal static class PatrolForegroundPermission
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    internal static bool Prepare(int hostPid, int gamePid, int backendPid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
        return Prepare(hostPid, gamePid, backendPid, foregroundPid,
            target => AllowSetForegroundWindow(checked((uint)target)));
    }

    // Permission only: this helper never activates a window or sends game input.
    internal static bool Prepare(int hostPid, int gamePid, int backendPid, uint foregroundPid, Func<int, bool> grant)
    {
        if (hostPid <= 0 || gamePid <= 0 || backendPid <= 0 || hostPid == gamePid
            || backendPid == hostPid || backendPid == gamePid) return false;
        if (foregroundPid == gamePid) return true;
        return foregroundPid == hostPid && grant(backendPid);
    }
}
