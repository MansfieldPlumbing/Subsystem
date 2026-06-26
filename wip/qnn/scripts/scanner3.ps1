<#
.SYNOPSIS
    QnnObjectGraph.ps1 - True Object-Based Memory Layout Carver
.DESCRIPTION
    Bypasses analog entropy/padding heuristics entirely. Scans the QNN 
    Metadata/OpTable region to extract the actual absolute memory pointers 
    laid down by the Qualcomm compiler.
    
    It then reconstructs the tensor bounding boxes inside packed mega-blobs 
    by sorting the pointer graph, extracting the exact byte sizes of every 
    un-padded operation.
#>

param([string]$BinPath = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$MegaBlobStart = 0x075E3E58,
    [long]$MegaBlobEnd   = 0x1D87EA7B,
    [string]$OutTsv = "C:\bin\qnn\unet_out\megablob_objects.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;
using System.Linq;

public static class QnnObjectGraph {
    public static unsafe List<long> DiscoverStructPointers(byte[] data, long targetStart, long targetEnd) {
        var pointers = new HashSet<long>();
        
        // Scan the metadata header region (everything before the mega-blob starts)
        long searchLimit = targetStart; 
        long searchWords = searchLimit / 4;
        
        bool[] isValidIndex = new bool[searchWords];
        var validIndexList = new List<long>();
        
        fixed (byte* p0 = data) {
            uint* p = (uint*)p0;
            
            for (long i = 0; i < searchWords; i++) {
                uint val = p[i];
                // Check if the 32-bit uint points into the mega-blob with 8-byte alignment
                if (val >= targetStart && val < targetEnd && val % 8 == 0) {
                    isValidIndex[i] = true;
                    validIndexList.Add(i);
                }
            }
            
            // Structural noise filter: True object pointers belong to an array of structs.
            // We verify that the pointer is part of a periodic structural stride.
            foreach (long idx in validIndexList) {
                bool hasStride = false;
                
                // Test structural strides from 8 bytes up to 512 bytes
                for (int stride = 2; stride <= 128; stride++) {
                    int chain = 0;
                    // Look ahead up to 5 struct strides (allowing gaps for ReLUs/Norms that lack weights)
                    for(int step = 1; step <= 5; step++) {
                        if (idx + step * stride < searchWords && isValidIndex[idx + step * stride]) {
                            chain++;
                        }
                    }
                    // If we found at least 2 other pointers at this exact struct offset, it's a real OpTable
                    if (chain >= 2) { 
                        hasStride = true;
                        break;
                    }
                }
                
                if (hasStride) {
                    pointers.Add(p[idx]);
                }
            }
        }
        
        var sorted = pointers.ToList();
        sorted.Sort();
        return sorted;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnObjectGraph').Type) {
    Write-Host "[*] Compiling C# Pointer-Tracing Engine..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

if (-not (Test-Path $BinPath)) { throw "Binary not found: $BinPath" }

Write-Host "[*] Loading Binary Graph: $BinPath" -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)

Write-Host "[*] Tracing Struct Pointers for Mega-Blob (0x$($MegaBlobStart.ToString('X8')) - 0x$($MegaBlobEnd.ToString('X8')))..." -ForegroundColor Cyan
$sw =[System.Diagnostics.Stopwatch]::StartNew()
$pointers = [QnnObjectGraph]::DiscoverStructPointers($bytes, $MegaBlobStart, $MegaBlobEnd)

# Add the end boundary to calculate the final object size
if ($pointers.Count -gt 0) { $pointers.Add($MegaBlobEnd) }
$sw.Stop()

Write-Host ("[+] Pointer Graph Extracted in {0:N2} ms" -f $sw.Elapsed.TotalMilliseconds) -ForegroundColor Green
Write-Host ("    Discovered {0:N0} contiguous tensor objects inside the mega-blob!" -f ($pointers.Count - 1)) -ForegroundColor White
Write-Host ""

$outLines = New-Object System.Text.StringBuilder
$null = $outLines.AppendLine("block_idx`toffset_hex`toffset_dec`tsize_bytes")

for ($i = 0; $i -lt ($pointers.Count - 1); $i++) {
    $pStart = $pointers[$i]
    $pNext = $pointers[$i+1]
    $size = $pNext - $pStart
    $null = $outLines.AppendLine(("{0}`t0x{1:X8}`t{1}`t{2}" -f $i, $pStart, $size))
    
    if ($i -lt 20) {
        Write-Host ("    Object {0,4}: Offset 0x{1:X8} | Size {2,10:N0} bytes" -f $i, $pStart, $size) -ForegroundColor Gray
    }
}

if ($pointers.Count -gt 20) { Write-Host "    ... (truncated)" -ForegroundColor DarkGray }

$outDir = Split-Path $OutTsv -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[IO.File]::WriteAllText($OutTsv, $outLines.ToString())

Write-Host ""
Write-Host "[+] Wrote full mega-blob object map to $OutTsv" -ForegroundColor Green
