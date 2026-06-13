using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;

namespace Subsystem.Windows;

// Layer 3: sswin as a PowerShell SUPERSET. Run argv (or -Command / -EncodedCommand / -File) in a
// hosted runspace and print the formatted result. One-shot today (a warm `serve` runspace over the
// named pipe comes later). The runspace is where the project's own cmdlets — CodeContext, the gate,
// the vom: provider — load beside every built-in, so `sswin gci`, `sswin get-codecontext`, and
// `sswin gci vom:\` are one shell. -EncodedCommand is the quoting-proof door for the agent.
internal static class Shim
{
    public static int Run(string[] args)
    {
        var script = ResolveScript(args);
        if (string.IsNullOrWhiteSpace(script)) { Console.Error.WriteLine("sswin: nothing to run"); return 2; }

        var iss = InitialSessionState.CreateDefault();   // full built-in cmdlet set (Management, Utility, ...)
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        LoadProjectCmdlets(iss);                         // the dogfood surface, beside the built-ins

        using var rs = RunspaceFactory.CreateRunspace(iss);
        rs.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        // Pipe results through Out-String so objects render the way pwsh's console does, not raw ToString.
        ps.AddScript(script).AddCommand("Out-String");

        int code = 0;
        try
        {
            foreach (var r in ps.Invoke()) Console.Write(r?.ToString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("sswin: " + ex.Message);
            return 1;
        }
        foreach (var e in ps.Streams.Error) { Console.Error.WriteLine(e.ToString()); code = 1; }
        return code;
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
        catch (Exception ex) { Console.Error.WriteLine("sswin: project cmdlets failed to load: " + ex.Message); }
    }

    // pwsh-compatible argument resolution; bare passthrough (`sswin gci`) is the default.
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
