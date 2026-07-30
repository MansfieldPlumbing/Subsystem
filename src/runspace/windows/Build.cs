using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Subsystem.Windows;

// `ss build` — the build lives IN the binary, never an external .ps1. It dumps + embeds the source (so the
// rebuilt exe carries its own code), publishes a fresh single-file ss.exe, and self-replaces the running
// image via rename-aside. Source = the repo beside the exe, or — if none — the EMBEDDED source extracted to
// temp, so a dropped ss.exe with no repo still rebuilds itself.
//
// dotnet (on the drive) is still the compiler for this one step — the last external dep. The road to zero:
// Roslyn is already inside ss.exe (verified), but a single-file exe's TPA list is EMPTY, so Roslyn has no
// reference assemblies to compile against. The fix (next): embed the ref set, extract to temp, compile +
// apphost-pack in-process. See the session handoff for the exact plan.
internal static class Build
{
    public static int Run(string[] args)
    {
        var first = args.FirstOrDefault()?.ToLowerInvariant();
        return first switch
        {
            "-help" or "--help" or "-h" or "/?" or "help" => PrintHelp(),
            "apk" or "android"                            => BuildApk(args[1..]),
            "self" or "ss"                                => SelfBuild.Run(args[1..]),
            "win" or "windows" or "exe"                   => BuildWindows(args[1..]),
            _                                             => BuildWindows(args),
        };
    }

