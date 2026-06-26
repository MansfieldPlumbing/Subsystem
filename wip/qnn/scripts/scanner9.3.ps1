<#
.SYNOPSIS
    QnnStructHarvester.ps1 - The FlatBuffer Struct Harvester
.DESCRIPTION
    Bypasses arbitrary grids and arrays. Uses the exact 20-byte OpDescriptor 
    struct signature identified via Known-Plaintext Attack to harvest every 
    absolute pointer from the FlatBuffer metadata header.
#>

param(
    [string]$BinPath     = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$MegaBlobStart = 0x075E3E58,
    [long]$MegaBlobEnd   = 0x1D87EA7B,
    [string]$OutTsv      = "C:\bin\qnn\unet_out\megablob_tensors.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;
using System.Linq;

public class TensorDesc {
    public uint Pointer;
    public uint LogicalSize;
    public uint TypeFlag;
}

public static class QnnStructHarvester {
    public static unsafe TensorDesc[] Harvest(byte[] data, uint targetStart, uint targetEnd) {
        // Only scan the 12MB Flatbuffer Metadata Header
        int limit = Math.Min(data.Length, 12000000); 
        var descriptors = new Dictionary<uint, TensorDesc>();
        
        fixed (byte* p0 = data) {
            uint* p = (uint*)p0;
            int maxWords = limit / 4;
            
            // Scan for the exact 20-byte signature from the Plaintext Sniper hit
            for (int i = 1; i < maxWords - 3; i++) {
                uint ptr = p[i];
                
                // 1. Strict Pointer Bound Check & Alignment (FlatBuffer objects are 8-byte aligned)
                if (ptr >= targetStart && ptr < targetEnd && (ptr % 8 == 0)) {
                    uint typeFlag = p[i - 1];
                    uint size = p[i + 3];
                    
                    // 2. Strict Signature Filter
                    // Type Enum must be a small byte (usually 3 for weights). 
                    // Size must be a plausible tensor byte count.
                    if (typeFlag < 256 && size > 64 && size < (targetEnd - targetStart)) {
                        
                        // Valid Struct Locked. (Deduplicate overlapping vtables)
                        if (!descriptors.ContainsKey(ptr)) {
                            descriptors[ptr] = new TensorDesc { Pointer = ptr, LogicalSize = size, TypeFlag = typeFlag };
                        } else if (size > descriptors[ptr].LogicalSize) {
                            descriptors[ptr].LogicalSize = size;
                        }
                    }
                }
            }
        }
        
        var list = descriptors.Values.ToList();
        list.Sort((a, b) => a.Pointer.CompareTo(b.Pointer));
        return list.ToArray();
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnStructHarvester').Type) {
    Write-Host "[*] Compiling C# Struct Harvester..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

Write-Host "[*] Loading Binary Payload: $BinPath" -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)

Write-Host "[*] Harvesting FlatBuffer Pointers for Mega-Blob (0x$($MegaBlobStart.ToString('X8')) - 0x$($MegaBlobEnd.ToString('X8')))..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$tensors =[QnnStructHarvester]::Harvest($bytes, $MegaBlobStart, $MegaBlobEnd)
$sw.Stop()

Write-Host ("[+] Harvester Finished in {0:N2} ms" -f $sw.Elapsed.TotalMilliseconds) -ForegroundColor Green
Write-Host ("    Extracted {0:N0} True Tensors from Mega-Blob" -f $tensors.Length) -ForegroundColor White
Write-Host ""

$outLines = New-Object System.Text.StringBuilder
$null = $outLines.AppendLine("idx`tptr_hex`tptr_dec`tlogical_size`tphysical_padded_size")

Write-Host ("    {0,-5} | {1,-10} | {2,-13} | {3,-15}" -f "Idx", "Pointer", "Logical Size", "Physical Padded Size") -ForegroundColor DarkGray
Write-Host "    --------------------------------------------------------" -ForegroundColor DarkGray

for ($i = 0; $i -lt $tensors.Length; $i++) {
    $t = $tensors[$i]
    
    # The true physical size in the binary is the distance to the next pointer
    if ($i -eq $tensors.Length - 1) {
        $physicalSize = $MegaBlobEnd - $t.Pointer
    } else {
        $physicalSize = $tensors[$i+1].Pointer - $t.Pointer
    }
    
    $null = $outLines.AppendLine(("{0}`t0x{1:X8}`t{1}`t{2}`t{3}" -f $i, $t.Pointer, $t.LogicalSize, $physicalSize))
    
    if ($i -lt 30) {
        Write-Host ("    {0,-5} | 0x{1:X8} | {2,-13:N0} | {3,-15:N0}" -f $i, $t.Pointer, $t.LogicalSize, $physicalSize) -ForegroundColor Cyan
    }
}

if ($tensors.Length -gt 30) { Write-Host "    ... (truncated)" -ForegroundColor DarkGray }

$outDir = Split-Path $OutTsv -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[IO.File]::WriteAllText($OutTsv, $outLines.ToString())

Write-Host ""
Write-Host "[+] Wrote true tensor map to $OutTsv" -ForegroundColor Green