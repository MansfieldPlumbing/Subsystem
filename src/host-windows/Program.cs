using Subsystem.Windows;

// ss — the Windows head. One binary, several entry modes:
//   ss <powershell...>        run argv as PowerShell in the hosted runspace (built-ins + project cmdlets)
//   ss -Command "..."         explicit command (pwsh-compatible)
//   ss -EncodedCommand <b64>  base64 UTF-16LE command — quoting-proof, the agent's door
//   ss -File <path>           run a script file
//   ss selftest               VOM + Cm kernel self-tests (Layers 1-2)
//   ss describe [--map|--json] self-describe the system (contract, components, cmdlets, source map)
//   ss help                   usage
//   (no args)                 help — first contact teaches the modes
// No args = first contact. Teach the modes — and if this was a double-click (ss owns its console), keep
// the window open so the help is readable instead of flashing and vanishing.
if (args.Length == 0) { var rc = Help.Print(); Interactive.KeepOpenIfDoubleClicked(); return rc; }
var mode = args[0].ToLowerInvariant();
return mode switch
{
    "selftest"                                                       => SelfTest.Run(),
    "help" or "-help" or "--help" or "-h" or "/help" or "-?" or "/?"  => Help.Print(),
    "describe" or "-describe" or "--describe"
        or "contextualize" or "--contextualize" or "-c"              => Describe.Run(args[1..]),
    "build" or "-build" or "--build"                                 => Build.Run(args[1..]),
    "check" or "-check" or "--check"                                 => Check.Run(args[1..]),
    "extract" or "-extract" or "--extract"                           => SelfSource.Extract(args[1..]),
    _                                                                => Shim.Run(args),
};
