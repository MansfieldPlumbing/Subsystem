using System;
using System.Runtime.InteropServices;

namespace Subsystem.Windows;

// Don't let ss "swallow" itself on a double-click. When launched from Explorer it OWNS its console (it is
// the only process attached), so the window would flash help and vanish — useless. Keep it open there.
// A terminal/shell launch SHARES the shell's console (>1 attached process) and a piped/redirected launch
// is non-interactive (the agent door, scripting); both are left untouched. The "swallow" (run-and-exit)
// only happens when explicitly invoked that way — never on a double-click.
internal static class Interactive
{
    public static void KeepOpenIfDoubleClicked()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return;   // piped/scripted → never pause
            var buf = new uint[2];
            if (GetConsoleProcessList(buf, 2) > 1) return;                         // shares a shell's console → leave it
            Console.Write("\nPress any key to close . . . ");
            Console.ReadKey(intercept: true);
        }
        catch (Exception ex) { Dg.Warn("interactive", ex); /* no console / headless → nothing to keep open */ }
    }

    // A double-click from Explorer OWNS its console (only attached process) and is interactive (not piped).
    // That is the launch that opens the shell UI instead of printing help.
    public static bool IsDoubleClick()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
            var buf = new uint[2];
            return GetConsoleProcessList(buf, 2) <= 1;
        }
        catch { return false; }
    }

    // Hide THIS process's own console (double-click path only — IsDoubleClick already proved we own it, so
    // it is never a shell's window) so the UI doesn't leave a console behind it.
    public static void HideOwnConsole()
    {
        try { var h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE); }
        catch (Exception ex) { Dg.Warn("interactive", ex); /* headless → nothing to hide */ }
    }

    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