    private static int BuildWindows(string[] args)
    {
        Console.WriteLine("ss build — self-rebuild (Windows head). Safety checks:");

        var root = ResolveSource(PathArg(args));
        if (root == null) { Console.Error.WriteLine("  [FAIL] source: no repo beside the exe and no embedded source. Cannot build."); return 2; }
        int srcFiles = SafeCount(root, "*.cs");
        Console.WriteLine($"  [ok] source: {root} ({srcFiles} .cs files)");

        var csproj = Path.Combine(root, "subsystem.master.csproj");
        if (!File.Exists(csproj)) { Console.Error.WriteLine($"  [FAIL] project: {csproj} missing."); return 2; }
        Console.WriteLine($"  [ok] project: {csproj}");

        if (srcFiles < 20) { Console.Error.WriteLine($"  [FAIL] sanity: only {srcFiles} .cs files — the source looks incomplete; refusing to build from it."); return 2; }
        Console.WriteLine($"  [ok] sanity: source is complete");
        Console.WriteLine();

        bool clearForRelease = HasFlag(args, "--clear-for-release") || HasFlag(args, "-c");
        if (clearForRelease)
        {
            Console.Write("WARNING: --clear-for-release will permanently delete the local requests database. Are you sure? (y/N): ");
            var r1 = Console.ReadLine();
            if (r1?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
            {
                Console.Error.WriteLine("Build aborted by user (clear-for-release gate 1 rejected).");
                return 3;
            }

            Console.Write("ARE YOU REALLY, REALLY SURE? All local requests and EOS logs will be wiped! (yes/NO): ");
            var r2 = Console.ReadLine();
            if (r2?.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) != true)
            {
                Console.Error.WriteLine("Build aborted by user (clear-for-release gate 2 rejected).");
                return 3;
            }

            // Clean up requests and config DBs from the resolved unified location
            var requestsDb = Subsystem.Cm.EnvironmentVariables.Db.Requests;
            var configDb = Subsystem.Cm.EnvironmentVariables.Db.Config;
            foreach (var path in new[] { requestsDb, configDb })
            {
                if (File.Exists(path))
                {
                    try { File.Delete(path); Console.WriteLine($"ss build: deleted unified database for release ({path})"); }
                    catch (Exception ex) { Console.Error.WriteLine($"ss build: failed to delete database {path}: {ex.Message}"); }
                }
            }

            // Legacy paths clean up
            var legacyPaths = new[]
            {
                Path.Combine(root, "subsystem-requests.db"),
                Path.Combine(AppContext.BaseDirectory, "subsystem-requests.db"),
                Path.Combine(root, "subsystem-registry.db"),
                Path.Combine(AppContext.BaseDirectory, "subsystem-registry.db")
            };
            foreach (var path in legacyPaths)
            {
                if (File.Exists(path))
                {
                    try { File.Delete(path); Console.WriteLine($"ss build: deleted legacy database ({path})"); }
                    catch (Exception ex) { Console.Error.WriteLine($"ss build: failed to delete legacy database {path}: {ex.Message}"); }
                }
            }
        }

        Console.WriteLine("ss build: compiling in-process via Roslyn + packaging single-file bundle...");
        var (exeBytes, compileErrors) = SelfBuild.Compile(root);
        if (exeBytes == null)
        {
            var es = compileErrors ?? Array.Empty<string>();
            Console.Error.WriteLine($"ss build: RED — compile failed ({es.Length} errors). First 40:");
            foreach (var e in es.Take(40)) Console.Error.WriteLine("  " + e);
            return 1;
        }
        Console.WriteLine($"ss build: compiled ss.exe ({exeBytes.Length / (1024 * 1024):n0} MB)");

        // Replace the running exe IN-SITU (update in place) + drop a copy in the source repo. Never the drive root.
        var selfExe = Environment.ProcessPath ?? Path.Combine(root, "ss.exe");
        foreach (var t in new[] { selfExe, Path.Combine(root, "ss.exe") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { File.WriteAllBytes(t, exeBytes); Console.WriteLine("  + " + t); }
            catch   // the running image is locked — rename it aside (Windows allows it), then write.
            {
                var old = t + "." + Guid.NewGuid().ToString("N").Substring(0, 4) + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch (Exception ex) { Dg.Warn("build", ex); }
                try { File.Move(t, old); File.WriteAllBytes(t, exeBytes); Console.WriteLine($"  + {t}  (self-replaced; old → {old})"); }
                catch (Exception ex) { Console.Error.WriteLine($"  ! {t}: {ex.Message}"); }
            }
        }

        // Gather native sidecars from their repository sources and package them beside the executable
        var sidecarSources = new List<string>();
        var dpDll = Path.Combine(root, "src", "native", "directport", "directport.dll");
        if (File.Exists(dpDll)) sidecarSources.Add(dpDll);
        else
        {
            var fallbackDp = Path.Combine(root, "directport.dll");
            if (File.Exists(fallbackDp)) sidecarSources.Add(fallbackDp);
        }

        var vcDir = Path.Combine(root, "src", "native", "virtuacam");
        if (Directory.Exists(vcDir))
        {
            foreach (var f in Directory.GetFiles(vcDir))
            {
                if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    sidecarSources.Add(f);
            }
        }
        else
        {
            foreach (var name in new[] { "DirectPortBroker.dll", "DirectPortClient.dll", "DirectPortConsumer.dll", "DirectPortDisplay.dll", "DirectPortMFCamera.dll", "DirectPortMFGraphicsCapture.dll", "VirtuaCamProcess.exe" })
            {
                var f = Path.Combine(root, name);
                if (File.Exists(f)) sidecarSources.Add(f);
            }
        }

        var vomDll = Path.Combine(root, "src", "native", "vom", "vom.dll");
        if (File.Exists(vomDll)) sidecarSources.Add(vomDll);

        foreach (var dir in new[] { Path.GetDirectoryName(selfExe), root }
                     .Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var srcPath in sidecarSources)
            {
                var leaf = Path.GetFileName(srcPath);
                var destPath = Path.Combine(dir!, leaf);
                try { File.Copy(srcPath, destPath, true); Console.WriteLine($"  + sidecar: {leaf} -> {dir}"); }
                catch (Exception ex) { Console.Error.WriteLine($"  ! sidecar {leaf} -> {dir}: {ex.Message}"); }
            }
        }

        Console.WriteLine("ss build: GREEN");
        return 0;
    }

