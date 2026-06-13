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
        catch { /* no console / headless → nothing to keep open */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);
}
