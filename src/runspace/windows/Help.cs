using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Reflection;

namespace Subsystem.Windows;

// `ss help` — the self-teaching front door. A cold reader (agent, Scott, or a screen-reader user)
// must learn the tool from this alone, without reaching for memory or model training. Pair with
// `ss contextualize`, which teaches the SYSTEM and the source.
internal static class Help
{
    public static int Print()
    {
        Console.WriteLine(Text);
        PrintProjectCmdlets();
        return 0;
    }

    // The project cmdlet roster, derived LIVE from the loaded assemblies (never a hardcoded list that rots) —
    // the same [Cmdlet]-attribute scan Shim uses to register them, so this grows the instant a cmdlet is added.
    private static void PrintProjectCmdlets()
    {
        var assemblies = new[]
        {
            typeof(Subsystem.Tools.CodeContext.Cmdlets.GetCodeContextCmdlet).Assembly,
            typeof(Help).Assembly,
        }.Distinct();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
                if (t.GetCustomAttribute<CmdletAttribute>() is { } a) names.Add($"{a.VerbName}-{a.NounName}");
        }
        if (names.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine($"PROJECT CMDLETS ({names.Count}) — loaded beside every pwsh built-in; run any in the shell (or `ss Get-Command`)");
        foreach (var n in names) Console.WriteLine("  " + n);
    }

    private const string Text =
@"ss — Subsystem's Windows head: a PowerShell 7.7 SUPERSET + the VOM/Cm object kernel.
Drop it anywhere; it self-describes. This is the IDE.

USAGE
  ss <powershell...>        run argv as PowerShell (built-ins + project cmdlets).   e.g.  ss gci
  ss -Command ""<script>""    run an explicit command (pwsh-compatible)
  ss -File <path>           run a script file
  ss -EncodedCommand <b64>  base64 (UTF-16LE) command — quoting-proof, the agent door
  ss selftest               run the VOM kernel + Cm registry self-tests (Layers 1-2)
  ss build [apk] [-o]       rebuild this exe (Windows head); `apk` builds+signs the Android head, then gates (-o forces a red gate)
  ss check [--gate|--list]  analyzer ratchet (SS000-SS022; run --list for the authoritative live roster); --gate = fail-closed (Build Failed); --list = the roster
  ss chat ""<prompt>""        run a prompt through the in-process LiteRT-LM CPU backend
  ss surface [name]         become a DirectPort PRODUCER (a test pattern) for the virtual camera (--grant)
  ss camera [name]          host the VirtuaCam broker — the virtual-webcam GATEWAY; feed it with `ss surface` (--grant)
  ss tui [--once]           paint the live VOM object namespace as cells -> VT (the dotnet-renderer floor); --once = one frame
  ss gateway                system-tray presence (the Windows GATEWAY) — launches the WebView shell + the TUI
  ss contextualize  (-c)    contextualize the system from the binary — add --json | --map
  ss contextualize --map    the live architecture map, subsystem by subsystem
  ss contextualize --json   the contract as JSON (for agents / MCP)
  ss onboard                one-shot alignment package: telos · laws · decisions · state · file manifest · contract (start here)
  ss <verb> --path|-p <dir> point any verb at a source tree instead of the repo beside the exe
  ss help                   this text

WHAT THIS IS
  An in-process, NT-Object-Manager-shaped CoreCLR + PowerShell runtime: ONE object namespace,
  refcounted handles, per-owner quotas, deterministic cascade-kill (Terminate / DropPrefix). The
  registry (Cm) is a PROJECTION of the namespace; the UI is a presenter that holds nothing;
  behaviors are verbs on objects. ""It's NT, and it's a fractal.""

CODE CONTEXTUALIZER — understand the system AND its source, from the binary alone
  ss contextualize       contract · component DAG · the project cmdlets loaded here
  ss contextualize --map the live context map: every file, its top-level types, and what it INCLUDES
                         (its internal `using` edges) — re-read from source each call, never a stale snapshot
  ss contextualize --json the contract as JSON (for agents / MCP)
  ss extract <dir>       write the embedded source back out — this exe carries its own code
  ss Get-Command         every command available in this shell";
}
