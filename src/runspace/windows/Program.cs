using Subsystem.Windows;

// ss — the Windows head. One binary, several entry modes:
//   ss <powershell...>        run argv as PowerShell in the hosted runspace (built-ins + project cmdlets)
//   ss -Command "..."         explicit command (pwsh-compatible)
//   ss -EncodedCommand <b64>  base64 UTF-16LE command — quoting-proof, the agent's door
//   ss -File <path>           run a script file
//   ss selftest               VOM + Cm kernel self-tests (Layers 1-2), logged to smoketest-log.md
//   ss diag                   the living diagnostic suite (kernel + toolchain + self-carry), logged to the ledger
//   ss contextualize [--map|--json] (-c) self-describe the system (contract, components, cmdlets, source map)
//   ss onboard                the one-shot alignment package — telos · laws · decisions · state · contract
//   ss help                   usage
//   (no args)                 help — first contact teaches the modes
// No args = first contact. Teach the modes — and if this was a double-click (ss owns its console), keep
// the window open so the help is readable instead of flashing and vanishing.
// No args = first contact. A DOUBLE-CLICK (owns its console, interactive) opens the shell UI — the WebView
// window on the DirectPort adapter. A bare shell / redirected launch just prints help.
if (args.Length == 0)
{
    if (Interactive.IsDoubleClick()) { Interactive.HideOwnConsole(); return WinShellUi.Run(System.Array.Empty<string>()); }
    return Help.Print();
}
var mode = args[0].ToLowerInvariant();
return mode switch
{
    "selftest"                                                       => SelfTest.Run(args[1..]),
    "diag" or "-diag" or "--diag" or "selfcheck"                     => Diag.Run(args[1..]),
    "help" or "-help" or "--help" or "-h" or "/help" or "-?" or "/?"  => Help.Print(),
    "contextualize" or "-contextualize" or "--contextualize" or "-c" => Contextualize.Run(args[1..]),
    "onboard" or "-onboard" or "--onboard" or "brief"                => Onboard.Run(args[1..]),
    "build" or "-build" or "--build"                                 => Build.Run(args[1..]),
    "check" or "-check" or "--check"                                 => Check.Run(args[1..]),
    "extract" or "-extract" or "--extract"                           => SelfSource.Extract(args[1..]),
    "chat"                                                           => Chat.Run(args[1..]),
    "surface" or "-surface" or "--surface"                           => Surface.Run(args[1..]),
    "view" or "-view" or "--view"                                    => View.Run(args[1..]),
    "ui" or "-ui" or "--ui"                                          => WinShellUi.Run(args[1..]),
    "mcp" or "-mcp" or "--mcp"                                       => Mcp.Run(args[1..]),
    "git"                                                            => Git.Run(args[1..]),
    "status" or "st"                                                 => Status.Run(args[1..]),
    "refs"                                                           => Refs.Run(args[1..]),
    _                                                                => Shim.Run(args),
};