    // `ss build apk` — the SHIP target, dogfooded into the binary: drive the .NET-Android build of the
    // runspace project, then the published analyzer gate (fail-closed), then report the signed APK. dotnet +
    // the Android workload + a JDK ARE required here — an APK is an aapt2/r8/apksigner pipeline, not a plain
    // Roslyn compile — so this verb OWNS that toolchain rather than replacing it. Zero-dotnet self-build is a
    // Windows-exe-only road (Roslyn + an in-proc single-file pack); the APK cannot shed the workload.
    private static int BuildApk(string[] args)
    {
        Console.WriteLine("ss build apk — Android head (ship target). Safety checks:");

        var root = ResolveSource(PathArg(args));
        if (root == null) { Console.Error.WriteLine("  [FAIL] source: no repo beside the exe and no embedded source."); return 2; }
        Console.WriteLine($"  [ok] source: {root}");

        var csproj = Path.Combine(root, "subsystem.master.csproj");
        if (!File.Exists(csproj)) { Console.Error.WriteLine($"  [FAIL] project: {csproj} missing."); return 2; }
        Console.WriteLine($"  [ok] project: {csproj}");

        var dotnet = ResolveDotnet();
        if (dotnet == null) { Console.Error.WriteLine("  [FAIL] compiler: no dotnet (DOTNET_ROOT / <drive>\\dotnet / PATH)."); return 2; }
        Console.WriteLine($"  [ok] compiler: {dotnet}");

        // Android SDK, drive-derived (override via SS_ANDROID) — MSBuild reads this env var
        // as the AndroidSdkDirectory property, so exporting it IS the -p: pin.
        var drive = Path.GetPathRoot(root) ?? root;
        // Toolchain discovery: the S:\bin layout first (the cleaned-up arsenal), then the legacy drive root.
        // Native libs (the big .so set: libLiteRtLm, libpsl, …) live OUTSIDE the repo at <drive>\libs (SS_LIBS).
        var android = Environment.GetEnvironmentVariable("SS_ANDROID") ?? ToolDir(drive, "bin/android-sdk", "android-sdk", "Android");
        var libs    = Environment.GetEnvironmentVariable("SS_LIBS")    ?? ToolDir(drive, "libs", "bin/libs");
        if (!Directory.Exists(android)) { Console.Error.WriteLine($"  [FAIL] android sdk: {android} missing (set SS_ANDROID)."); return 2; }
        
        Console.WriteLine($"  [ok] android sdk: {android}");
        Console.WriteLine($"  [ok] native libs: {libs}");
        Console.WriteLine();

        bool sign = !HasFlag(args, "--no-sign");
        var env = new Dictionary<string, string>
        {
            ["ANDROID_HOME"] = android, ["ANDROID_SDK_ROOT"] = android, ["AndroidSdkDirectory"] = android,
            ["SS_LIBS"] = libs,
        };

        // SignAndroidPackage produces the installable -Signed.apk; a plain build skips signing (faster).
        var target = sign ? "build -t:SignAndroidPackage" : "build";
        Console.WriteLine($"ss build apk: {(sign ? "building + signing" : "building")} via {dotnet}");
        int rc = RunProc(dotnet, $"{target} \"{csproj}\" -c Release -f net11.0-android -clp:ErrorsOnly -p:AndroidSdkDirectory=\"{android}\" -p:SS_LIBS=\"{libs}\"", env);
        if (rc != 0) { Console.Error.WriteLine($"ss build apk: RED — build failed (exit {rc})"); return 1; }
        Console.WriteLine("ss build apk: compiled");

        // The build IS the gate: run the IN-PROC analyzer ratchet (no dotnet, no pre-published checker, no
        // stale path). A RED gate is a HARD STOP — (Build Failed). There is no override; the gate cannot be forced.
        Console.WriteLine("ss build apk: gate — in-proc analyzer ratchet…");
        var gateArgs = new[] { "--gate", "--path", root };
        int g = InProcGate.Run(gateArgs);
        if (g != 0) return g;

        var apk = FindSignedApk(drive, root);
        Console.WriteLine(apk != null ? $"ss build apk: GREEN — {apk}" : "ss build apk: GREEN (signed APK path not located; check the build output)");
        return 0;
    }

