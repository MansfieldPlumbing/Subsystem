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
        // The opinionated-shell refusal gate. ss is PowerShell 7 + the VOM kernel — not a launcher for
        // stringy shell-isms. A passthrough string that fronts a first-class ss verb or a banned text tool
        // is refused with the native way to say it. --raw is the one explicit escape, stripped here.
        bool raw = args.Any(a => a.Equals("--raw", StringComparison.OrdinalIgnoreCase) || a.Equals("-raw", StringComparison.OrdinalIgnoreCase));
        if (raw)
            args = args.Where(a => !a.Equals("--raw", StringComparison.OrdinalIgnoreCase) && !a.Equals("-raw", StringComparison.OrdinalIgnoreCase)).ToArray();
        else
        {
            var refusal = RefuseStringy(args);
            if (refusal != null) { Console.Error.WriteLine(refusal); return 2; }
        }

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

    // Register the project's cmdlets from the in-memory assemblies by their [Cmdlet] attribute —
    // the single-file-safe path (Assembly.Location is empty in a single-file bundle, so ImportPSModule
    // by path fails; this is the same attribute-reflection SubsystemAliases uses on Android). Two assemblies:
    // the CodeContext cmdlets live in their own assembly; the ticket/EOS cmdlets (Open/Get/Close-Ticket,
    // Add-EosLog) live in THIS host assembly. Dedup by command name. Additive: a load failure is reported,
    // never swallowed, and the shell still works as plain pwsh. SHARED: the MCP host (Mcp.OpenRunspace) calls
    // this too — one loader, both faces, so the console and MCP cmdlet surfaces can never drift (#39).
    internal static void LoadProjectCmdlets(InitialSessionState iss)
    {
        var assemblies = new[]
        {
            typeof(Subsystem.Tools.CodeContext.Cmdlets.GetCodeContextCmdlet).Assembly,
            typeof(Shim).Assembly,
        }.Distinct();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in assemblies)
        {
            try
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                foreach (var type in types)
                {
                    var attr = type.GetCustomAttribute<CmdletAttribute>();
                    if (attr == null) continue;
                    var name = $"{attr.VerbName}-{attr.NounName}";
                    if (seen.Add(name))
                        iss.Commands.Add(new SessionStateCmdletEntry(name, type, null));
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"ss: project cmdlets failed to load from {asm.GetName().Name}: " + ex.Message); }
        }
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

    // ---- the opinionated-shell refusal gate ----

    // Inspect what a passthrough invocation actually fronts. Returns a refusal message (the native way to
    // say it), or null to allow. A -File invocation runs a real script and is always allowed; --raw is
    // handled by Run before this is reached.
    static string? RefuseStringy(string[] args)
    {
        if (args.Any(a => a.Equals("-File", StringComparison.OrdinalIgnoreCase))) return null;
        var token = LeadingToken(args);
        if (token == null) return null;

        // First-class ss verbs (kept in step with Program.cs dispatch): say it the verb way, no quotes.
        string[] verbs = { "git", "build", "check", "contextualize", "onboard", "diag", "selftest",
                           "status", "refs", "mcp", "chat", "extract", "surface", "view", "ui" };
        if (verbs.Contains(token))
            return $"ss: '{token}' is a first-class ss verb — run  ss {token} …  (drop the quotes), not a passthrough string.\n" +
                    "     Intentional PowerShell? re-run with --raw.";

        // ss already IS PowerShell 7 — wrapping it is redundant.
        if (token is "pwsh" or "powershell" or "ss")
            return "ss: ss already IS PowerShell 7 — run the command directly (e.g.  ss gci ). The wrapper is redundant; --raw to force.";

        // Off the dogfood floor: text-munging tools the doctrine refuses. Query objects / the compiler.
        string[] textTools = { "bash", "sh", "zsh", "grep", "egrep", "fgrep", "sed", "awk" };
        if (textTools.Contains(token))
            return $"ss: '{token}' is off the floor — ss is an object shell, not text-munging.\n" +
                    "     Search code with  ss refs <Symbol>  / Get-CodeContext and pipe objects, not lines.\n" +
                    "     If you truly mean it, --raw.";

        return null;
    }

    // The first bare command word the args resolve to — the thing being invoked. Reuses ResolveScript so
    // bare passthrough, -Command, and -EncodedCommand all surface the same leading token; strips a path and
    // an executable extension so `ss "git status"`, `ss -Command 'git ...'`, and an explicit git.exe path
    // all read as `git`. A .ps1 (or any non-exe) token is left intact, so user scripts are never refused.
    static string? LeadingToken(string[] args)
    {
        var script = ResolveScript(args);
        if (string.IsNullOrWhiteSpace(script)) return null;
        var s = script.TrimStart();
        int end = s.Length;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\r' or '\n' or ';' or '|' or '&' or '(') { end = i; break; }
        }
        var first = s.Substring(0, end).Trim('"', '\'', '`');
        int slash = first.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0 && slash + 1 < first.Length) first = first.Substring(slash + 1);
        foreach (var ext in new[] { ".exe", ".com", ".cmd", ".bat" })
            if (first.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { first = first.Substring(0, first.Length - ext.Length); break; }
        return first.Length == 0 ? null : first.ToLowerInvariant();
    }
}
