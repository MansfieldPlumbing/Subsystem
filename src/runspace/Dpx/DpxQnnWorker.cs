using System;
using System.Diagnostics;
using System.IO;

namespace Subsystem.Dpx
{
    // DpxQnnWorker — spawns the Hexagon NPU work as a CHILD PROCESS rather than dlopen'ing libQnnHtp.so
    // in-process. Confirmed on-device (razr, v73, 2026-07-02): in-process dlopen runs inside Android's app
    // JNI linker namespace (clns-9), which refuses libQnnHtp.so's transitive vendor/system dependencies
    // (libcdsprpc.so -> libhidlbase.so) regardless of what the APK bundles — the chain is unbounded from
    // inside that namespace. A separately EXEC'D process gets the standard system linker instead and
    // reaches those libraries directly; this is how LocalDream's own QNN backend works (a spawned worker,
    // never JNI-loaded). Proven bit-exact: backend/context/graph create -> HTP prepare ON-DEVICE ->
    // execute -> max|rel|=0.00000 vs the CPU reference.
    //
    // The worker binary (libqnnworker.so — named .so so the installer extracts it into
    // ApplicationInfo.NativeLibraryDir WITH exec permission; /data/local/tmp and most app-data paths are
    // noexec on modern Android, this directory is the one exemption) lives beside every other bundled QNN
    // .so, so LD_LIBRARY_PATH/ADSP_LIBRARY_PATH pointed at that one directory resolves the whole chain
    // consistently (same SDK build for every library — a version-skewed mix was the wall on the FIRST
    // on-device retest this session).
    public static class DpxQnnWorker
    {
        // Runs the worker's built-in MatMul proof (backendLib relative to nativeLibraryDir, e.g.
        // "libQnnHtp.so"; outputBinName is the emitted HTP context-binary file, written into workingDir).
        // Returns the worker's stdout (the PASS/FAIL verdict line + the numeric receipt) — parsed by the
        // caller, never trusted blind (the exit code is the actual verdict).
        public static string RunProof(string nativeLibraryDir, string workingDir, string backendLib = "libQnnHtp.so", string outputBinName = "qnnworker_out.bin")
        {
            string worker = Path.Combine(nativeLibraryDir, "libqnnworker.so");
            if (!File.Exists(worker)) return "DpxQnnWorker: libqnnworker.so not bundled (SS_QNN/S:\\qairt absent at build time)";

            var psi = new ProcessStartInfo
            {
                FileName = worker,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(backendLib);
            psi.ArgumentList.Add(outputBinName);
            // Every QNN .so (backend, prepare, system, the v73 stub+skel) and the worker itself live in the
            // SAME extracted directory — one consistent SDK build, no version-mismatch surface.
            psi.Environment["LD_LIBRARY_PATH"] = nativeLibraryDir;
            // ADSP_LIBRARY_PATH is the DSP-SIDE loader's search list (fastRPC resolves the Hexagon skel + its
            // dependencies on the cDSP against this). It MUST include not just our bundled skel dir but the
            // VENDOR DSP runtime paths — the skel depends on on-device DSP base libs under
            // /vendor/lib/rfsa/adsp (confirmed present on the S23). Pointing it at ONLY nativeLibraryDir is
            // what makes the skel fail to load ("Failed to create transport / Failed to load skel, 4000"):
            // the DSP finds our skel but can't resolve its deps. A correctly-configured backend (LocalDream's)
            // lists both. Non-existent entries are harmlessly skipped by the DSP loader.
            psi.Environment["ADSP_LIBRARY_PATH"] =
                nativeLibraryDir + ";/vendor/lib/rfsa/adsp;/vendor/dsp/cdsp;/system/lib/rfsa/adsp;/dsp";

            using var proc = Process.Start(psi);
            if (proc == null) return "DpxQnnWorker: Process.Start returned null";
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            return $"DpxQnnWorker: exit={proc.ExitCode}\n--- stdout ---\n{stdout}--- stderr ---\n{stderr}";
        }
    }
}
