using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;

namespace Subsystem.Windows;

// Layer 3: ss as a PowerShell SUPERSET. Run argv (or -Command / -EncodedCommand / -File) in a
// hosted runspace and print the formatted result. One-shot today (a warm `serve` runspace over the
// named pipe comes later). The runspace is where the project's own cmdlets — CodeContext, the gate,
// the vom: provider — load beside every built-in, so `ss gci`, `ss get-codecontext`, and
// `ss gci vom:\` are one shell. -EncodedCommand is the quoting-proof door for the agent.
internal static class Shim
{
    public static int Run(string[] args)
    {
        var iss = InitialSessionState.CreateDefault();   // full built-in cmdlet set (Management, Utility, ...)
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        LoadProjectCmdlets(iss);                         // the dogfood surface, beside the built-ins

        // Render exactly like the pwsh console: a real PSHost wired to Console + Out-Default. This
        // fixes two output defects at once — (1) `exit` no longer swallows output (Out-Default writes
        // to the host DURING invocation, not in a terminal end-block); (2) Write-Host / Write-Warning /
        // the information stream now appear (a bare RunspaceFactory runspace drops the host streams —
        // friction F8). ConsoleHost.SetShouldExit also captures `exit N`, so ss returns the script's code.
        var host = new ConsoleHost();
        using var rs = RunspaceFactory.CreateRunspace(host, iss);
        rs.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        // -File <path> [args…] — invoke the FILE as a script command so $PSScriptRoot / $PSCommandPath /
        // $args resolve exactly like `pwsh -File`. Running the file's TEXT via AddScript loses that file
        // context (friction: a script that anchors on $PSScriptRoot — e.g. build-ss.ps1 — broke under
        // `ss -File`). Everything else (bare passthrough, -Command, -EncodedCommand) stays AddScript.
        int fileIdx = Array.FindIndex(args, a => a.Equals("-File", StringComparison.OrdinalIgnoreCase));
        if (fileIdx >= 0 && fileIdx + 1 < args.Length)
        {
            ps.AddCommand(args[fileIdx + 1], useLocalScope: false);
            for (int i = fileIdx + 2; i < args.Length; i++) ps.AddArgument(args[i]);
        }
        else
        {
            var script = ResolveScript(args);
            if (string.IsNullOrWhiteSpace(script)) { Console.Error.WriteLine("ss: nothing to run"); return 2; }
            ps.AddScript(script);
        }
        ps.AddCommand("Out-Default");

        int code = 0;
        try
        {
            ps.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ss: " + ex.Message);
            return 1;
        }
        // A real PowerShell error fails the run; a NATIVE command writing to stderr does NOT. git, dotnet
        // and adb all chatter on stderr while succeeding — friction F9: every `ss "<native>"` looked failed
        // because PowerShell surfaces native stderr as NativeCommandError records. Those don't set the code.
        foreach (var e in ps.Streams.Error)
        {
            Console.Error.WriteLine(e.ToString());
            if (!(e.FullyQualifiedErrorId ?? string.Empty).Contains("NativeCommand", StringComparison.OrdinalIgnoreCase))
                code = 1;
        }
        // For a passthrough shell, a native command's OWN exit code is the truth: a real failure surfaces,
        // a success (exit 0) stays green even after stderr chatter. `exit N` (host.ExitCode) still wins.
        if (rs.SessionStateProxy.GetVariable("LASTEXITCODE") is int nativeExit && nativeExit != 0)
            code = nativeExit;
        return host.ExitCode ?? code;
    }

    // Register the project's cmdlets from the in-memory assembly by their [Cmdlet] attribute —
    // the single-file-safe path (Assembly.Location is empty in a single-file bundle, so ImportPSModule
    // by path fails; this is the same attribute-reflection SubsystemAliases uses on Android). Additive:
    // a load failure is reported, never swallowed, and the shell still works as plain pwsh.
    static void LoadProjectCmdlets(InitialSessionState iss)
    {
        try
        {
            var asm = typeof(Subsystem.Tools.CodeContext.Cmdlets.GetCodeContextCmdlet).Assembly;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (var type in types)
            {
                var attr = type.GetCustomAttribute<CmdletAttribute>();
                if (attr != null)
                    iss.Commands.Add(new SessionStateCmdletEntry($"{attr.VerbName}-{attr.NounName}", type, null));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("ss: project cmdlets failed to load: " + ex.Message); }
    }

    // pwsh-compatible argument resolution; bare passthrough (`ss gci`) is the default.
    static string ResolveScript(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return Encoding.Unicode.GetString(Convert.FromBase64String(args[i + 1]));
            if (a.Equals("-Command", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return string.Join(' ', args[(i + 1)..]);
            if (a.Equals("-File", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return File.ReadAllText(args[i + 1]);
        }
        return string.Join(' ', args);
    }
}
