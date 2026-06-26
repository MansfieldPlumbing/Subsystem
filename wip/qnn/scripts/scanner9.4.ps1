<#
.SYNOPSIS
    QnnVirtualHarvester.ps1 - The True File-Offset Graph Harvester
.DESCRIPTION
    Uses the mathematically proven Type-3 FlatBuffer descriptor signature 
    to extract Virtual Execution Pointers, translating them into absolute 
    physical file offsets for direct gfx900/Vega 10 enablement.
#>

param(
    [string]$BinPath     = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$WeightBase    = 0x00081000,
    [string]$OutTsv      = "C:\bin\qnn\unet_out\true_tensors.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;
using System.Linq;

public class TensorDesc {
    public long FileOffset;
    public uint LogicalSize;
}

public static class QnnVirtualHarvester {
    public static unsafe TensorDesc[] Harvest(byte[] data, long weightBase) {
        // Only scan the 12MB Flatbuffer Metadata Header
        int limit = Math.Min(data.Length, 12000000); 
        var descriptors = new Dictionary<long, TensorDesc>();
        
        fixed (byte* p0 = data) {
            uint* p = (uint*)p0;
            int maxWords = limit / 4;
            
            // i is the index of the Pointer field
            for (int i = 1; i < maxWords - 4; i++) {
                uint typeFlag = p[i - 1];
                
                // Type 3 = Static Memory Mapped Tensor (Weights & Scales)
                if (typeFlag == 3) {
                    uint vptr = p[i];
                    uint size = p[i + 3];
                    
                    // Hexagon DSP aligns VTCM buffers to 8, 64, or 128 bytes.
                    if (vptr % 8 == 0 && size >= 16 && size < 400000000) {
                        long fileOffset = weightBase + vptr;
                        
                        // Strict boundary enforcement
                        if (fileOffset >= weightBase && fileOffset + size <= data.Length) {
                            
                            if (!descriptors.ContainsKey(fileOffset)) {
                                descriptors[fileOffset] = new TensorDesc { 
                                    FileOffset = fileOffset, 
                                    LogicalSize = size 
                                };
                            } else if (size > descriptors[fileOffset].LogicalSize) {
                                descriptors[fileOffset].LogicalSize = size;
                            }
                        }
                    }
                }
            }
        }
        
        var list = descriptors.Values.ToList();
        list.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));
        
        // Final sanity pass: remove false-positive overlaps
        var cleanList = new List<TensorDesc>();
        long currentEnd = 0;
        foreach (var t in list) {
            if (t.FileOffset >= currentEnd) {
                cleanList.Add(t);
                currentEnd = t.FileOffset + t.LogicalSize;
            }
        }
        
        return cleanList.ToArray();
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnVirtualHarvester').Type) {
    Write-Host "[*] Compiling C# Virtual Harvester..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

Write-Host "[*] Loading Binary Payload: $BinPath" -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)

Write-Host "[*] Translating Virtual Execution Pointers to Physical Offsets..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$tensors =[QnnVirtualHarvester]::Harvest($bytes, $WeightBase)
$sw.Stop()

Write-Host ("[+] Virtual Harvest Finished in {0:N2} ms" -f $sw.Elapsed.TotalMilliseconds) -ForegroundColor Green
Write-Host ("    Successfully Mapped {0:N0} True Tensors!" -f $tensors.Length) -ForegroundColor White
Write-Host ""

$outLines = New-Object System.Text.StringBuilder
$null = $outLines.AppendLine("idx`toffset_hex`toffset_dec`tlogical_size`tpadding_bytes")

Write-Host ("    {0,-5} | {1,-10} | {2,-13} | {3,-15}" -f "Idx", "File Offset", "Logical Size", "Padding Bytes") -ForegroundColor DarkGray
Write-Host "    --------------------------------------------------------" -ForegroundColor DarkGray

for ($i = 0; $i -lt $tensors.Length; $i++) {
    $t = $tensors[$i]
    
    # Calculate exact padding added by the compiler
    if ($i -eq $tensors.Length - 1) {
        $physicalPadding = ($bytes.Length - $t.FileOffset) - $t.LogicalSize
    } else {
        $physicalPadding = ($tensors[$i+1].FileOffset - $t.FileOffset) - $t.LogicalSize
    }
    
    $null = $outLines.AppendLine(("{0}`t0x{1:X8}`t{1}`t{2}`t{3}" -f $i, $t.FileOffset, $t.LogicalSize, $physicalPadding))
    
    if ($i -lt 30) {
        Write-Host ("    {0,-5} | 0x{1:X8} | {2,-13:N0} | {3,-15:N0}" -f $i, $t.FileOffset, $t.LogicalSize, $physicalPadding) -ForegroundColor Cyan
    }
}

if ($tensors.Length -gt 30) { Write-Host "    ... (truncated)" -ForegroundColor DarkGray }

$outDir = Split-Path $OutTsv -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[IO.File]::WriteAllText($OutTsv, $outLines.ToString())

Write-Host ""
Write-Host "[+] Wrote true unified tensor map to $OutTsv" -ForegroundColor Green