using System;

namespace Subsystem.Windows;

// `ss help` — the self-teaching front door. A cold reader (agent, Scott, or a screen-reader user)
// must learn the tool from this alone, without reaching for memory or model training. Pair with
// `ss describe`, which teaches the SYSTEM and the source.
internal static class Help
{
    public static int Print()
    {
        Console.WriteLine(Text);
        return 0;
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
  ss check [--gate|--list]  analyzer ratchet (SS000-017); --gate = fail-closed (Build Failed); --list = the analyzer roster
  ss contextualize  (-c)    describe the system from the binary (alias: describe) — add --json | --map
  ss describe --map         the live architecture map, subsystem by subsystem
  ss describe --json        the contract as JSON (for agents / MCP)
  ss <verb> --path|-p <dir> point any verb at a source tree instead of the repo beside the exe
  ss help                   this text

WHAT THIS IS
  An in-process, NT-Object-Manager-shaped CoreCLR + PowerShell runtime: ONE object namespace,
  refcounted handles, per-owner quotas, deterministic cascade-kill (Terminate / DropPrefix). The
  registry (Cm) is a PROJECTION of the namespace; the UI is a presenter that holds nothing;
  behaviors are verbs on objects. ""It's NT, and it's a fractal.""

CODE CONTEXTUALIZER — understand the system AND its source, from the binary alone
  ss describe            contract · component DAG · the project cmdlets loaded here
  ss describe --map      the live context map: every file, its top-level types, and what it INCLUDES
                         (its internal `using` edges) — re-read from source each call, never a stale snapshot
  ss describe --json     the contract as JSON (for agents / MCP)
  ss extract <dir>       write the embedded source back out — this exe carries its own code
  ss Get-Command         every command available in this shell";
}
