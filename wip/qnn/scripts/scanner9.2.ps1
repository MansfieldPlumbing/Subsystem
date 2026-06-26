<#
.SYNOPSIS
    QnnAbsoluteCarver.ps1 - The Ground-Truth Object Hydrator
.DESCRIPTION
    Fuses the flawless logical boundaries from the compiler maps (weight_map.tsv)
    with the physical IEEE-754 discriminator. Zero guessing. 100% ground truth.
#>

$ErrorActionPreference = 'Stop'

# ==============================================================================
# AUTO-DISCOVERY ROUTER
# ==============================================================================
function Get-Artifact([string]$FileName) {
    $searchPaths = @(
        ".\$FileName", 
        ".\unet_out\$FileName",
        "C:\bin\qnn\$FileName",
        "C:\bin\qnn\unet_out\$FileName",
        "C:\bin\qnn\test-harness-s23-v73\$FileName"
    )
    foreach ($path in $searchPaths) {
        if (Test-Path $path) { return (Resolve-Path $path).Path }
    }
    throw "[!] Cannot find $FileName. Check your directories."
}

$BinPath = Get-Artifact "unet.bin"
$WeightMapPath = Get-Artifact "weight_map.tsv"

Write-Host "[*] Discovered Binary    : $BinPath" -ForegroundColor DarkGray
Write-Host "[*] Discovered Weight Map: $WeightMapPath" -ForegroundColor DarkGray

# ==============================================================================
# C# NATIVE HYDRATION ENGINE
# ==============================================================================
$csharpCode = @"
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Collections.Generic;
using System.Linq;

public class GraphNode {
    public string OpName;
    public long Offset;
    public long Size;
    public string PhysicalClassification;
    public double ScaleDensity;
}

public static class AbsoluteCarver {
    public static unsafe List<GraphNode> HydrateGraph(string binPath, string tsvPath) {
        var nodes = new List<GraphNode>();
        long fileSize = new FileInfo(binPath).Length;

        // Parse logical boundaries from the blueprint
        foreach (var line in File.ReadLines(tsvPath).Skip(1)) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 5) continue;
            
            nodes.Add(new GraphNode {
                Offset = Convert.ToInt64(parts[1].Replace("0x", ""), 16),
                Size = long.Parse(parts[2]),
                OpName = parts[4]
            });
        }

        // Map the physical binary and hydrate the logical nodes
        using (var mmf = MemoryMappedFile.CreateFromFile(binPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read)) {
            byte* basePtr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

            try {
                foreach (var node in nodes) {
                    if (node.Offset + node.Size > fileSize || node.Size == 0) {
                        node.PhysicalClassification = "Out_Of_Bounds";
                        continue;
                    }

                    // Strict physical inspection of true boundaries
                    byte* p = basePtr + node.Offset;
                    int words = (int)(node.Size / 4);
                    int scaleCount = 0;
                    bool allZero = true;

                    uint* wp = (uint*)p;
                    for (int w = 0; w < words; w++) {
                        uint bits = wp[w];
                        if (bits != 0) allZero = false;
                        
                        uint exp = (bits >> 23) & 0xFF;
                        uint sign = bits >> 31;
                        
                        if (sign == 0 && exp >= 100 && exp <= 130) {
                            if (bits != 0) scaleCount++;
                        }
                    }

                    node.ScaleDensity = words > 0 ? (double)scaleCount / words : 0;

                    if (allZero) {
                        node.PhysicalClassification = "Zero_Padding";
                    } 
                    else if (node.ScaleDensity > 0.50) {
                        node.PhysicalClassification = "Pure_FP32_Table";
                    }
                    else if (node.ScaleDensity > 0.05) {
                        node.PhysicalClassification = "Mixed_Scale_Tensor";
                    }
                    else {
                        node.PhysicalClassification = "Quantized_Weight_Tensor";
                    }
                }
            }
            finally {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        
        return nodes;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'AbsoluteCarver').Type) {
    Write-Host "[*] Compiling C# Absolute Carver Engine..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

# ==============================================================================
# RUN
# ==============================================================================
Write-Host "[*] Hydrating Logical Compiler Graph with Physical MMF Data..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$hydratedGraph = [AbsoluteCarver]::HydrateGraph($BinPath, $WeightMapPath)

$sw.Stop()

Write-Host ("[+] Object Hydration Complete in {0:N2} ms" -f $sw.Elapsed.TotalMilliseconds) -ForegroundColor Green
Write-Host ""
Write-Host "====================================================================================================" -ForegroundColor DarkGray
Write-Host " GROUND TRUTH GRAPH VALIDATION" -ForegroundColor White
Write-Host "====================================================================================================" -ForegroundColor DarkGray

$format = "{0,-10} | {1,-10} | {2,-25} | {3,-12} | {4}"
Write-Host ($format -f "Offset", "Size (B)", "Physical Reality", "FP32 Density", "OpName") -ForegroundColor Cyan
Write-Host ("-" * 100) -ForegroundColor DarkGray

foreach ($node in $hydratedGraph | Select-Object -First 30) {
    $color = switch ($node.PhysicalClassification) {
        "Zero_Padding"            { "DarkGray" }
        "Pure_FP32_Table"         { "Yellow" }
        "Mixed_Scale_Tensor"      { "Magenta" }
        "Quantized_Weight_Tensor" { "Green" }
        default                   { "Red" }
    }
    
    $row = $format -f ("0x{0:X8}" -f $node.Offset), 
                      $node.Size, 
                      $node.PhysicalClassification, 
                      ("{0:P1}" -f $node.ScaleDensity), 
                      $node.OpName

    Write-Host $row -ForegroundColor $color
}
# ==============================================================================
# PAYLOAD COMPOSITION REPORT
# ==============================================================================
$totalBytes = ($hydratedGraph | Measure-Object Size -Sum).Sum

Write-Host ""
Write-Host "====================================================================================================" -ForegroundColor DarkGray
Write-Host " PAYLOAD COMPOSITION SUMMARY" -ForegroundColor White
Write-Host "====================================================================================================" -ForegroundColor DarkGray

$hydratedGraph | Group-Object PhysicalClassification | Sort-Object Count -Descending | Select-Object Name, Count, 
    @{Name='Size (MB)'; Expression={"{0:N2}" -f (($_.Group | Measure-Object Size -Sum).Sum / 1MB)}},
    @{Name='Payload %'; Expression={"{0:N2}%" -f ((($_.Group | Measure-Object Size -Sum).Sum / $totalBytes) * 100)}} | Format-Table -AutoSize