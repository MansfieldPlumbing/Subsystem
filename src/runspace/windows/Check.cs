namespace Subsystem.Windows;

// `ss check` — the anti-drift / anti-slop analyzer ratchet (SS000-SS021), run IN-PROCESS: no dotnet, no
// MSBuild, no pre-published checker. The work lives in InProcGate — ss.exe compiles its own analyzer suite
// from the source it carries (src/analyzers/*.cs) with the bundled Roslyn and runs it over an in-proc
// compilation of the runspace tree, the ouroboros gating itself from itself. Flags pass straight through:
//   ss check            the full finding roster, grouped by rule
//   ss check --list      the analyzer roster (id + what it enforces)
//   ss check --gate      the fail-closed ratchet (NEW violations above the baseline only)
//   ss check --write-baseline   regenerate SS-BASELINE.txt from the current tree
//   ss check --path <dir>       gate a source tree (default: the repo beside the exe / embedded source)
internal static class Check
{
    public static int Run(string[] args) => InProcGate.Run(args);
}