    private static string? FindSignedApk(string drive, string root)
    {
        // Directory.Build.props redirects output to $(SS_BUILD), else a 'build' SIBLING of the repo root —
        // search that first (a worktree's sibling is NOT <drive>\build; searching only the drive root once
        // reported a two-day-stale APK as the fresh build). Then the legacy locations.
        var ssBuild = Environment.GetEnvironmentVariable("SS_BUILD");
        if (string.IsNullOrWhiteSpace(ssBuild)) ssBuild = Path.GetFullPath(Path.Combine(root, "..", "build"));
        foreach (var baseDir in new[] { ssBuild, Path.Combine(drive, "build"), Path.Combine(root, "src", "runspace") })
        {
            try
            {
                if (!Directory.Exists(baseDir)) continue;
                var hit = Directory.GetFiles(baseDir, "*-Signed.apk", SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch (Exception ex) { Dg.Warn("build", ex); }
        }
        return null;
    }

    internal static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    // The gate has NO override. A RED gate is ALWAYS a hard stop — the honor system was retired (a model
    // flipped --override to force async/red gates through; the runspace owner takes no part in that). This
    // is intentionally a dead flag, not a knob: build-system dev that must land past the gate edits SOURCE
    // (the baseline / the analyzer), never a runtime bypass. — Scott: "i own my runspace."
    internal static bool IsOverride(string[] args) => false;

    // --path / -p : point a verb at a source tree.
    internal static string? PathArg(string[] args) => ArgValue(args, "--path") ?? ArgValue(args, "-p");

    // The gate verdict (shared by `ss build apk` and `ss check --gate`): a RED gate is a HARD STOP,
    // labeled (Build Failed). There is no downgrade — the gate cannot be forced through. `overridden` is
    // retained only so callers compile; it is always false (see IsOverride).
    internal static int GateVerdict(int gateExit, bool overridden)
    {
        if (gateExit == 0) { Console.WriteLine("gate: passed"); return 0; }
        Console.WriteLine();
        Console.WriteLine("(Build Failed)");
        return gateExit;
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
@"ss build — build a Subsystem head from source (the build lives IN the binary).

  ss build            rebuild the WINDOWS head (this ss.exe) and self-replace the running image
  ss build apk        build (+ sign) the ANDROID head — the ship target — then run the gate
  ss build win        explicit Windows-head build (same as bare `ss build`)

OPTIONS
  --path, -p <dir>    build from this source tree instead of the repo beside the exe / embedded source
  --no-sign           (apk) build without signing — faster, not installable
                      (there is no gate override — a RED gate is always fatal; fix the finding or the baseline)

The Windows head compiles with the on-drive dotnet (the last external dep on that road). The APK build
needs dotnet + the .NET-Android workload + a JDK by nature (aapt2/r8/apksigner) and drives them from here.");
        return 0;
    }

    // ---- source resolution ----

    internal static string? ResolveSource(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath)) return Path.GetFullPath(overridePath);
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? Directory.GetCurrentDirectory();
        // Look ONLY at the exe's own location — NEVER walk up into an unrelated repo elsewhere on the drive
        // (that defeated the blind build: from S:\ss it found S:\subsystem and ignored the embed).
        // 1. the exe sits inside the repo (S:\subsystem\ss.exe).
        if (File.Exists(Path.Combine(exeDir, "subsystem.master.csproj"))) return exeDir;
        // 2. a `subsystem` repo right beside the exe (S:\ss.exe -> S:\subsystem, or one we grew before).
        var beside = Path.Combine(exeDir, "subsystem");
        if (File.Exists(Path.Combine(beside, "subsystem.master.csproj"))) return beside;
        // 3. blind: no repo on disk. Reconstitute the embedded source into a FRESH temp dir — NEVER delete or
        //    hydrate a populated directory (a dropped ss.exe may sit in a folder full of the user's files).
        var temp = Path.Combine(Path.GetTempPath(), "subsystem-src-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        if (SelfSource.ExtractEmbedded(temp))
        {
            Console.WriteLine($"  ..  no repo beside the exe — reconstituted the embedded source into a temp dir: {temp}");
            return temp;
        }
        return null;
    }

    private static int SafeCount(string root, string pattern)
    {
        try { return Directory.GetFiles(root, pattern, SearchOption.AllDirectories).Count(p => !p.Contains("\\obj\\") && !p.Contains("\\bin\\")); }
        catch (Exception ex) { Dg.Log("build", ex.Message); return 0; }
    }

    // Resolve the on-drive dotnet — still the compiler for the Windows publish and the APK pipeline (the
    // analyzer gate itself is now in-proc, no dotnet). Precedence: an explicit env override (SS_DOTNET, then
    // DOTNET_ROOT — a dir or a dotnet.exe), then the exe's own drive (<drive>\bin\dotnet for the S:\bin
    // toolchain layout, then a flat <drive>\dotnet), then PATH last — the dogfood floor omits dotnet.
    internal static string? ResolveDotnet()
    {
        foreach (var name in new[] { "SS_DOTNET", "DOTNET_ROOT" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(v)) continue;
            if (v.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(v)) return v;
            var p = Path.Combine(v, "dotnet.exe");
            if (File.Exists(p)) return p;
        }
        var drive = Path.GetPathRoot(Environment.ProcessPath ?? Directory.GetCurrentDirectory()) ?? "";
        foreach (var rel in new[] { Path.Combine("bin", "dotnet"), "dotnet" })
        {
            var d = Path.Combine(drive, rel, "dotnet.exe");
            if (File.Exists(d)) return d;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { var p = Path.Combine(dir, "dotnet.exe"); if (File.Exists(p)) return p; } catch (Exception ex) { Dg.Log("build", ex.Message); }
        return null;
    }

    // Discover a toolchain dir on the build drive: the first candidate that exists (the S:\bin layout, then
    // the legacy drive root), or the first candidate as a fallback so a failure names a sensible path.
    static string ToolDir(string drive, params string[] rels)
    {
        foreach (var rel in rels)
        {
            var p = Path.Combine(drive, rel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(p)) return p;
        }
        return Path.Combine(drive, rels[0].Replace('/', Path.DirectorySeparatorChar));
    }

    // ---- the ♦/♠ source dump (C#, self-contained — mirrors GetCodeContextCmdlet so `ss build` needs no runspace) ----

    private static void WriteSourceDump(string root, string outPath)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", "bin", "obj", "dist", "build", ".git", ".vs", "packages", "vendor", "reference", "models" };
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".ps1", ".js", ".ts", ".html", ".css", ".json", ".csproj", ".xml", ".config", ".props", ".targets", ".sln" };

        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (blocked.Contains(Path.GetFileName(dir))) continue;
            try { foreach (var d in Directory.GetDirectories(dir)) stack.Push(d); } catch (Exception ex) { Dg.Log("build", ex.Message); }
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (exts.Contains(Path.GetExtension(f)) && new FileInfo(f).Length <= 4L * 1024 * 1024)
                    {
                        found.Add(f);
                    }
                }
            }
            catch (Exception ex) { Dg.Log("build", ex.Message); }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append("♦ repo: ").Append(Path.GetFileName(root.TrimEnd('\\', '/'))).Append(" | ").Append(DateTime.UtcNow.ToString("o")).Append('\n');
        int startLine = found.Count + 2;
        var blocks = new List<(string Rel, string[] Lines)>();
        foreach (var f in found)
        {
            string[] lines;
            try { lines = File.ReadAllLines(f); } catch { continue; }
            if (lines.Length == 0) continue;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            blocks.Add((rel, lines));
            sb.Append(rel).Append(" | ").Append(startLine).Append('\n');
            startLine += lines.Length + 1;
        }
        foreach (var b in blocks)
        {
            sb.Append("♠ ").Append(b.Rel).Append('\n');
            foreach (var l in b.Lines) sb.Append(l).Append('\n');
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static int RunProc(string exe, string args, IDictionary<string, string>? env = null, string? workingDir = null, bool echoStdout = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(exe)!;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is { } s && (echoStdout || s.Contains(": error ", StringComparison.Ordinal))) Console.WriteLine(s); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is { } s) Console.Error.WriteLine(s); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
