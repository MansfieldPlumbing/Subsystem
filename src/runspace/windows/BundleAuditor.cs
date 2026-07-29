using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace Subsystem.Windows;

public static class BundleAuditor
{
    public static int Run()
    {
        string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        byte[] exeBytes = File.ReadAllBytes(exePath);
        BundleManifest manifest = SelfBundle.Read(exeBytes);

        Console.WriteLine("=== TOP 40 LARGEST BUNDLE FILES IN SS.EXE ===");
        Console.WriteLine("{0,-65} {1,-15} {2,12} {3,12}", "Name", "Type", "Size (KB)", "Size (MB)");
        Console.WriteLine(new string('-', 108));

        var sorted = manifest.Files.OrderByDescending(f => f.Size).ToList();

        foreach (var f in sorted.Take(40))
        {
            Console.WriteLine("{0,-65} {1,-15} {2,12:N1} {3,12:N2}",
                f.RelativePath.Length > 64 ? f.RelativePath.Substring(0, 61) + "..." : f.RelativePath,
                f.Type,
                f.Size / 1024.0,
                f.Size / (1024.0 * 1024.0));
        }

        Console.WriteLine("\n=== CATEGORY SUMMARY ===");
        Console.WriteLine("{0,-20} {1,10} {2,15}", "Type", "Count", "Total (MB)");
        Console.WriteLine(new string('-', 50));

        var grouped = sorted.GroupBy(f => f.Type);
        foreach (var g in grouped.OrderByDescending(g => g.Sum(f => f.Size)))
        {
            Console.WriteLine("{0,-20} {1,10} {2,15:N2}",
                g.Key,
                g.Count(),
                g.Sum(f => f.Size) / (1024.0 * 1024.0));
        }

        Console.WriteLine("\n=== EMBEDDED MANIFEST RESOURCES INSIDE SS.DLL ===");
        Console.WriteLine("{0,-65} {1,12} {2,12}", "Resource Name", "Size (KB)", "Size (MB)");
        Console.WriteLine(new string('-', 92));

        var asm = typeof(ObpHost).Assembly;
        var resList = asm.GetManifestResourceNames()
            .Select(n => {
                using var s = asm.GetManifestResourceStream(n);
                long len = s?.Length ?? 0;
                return new { Name = n, Size = len };
            })
            .OrderByDescending(r => r.Size)
            .ToList();

        foreach (var r in resList.Take(30))
        {
            Console.WriteLine("{0,-65} {1,12:N1} {2,12:N2}",
                r.Name.Length > 64 ? r.Name.Substring(0, 61) + "..." : r.Name,
                r.Size / 1024.0,
                r.Size / (1024.0 * 1024.0));
        }

        Console.WriteLine("\nTotal Embedded Resources in ss.dll: {0} files, {1:N2} MB",
            resList.Count, resList.Sum(r => r.Size) / (1024.0 * 1024.0));

        // Audit contents of ss-source.dump
        try
        {
            using var dumpStream = asm.GetManifestResourceStream("ss-source.dump");
            if (dumpStream != null)
            {
                using var reader = new StreamReader(dumpStream);
                string text = reader.ReadToEnd();
                var dumpBlocks = new List<(string Path, int Length)>();
                var matches = Regex.Matches(text, @"♠ ([^\r\n]+)\r?\n");
                for (int i = 0; i < matches.Count; i++)
                {
                    string path = matches[i].Groups[1].Value;
                    int start = matches[i].Index;
                    int end = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;
                    dumpBlocks.Add((path, end - start));
                }

                Console.WriteLine("\n=== TOP 25 LARGEST FILES INSIDE SS-SOURCE.DUMP ===");
                Console.WriteLine("{0,-65} {1,12} {2,12}", "Dump File Path", "Size (KB)", "Size (MB)");
                Console.WriteLine(new string('-', 92));

                foreach (var b in dumpBlocks.OrderByDescending(b => b.Length).Take(25))
                {
                    Console.WriteLine("{0,-65} {1,12:N1} {2,12:N2}",
                        b.Path.Length > 64 ? b.Path.Substring(0, 61) + "..." : b.Path,
                        b.Length / 1024.0,
                        b.Length / (1024.0 * 1024.0));
                }
                Console.WriteLine("\nTotal Dumped Files: {0}, Total Size: {1:N2} MB", dumpBlocks.Count, text.Length / (1024.0 * 1024.0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Could not audit ss-source.dump: " + ex.Message);
        }

        return 0;
    }
}
