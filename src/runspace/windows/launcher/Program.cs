using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Subsystem.Launcher;

internal class Program
{
    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        string bootJsonPath = Path.Combine(baseDir, "boot.json");

        BootConfig config = LoadOrCreateConfig(bootJsonPath);

        string activeSlot = config.ActiveSlot.Trim().ToUpperInvariant();
        string activePath = activeSlot == "B" ? config.SlotB : config.SlotA;
        string backupSlot = activeSlot == "B" ? "A" : "B";
        string backupPath = activeSlot == "B" ? config.SlotA : config.SlotB;

        // Resolve absolute paths
        string activeFullPath = Path.GetFullPath(Path.Combine(baseDir, activePath));
        string backupFullPath = Path.GetFullPath(Path.Combine(baseDir, backupPath));

        try
        {
            return ExecutePayload(activeFullPath, args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[BOOT FAIL] Active slot {activeSlot} ({activePath}) failed to execute.");
            Console.Error.WriteLine($"  Error: {ex.Message}");
            Console.Error.WriteLine($"  Rolling back to backup slot {backupSlot} ({backupPath})...");

            // Write crash log
            try
            {
                string logPath = Path.Combine(baseDir, "boot-failure.log");
                File.WriteAllText(logPath, $"[{DateTime.UtcNow:o}] Slot {activeSlot} failed: {ex}\n");
            }
            catch (Exception writeEx)
            {
                Console.Error.WriteLine($"[WARNING] Failed to write boot failure log: {writeEx.Message}");
            }

            // Swap slot in config
            config.ActiveSlot = backupSlot;
            SaveConfig(bootJsonPath, config);

            try
            {
                return ExecutePayload(backupFullPath, args);
            }
            catch (Exception fallbackEx)
            {
                Console.Error.WriteLine($"\n[FATAL] Rollback slot {backupSlot} also failed.");
                Console.Error.WriteLine($"  Error: {fallbackEx.Message}");
                return -1;
            }
        }
    }

    private static int ExecutePayload(string dllPath, string[] args)
    {
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("Payload DLL not found.", dllPath);
        }

        // Load payload assembly
        var assembly = Assembly.LoadFrom(dllPath);
        
        // Resolve static Program.Main method
        var programType = assembly.GetType("Subsystem.Windows.Program");
        if (programType == null)
        {
            throw new TypeLoadException("Target entry type 'Subsystem.Windows.Program' not found in payload assembly.");
        }

        var mainMethod = programType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        if (mainMethod == null)
        {
            throw new EntryPointNotFoundException("Static 'Main(string[] args)' method not found in Subsystem.Windows.Program.");
        }

        // Invoke the main entry point
        object? result = mainMethod.Invoke(null, new object[] { args });
        return result is int exitCode ? exitCode : 0;
    }

    private static BootConfig LoadOrCreateConfig(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<BootConfig>(json);
                if (config != null) return config;
            }
            catch (Exception loadEx)
            {
                Console.Error.WriteLine($"[WARNING] Failed to load boot.json: {loadEx.Message}");
            }
        }

        var defaultConfig = new BootConfig
        {
            ActiveSlot = "A",
            SlotA = "payloads/SubsystemWin.A.dll",
            SlotB = "payloads/SubsystemWin.B.dll",
            StatusA = "stable",
            StatusB = "stable"
        };
        SaveConfig(path, defaultConfig);
        return defaultConfig;
    }

    private static void SaveConfig(string path, BootConfig config)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(path, json);
        }
        catch (Exception saveEx)
        {
            Console.Error.WriteLine($"[WARNING] Failed to save boot.json: {saveEx.Message}");
        }
    }

    private class BootConfig
    {
        public string ActiveSlot { get; set; } = "A";
        public string SlotA { get; set; } = "payloads/SubsystemWin.A.dll";
        public string SlotB { get; set; } = "payloads/SubsystemWin.B.dll";
        public string StatusA { get; set; } = "stable";
        public string StatusB { get; set; } = "stable";
    }
}
