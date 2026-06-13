using Subsystem.Windows;

// sswin — the Windows head. One binary, several entry modes:
//   sswin <powershell...>        run argv as PowerShell in the hosted runspace (built-ins + project cmdlets)
//   sswin -Command "..."         explicit command (pwsh-compatible)
//   sswin -EncodedCommand <b64>  base64 UTF-16LE command — quoting-proof, the agent's door
//   sswin -File <path>           run a script file
//   sswin selftest               VOM + Cm kernel self-tests (Layers 1-2)
//   (no args)                    selftest for now; GUI mode lands at Layer 5
if (args.Length == 0) return SelfTest.Run();
if (args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase)) return SelfTest.Run();
return Shim.Run(args);
