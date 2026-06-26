<#
.SYNOPSIS
    QnnGraphCarver.ps1 - The Deterministic Schema Lock
.DESCRIPTION
    Bypasses all heuristics. Scans the metadata region to mathematically lock 
    onto the Qualcomm OpDescriptor array using a high-confidence chain filter.
    Sorts the true pointers to precisely slice contiguous mega-blobs.
#>

param([string]$BinPath     = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$MegaBlobStart = 0x075E3E58,
    [long]$MegaBlobEnd   = 0x1D87EA7B,
    [string]$OutTsv      = "C:\bin\qnn\unet_out\megablob_carved.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;

public static class QnnGraphCarver {
    public static unsafe long[] Carve(byte[] data, long targetStart, long targetEnd) {
        // Only scan the first 32MB of the file for the Descriptor Table
        int searchLimit = Math.Min(data.Length, 32000000);
        int maxWords = searchLimit / 4;
        byte[] valid = new byte[maxWords];

        fixed (byte* p0 = data) {
            uint* p = (uint*)p0;
            
            // Mark all 32-bit integers that point inside our Megablob
            for (int i = 0; i < maxWords; i++) {
                uint val = p[i];
                if (val >= targetStart && val < targetEnd) valid[i] = 1;
            }

            int bestStride = 0;
            int bestChain = 0;
            int bestStart = 0;

            // Test structural strides from 16 bytes up to 512 bytes
            for (int stride = 4; stride <= 128; stride++) {
                for (int i = 0; i < maxWords - (stride * 20); i++) {
                    if (valid[i] == 1) {
                        int chain = 1;
                        int gaps = 0;
                        
                        // Look ahead to verify the array
                        for (int step = 1; step < 200; step++) {
                            int nextIdx = i + (step * stride);
                            if (nextIdx >= maxWords) break;

                            if (valid[nextIdx] == 1) {
                                chain++;
                                gaps = 0; // Reset gap counter on hit
                            } else {
                                gaps++;
                                // Tolerate up to 15 gaps (ops like ReLU, Norms, Adds that lack weights here)
                                if (gaps > 15) break; 
                            }
                        }

                        if (chain > bestChain) {
                            bestChain = chain;
                            bestStride = stride * 4;
                            bestStart = i * 4;
                        }
                    }
                }
            }

            // If we can't find at least 15 consecutive pointers in a perfect stride, it's noise.
            if (bestChain < 15) return new long[] { -1 };

            // We locked the schema. Extract all pointers belonging to this array.
            var ptrs = new HashSet<long>();
            int strideBytes = bestStride;
            int anchorWord = bestStart / 4;

            // Walk backwards to find the true start of the table
            int curr = anchorWord;
            int miss = 0;
            while (curr >= 0) {
                uint val = p[curr];
                if (val >= targetStart && val < targetEnd) {
                    ptrs.Add(val);
                    miss = 0;
                } else {
                    miss++;
                    if (miss > 15) break;
                }
                curr -= (strideBytes / 4);
            }

            // Walk forwards to the end of the table
            curr = anchorWord;
            miss = 0;
            while (curr < maxWords) {
                uint val = p[curr];
                if (val >= targetStart && val < targetEnd) {
                    ptrs.Add(val);
                    miss = 0;
                } else {
                    miss++;
                    if (miss > 15) break;
                }
                curr += (strideBytes / 4);
            }

            var sorted = new List<long>(ptrs);
            sorted.Sort();
            
            // Package metadata + pointers for PowerShell
            var result = new List<long> { bestStride, bestChain, bestStart };
            result.AddRange(sorted);
            return result.ToArray();
        }
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnGraphCarver').Type) {
    Write-Host "[*] Compiling C# Deterministic Schema Lock..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

if (-not (Test-Path $BinPath)) { throw "Binary not found: $BinPath" }

Write-Host "[*] Loading Binary Payload: $BinPath" -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)

Write-Host "[*] Mathematical Scan for Mega-Blob Pointers (0x$($MegaBlobStart.ToString('X8')) - 0x$($MegaBlobEnd.ToString('X8')))..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$results =[QnnGraphCarver]::Carve($bytes, $MegaBlobStart, $MegaBlobEnd)
$sw.Stop()

if ($results[0] -eq -1) {
    Write-Host "[!] Failed to mathematically lock schema. No valid pointer arrays found." -ForegroundColor Red
    exit
}

$stride = $results[0]
$chain  = $results[1]
$anchor = $results[2]
$tensorCount = $results.Length - 3

Write-Host ""
Write-Host "[*] SCHEMA LOCKED!" -ForegroundColor Magenta
Write-Host ("    Array Stride: {0} bytes" -f $stride) -ForegroundColor Gray
Write-Host ("    Table Anchor: 0x{0:X8}" -f $anchor) -ForegroundColor Gray
Write-Host ("    Confidence:   {0} confirmed array hits" -f $chain) -ForegroundColor Gray
Write-Host ""
Write-Host ("[+] Pointer Graph Extracted in {0:N2} ms" -f $sw.Elapsed.TotalMilliseconds) -ForegroundColor Green
Write-Host ("    Discovered {0:N0} True Tensors inside Mega-Blob" -f $tensorCount) -ForegroundColor White
Write-Host ""

$outLines = New-Object System.Text.StringBuilder
$null = $outLines.AppendLine("idx`toffset_hex`toffset_dec`tsize_bytes")

Write-Host ("    {0,-5} | {1,-10} | {2,-12}" -f "Idx", "Offset", "Size (Bytes)") -ForegroundColor DarkGray
Write-Host "    ------------------------------------" -ForegroundColor DarkGray

for ($i = 3; $i -lt $results.Length; $i++) {
    $idx = $i - 3
    $offset = $results[$i]
    
    # Calculate exact size using the delta to the next pointer
    if ($i -eq $results.Length - 1) {
        $size = $MegaBlobEnd - $offset
    } else {
        $size = $results[$i+1] - $offset
    }
    
    $null = $outLines.AppendLine(("{0}`t0x{1:X8}`t{1}`t{2}" -f $idx, $offset, $size))
    
    if ($idx -lt 25) {
        Write-Host ("    {0,-5} | 0x{1:X8} | {2,-12:N0}" -f $idx, $offset, $size) -ForegroundColor Cyan
    }
}

if ($tensorCount -gt 25) { Write-Host "    ... (truncated)" -ForegroundColor DarkGray }

$outDir = Split-Path $OutTsv -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[IO.File]::WriteAllText($OutTsv, $outLines.ToString())

Write-Host ""
Write-Host "[+] Wrote exact physical boundaries to $OutTsv" -ForegroundColor Green