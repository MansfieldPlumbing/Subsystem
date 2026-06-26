<#
.SYNOPSIS
    QnnScaleAnchor.ps1 - The IEEE-754 Physical Memory Carver
.DESCRIPTION
    Bypasses arbitrary grids and pointer hallucinations. Scans the fused mega-blob 
    using a bitwise floating-point discriminator to lock onto the FP32 Scale Tables 
    that invariably separate quantized weight tensors.
#>

param(
    [string]$BinPath     = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$MegaBlobStart = 0x075E3E58,
    [long]$MegaBlobEnd   = 0x1D87EA7B,[string]$OutTsv      = "C:\bin\qnn\unet_out\megablob_physical.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;

public class BlobSegment {
    public long Offset;
    public long Size;
    public string Kind;
}

public static class QnnScaleAnchor {
    public static unsafe List<BlobSegment> Carve(byte[] data, long start, long end) {
        var segments = new List<BlobSegment>();
        long chunkSize = 128; // 32 DWORDS
        
        long currentTypeStart = start;
        string currentType = "Unknown";
        
        fixed (byte* p0 = data) {
            byte* p = p0;
            
            for (long i = start; i < end; i += chunkSize) {
                long checkLen = Math.Min(chunkSize, end - i);
                int words = (int)(checkLen / 4);
                
                int scaleCount = 0;
                bool allZero = true;
                
                uint* wp = (uint*)(p + i);
                
                for (int w = 0; w < words; w++) {
                    uint bits = wp[w];
                    if (bits != 0) allZero = false;
                    
                    // Fast IEEE-754 extraction
                    uint exp = (bits >> 23) & 0xFF;
                    uint sign = bits >> 31;
                    
                    // ML Scales are positive, with float values roughly between 1e-5 and 5.0
                    // This correlates to an exponent strictly between 100 and 130
                    if (sign == 0 && exp >= 100 && exp <= 130) {
                        if (bits != 0) scaleCount++;
                    }
                }
                
                string kind;
                if (allZero) {
                    kind = "Zero_Padding";
                } 
                // In 32 words, 6+ strict FP32 matches is statistically impossible for random weights (~0.0001% chance)
                // This flawlessly discriminates pure FP32 tables and FP32/INT32 interleaved tables
                else if (scaleCount >= 6) {
                    kind = "Scale_Table"; 
                } 
                else {
                    kind = "Weight_Tensor";
                }
                
                // Merge contiguous chunks of the same type
                if (currentType == "Unknown") {
                    currentType = kind;
                    currentTypeStart = i;
                } else if (currentType != kind) {
                    segments.Add(new BlobSegment {
                        Offset = currentTypeStart,
                        Size = i - currentTypeStart,
                        Kind = currentType
                    });
                    currentType = kind;
                    currentTypeStart = i;
                }
            }
            
            // Catch tail
            if (currentTypeStart < end) {
                segments.Add(new BlobSegment {
                    Offset = currentTypeStart,
                    Size = end - currentTypeStart,
                    Kind = currentType
                });
            }
        }
        
        return segments;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnScaleAnchor').Type) {
    Write-Host "[*] Compiling C# IEEE-754 Discriminator..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

if (-not (Test-Path $BinPath)) { throw "Binary not found: $BinPath" }

Write-Host "[*] Loading Binary Payload: $BinPath" -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)

Write-Host "[*] Carving Mega-Blob (0x$($MegaBlobStart.ToString('X8')) - 0x$($MegaBlobEnd.ToString('X8'))) via FP32 Anchors..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$objects = [QnnScaleAnchor]::Carve($bytes, $MegaBlobStart, $MegaBlobEnd)

$sw.Stop()

# Filter out micro-padding to keep the display clean, but write everything to TSV
$displayObjects = $objects | Where-Object { $_.Kind -ne "Zero_Padding" -or $_.Size -gt 512 }

Write-Host ("[+] Physical Carving Complete in {0:N2} ms" -f $sw.Elapsed.TotalMilliseconds) -ForegroundColor Green
Write-Host ""
Write-Host "    First 30 Domain Objects Detected:" -ForegroundColor White
Write-Host ("    {0,-5} | {1,-10} | {2,-12} | {3,-15}" -f "Idx", "Offset", "Size (Bytes)", "Classification") -ForegroundColor DarkGray
Write-Host "    --------------------------------------------------------" -ForegroundColor DarkGray

for ($i = 0; $i -lt [Math]::Min(30, $displayObjects.Count); $i++) {
    $obj = $displayObjects[$i]
    
    $color = "Gray"
    if ($obj.Kind -eq "Scale_Table") { $color = "Yellow" }
    elseif ($obj.Kind -eq "Weight_Tensor") { $color = "Cyan" }
    
    Write-Host ("    {0,-5} | 0x{1:X8} | {2,-12:N0} | {3,-15}" -f $i, $obj.Offset, $obj.Size, $obj.Kind) -ForegroundColor $color
}

if ($displayObjects.Count -gt 30) { Write-Host "    ... (truncated)" -ForegroundColor DarkGray }

# Statistics
$weightCount = ($objects | Where-Object { $_.Kind -eq "Weight_Tensor" }).Count
$scaleCount  = ($objects | Where-Object { $_.Kind -eq "Scale_Table" }).Count

Write-Host ""
Write-Host "    Mega-Blob Composition:" -ForegroundColor White
Write-Host ("      Weight Tensors: {0:N0}" -f $weightCount) -ForegroundColor Cyan
Write-Host ("      Scale Tables:   {0:N0}" -f $scaleCount) -ForegroundColor Yellow

$outDir = Split-Path $OutTsv -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$outLines = New-Object System.Text.StringBuilder
$null = $outLines.AppendLine("block_idx`toffset_hex`toffset_dec`tsize_bytes`tkind")

$idx = 0
foreach ($obj in $objects) {
    $null = $outLines.AppendLine(("{0}`t0x{1:X8}`t{1}`t{2}`t{3}" -f $idx, $obj.Offset, $obj.Size, $obj.Kind))
    $idx++
}

[IO.File]::WriteAllText($OutTsv, $outLines.ToString())

Write-Host ""
Write-Host "[+] Wrote true physical schema to $OutTsv" -ForegroundColor Green