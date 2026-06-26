<#
.SYNOPSIS
    scanner_qnn.ps1 - Recursive sub-blob structure scanner for QNN context binaries.
    
.DESCRIPTION
    Takes a region (offset, size) inside the QNN binary and recursively
    descends looking for sub-structure. Uses C# inline (unsafe pointers)
    for the hot paths so we don't pay PowerShell interpreter tax.
    
    Detects:
      - Alignment boundaries (256, 512, 1024, 2048, 4096 byte alignments)
      - Entropy gradients (transitions from high-entropy weight regions
        to low-entropy scale/metadata tables)
      - FP32 scale tables (bytes with high-byte in [0x3D-0x40] range,
        typical for scales between 1e-3 and 1e-1)
      - Zero-padding runs at sub-blob granularity

.PARAMETER BinPath
    Path to the QNN context binary (default: C:\bin\qnn\test-harness-s23-v73\unet.bin)

.PARAMETER StartOffset
    Byte offset to start scanning (default: 0x075E3E58, the 354MB mega-blob)

.PARAMETER Length
    Number of bytes to scan (default: 371829795, the 354MB mega-blob size)
    Pass 0 to scan from StartOffset to end of file.

.PARAMETER OutTsv
    Output manifest path (default: C:\bin\qnn\unet_out\subblobs.tsv)

.PARAMETER MinSubBlobSize
    Minimum sub-blob size to report (default: 256 bytes)
#>

param(
    [string]$BinPath = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$StartOffset = 0x075E3E58,
    [long]$Length = 371829795,
    [string]$OutTsv = "C:\bin\qnn\unet_out\subblobs.tsv",
    [int]$MinSubBlobSize = 256
)

$ErrorActionPreference = 'Stop'

# ==============================================================================
# C# UNSAFE NATIVE EXTENSIONS
# ==============================================================================
$csharpCode = @"
using System;
using System.Collections.Generic;

public class SubBlob {
    public long Offset;
    public long Size;
    public double Entropy;
    public int Depth;
    public string Kind;  // "weight_dense", "scale_table", "zero_pad", "fp32_table", "unknown"
    public long PadAfter;
}

public static class FastScanner
{
    // Shannon entropy over a byte buffer (slice of larger array)
    public static unsafe double GetEntropy(byte[] bytes, long offset, long length)
    {
        if (length == 0) return 0.0;
        int[] freq = new int[256];
        fixed (byte* p0 = bytes) {
            byte* p = p0 + offset;
            for (long i = 0; i < length; i++) freq[*p++]++;
        }
        double ent = 0.0;
        double n = (double)length;
        for (int i = 0; i < 256; i++) {
            if (freq[i] > 0) {
                double prob = freq[i] / n;
                ent -= prob * Math.Log(prob, 2);
            }
        }
        return ent;
    }
    
    // Find runs of zeros >= minRun, returns list of (start, length) inside [offset, offset+length)
    public static unsafe List<long[]> FindZeroRuns(byte[] bytes, long offset, long length, int minRun)
    {
        List<long[]> runs = new List<long[]>();
        fixed (byte* p0 = bytes) {
            byte* p = p0 + offset;
            long runStart = -1;
            long runLen = 0;
            for (long i = 0; i < length; i++) {
                if (p[i] == 0) {
                    if (runStart < 0) runStart = i;
                    runLen++;
                } else {
                    if (runLen >= minRun) runs.Add(new long[] { offset + runStart, runLen });
                    runStart = -1;
                    runLen = 0;
                }
            }
            if (runLen >= minRun) runs.Add(new long[] { offset + runStart, runLen });
        }
        return runs;
    }
    
    // Sliding-window entropy: emit one value per stride
    public static unsafe double[] SlidingEntropy(byte[] bytes, long offset, long length, int window, int stride)
    {
        long n = (length - window) / stride + 1;
        if (n <= 0) return new double[0];
        double[] result = new double[n];
        int[] freq = new int[256];
        fixed (byte* p0 = bytes) {
            byte* p = p0 + offset;
            // Initialize histogram for first window
            for (int i = 0; i < window; i++) freq[p[i]]++;
            for (long step = 0; step < n; step++) {
                double ent = 0.0;
                double w = (double)window;
                for (int i = 0; i < 256; i++) {
                    if (freq[i] > 0) {
                        double prob = freq[i] / w;
                        ent -= prob * Math.Log(prob, 2);
                    }
                }
                result[step] = ent;
                // Slide
                long nextStart = (step + 1) * stride;
                if (nextStart + window > length) break;
                for (int s = 0; s < stride; s++) {
                    freq[p[(step * stride) + s]]--;
                    freq[p[(step * stride) + s + window]]++;
                }
            }
        }
        return result;
    }
    
    // Heuristic: does this region look like a packed FP32 scale table?
    // Real scales for SD weights are typically 1e-4 to 1e-1, so the high byte
    // of each fp32 (little-endian) sits in [0x3A, 0x40] (covers ~1e-4 to ~1.0).
    public static unsafe double FP32ScaleProbability(byte[] bytes, long offset, long length)
    {
        if (length < 16 || length % 4 != 0) return 0.0;
        long count = length / 4;
        long hits = 0;
        fixed (byte* p0 = bytes) {
            byte* p = p0 + offset;
            for (long i = 0; i < count; i++) {
                byte high = p[i * 4 + 3];  // little-endian: high byte is last
                // Positive normal FP32 in [~1e-9, ~1.0]: exponent byte in [0x30, 0x3F]
                // (sign=0, exponent bits 7..23 → high byte = 0x30..0x3F for that range)
                if (high >= 0x30 && high <= 0x40) hits++;
            }
        }
        return (double)hits / (double)count;
    }
    
    // Check alignment: is offset a multiple of any standard alignment?
    public static int DetectAlignment(long offset)
    {
        if ((offset & 0xFFF) == 0) return 4096;
        if ((offset & 0x7FF) == 0) return 2048;
        if ((offset & 0x3FF) == 0) return 1024;
        if ((offset & 0x1FF) == 0) return 512;
        if ((offset & 0xFF)  == 0) return 256;
        if ((offset & 0x3F)  == 0) return 64;
        return 0;
    }
}
"@

Write-Host "[*] Compiling C# unsafe inline..." -ForegroundColor DarkGray
Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'

# ==============================================================================
# LOAD BINARY
# ==============================================================================
Write-Host "[*] Loading $BinPath..." -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)
$fileSize = $bytes.LongLength
Write-Host "    Total file size: $($fileSize.ToString('N0')) bytes ($('{0:N1}' -f ($fileSize / 1MB)) MB)" -ForegroundColor Gray

if ($Length -eq 0) { $Length = $fileSize - $StartOffset }

Write-Host "    Scan region:     0x$($StartOffset.ToString('X8')) - 0x$(($StartOffset + $Length).ToString('X8'))" -ForegroundColor Gray
Write-Host "    Region size:     $($Length.ToString('N0')) bytes ($('{0:N1}' -f ($Length / 1MB)) MB)" -ForegroundColor Gray
Write-Host ""

# ==============================================================================
# RECURSIVE DESCENT
# ==============================================================================
$global:AllSubBlobs = New-Object System.Collections.Generic.List[SubBlob]
$global:CallCount = 0

function Invoke-DescentScan {
    param(
        [long]$Offset,
        [long]$Size,
        [int]$Depth,
        [int]$MinPadRun
    )
    
    $global:CallCount++
    $indent = "  " * $Depth
    
    if ($Size -lt $MinSubBlobSize) { return }
    if ($Depth -gt 6) { return }
    
    # Compute entropy of this region
    $ent = [FastScanner]::GetEntropy($bytes, $Offset, $Size)
    
    # Find zero-runs at this min-pad threshold
    $runs = [FastScanner]::FindZeroRuns($bytes, $Offset, $Size, $MinPadRun)
    
    $align = [FastScanner]::DetectAlignment($Offset)
    
    if ($runs.Count -eq 0) {
        # Atomic sub-blob — no internal padding
        # Classify it
        $fp32Prob = [FastScanner]::FP32ScaleProbability($bytes, $Offset, $Size)
        $kind = "weight_dense"
        if ($ent -lt 1.0) { $kind = "zero_pad" }
        elseif ($ent -lt 4.0) { $kind = "scale_table" }
        elseif ($fp32Prob -gt 0.85 -and $Size -lt 16384) { $kind = "fp32_table" }
        elseif ($ent -gt 7.5) { $kind = "weight_dense" }
        
        $sb = New-Object SubBlob
        $sb.Offset = $Offset
        $sb.Size = $Size
        $sb.Entropy = [Math]::Round($ent, 3)
        $sb.Depth = $Depth
        $sb.Kind = $kind
        $sb.PadAfter = 0
        $global:AllSubBlobs.Add($sb)
        
        if ($Depth -le 2 -or $Size -gt 102400) {
            $color = switch ($kind) {
                "zero_pad"     { "DarkGray" }
                "scale_table"  { "Yellow" }
                "fp32_table"   { "Cyan" }
                "weight_dense" { "Green" }
                default        { "Gray" }
            }
            $alignStr = if ($align -gt 0) { "align=$align" } else { "" }
            Write-Host ("{0}[L{1}] 0x{2:X8} {3,12:N0}B  ent={4:N2}  fp32={5:N2}  {6,-14} {7}" -f $indent, $Depth, $Offset, $Size, $ent, $fp32Prob, $kind, $alignStr) -ForegroundColor $color
        }
        return
    }
    
    # We found sub-runs; recurse into each sub-region between runs
    $subRegions = New-Object System.Collections.Generic.List[long[]]
    $cursor = $Offset
    foreach ($run in $runs) {
        $runStart = $run[0]
        $runLen = $run[1]
        if ($runStart -gt $cursor) {
            $subRegions.Add(@($cursor, $runStart - $cursor))
        }
        $cursor = $runStart + $runLen
    }
    if ($cursor -lt $Offset + $Size) {
        $subRegions.Add(@($cursor, $Offset + $Size - $cursor))
    }
    
    if ($Depth -le 1) {
        Write-Host ("{0}[L{1}] 0x{2:X8} {3,12:N0}B  ent={4:N2}  → {5} sub-regions (pad>={6})" -f $indent, $Depth, $Offset, $Size, $ent, $subRegions.Count, $MinPadRun) -ForegroundColor Magenta
    }
    
    # Decide on the next level's min-pad-run
    # Top-level used 16, dive with progressively smaller thresholds to find finer structure
    $nextMinPad = switch ($Depth) {
        0 { 8 }
        1 { 4 }
        2 { 2 }
        default { 2 }
    }
    
    foreach ($region in $subRegions) {
        Invoke-DescentScan -Offset $region[0] -Size $region[1] -Depth ($Depth + 1) -MinPadRun $nextMinPad
    }
}

# ==============================================================================
# RUN
# ==============================================================================
Write-Host "[*] Starting recursive descent (max depth 6)..." -ForegroundColor Cyan
Write-Host ""

$sw = [System.Diagnostics.Stopwatch]::StartNew()
Invoke-DescentScan -Offset $StartOffset -Size $Length -Depth 0 -MinPadRun 16
$sw.Stop()

Write-Host ""
Write-Host "[*] Descent complete in $('{0:N2}' -f $sw.Elapsed.TotalSeconds)s ($global:CallCount recursive calls)" -ForegroundColor Green
Write-Host "    Found $($global:AllSubBlobs.Count) sub-blobs" -ForegroundColor Gray

# ==============================================================================
# SUMMARY + EXPORT
# ==============================================================================
$kindCounts = $global:AllSubBlobs | Group-Object -Property Kind | Sort-Object Count -Descending
Write-Host ""
Write-Host "Sub-blob kind distribution:" -ForegroundColor Cyan
foreach ($g in $kindCounts) {
    $totalBytes = ($g.Group | Measure-Object -Property Size -Sum).Sum
    Write-Host ("  {0,-14} {1,6:N0} blobs  {2,12:N0} bytes ({3:N1} MB)" -f $g.Name, $g.Count, $totalBytes, ($totalBytes / 1MB)) -ForegroundColor Gray
}

# Size histogram
Write-Host ""
Write-Host "Size distribution:" -ForegroundColor Cyan
$buckets = @{
    "<1KB"      = @($global:AllSubBlobs | Where-Object { $_.Size -lt 1024 })
    "1-10KB"    = @($global:AllSubBlobs | Where-Object { $_.Size -ge 1024 -and $_.Size -lt 10240 })
    "10-100KB"  = @($global:AllSubBlobs | Where-Object { $_.Size -ge 10240 -and $_.Size -lt 102400 })
    "100KB-1MB" = @($global:AllSubBlobs | Where-Object { $_.Size -ge 102400 -and $_.Size -lt 1048576 })
    "1-10MB"    = @($global:AllSubBlobs | Where-Object { $_.Size -ge 1048576 -and $_.Size -lt 10485760 })
    ">10MB"     = @($global:AllSubBlobs | Where-Object { $_.Size -ge 10485760 })
}
foreach ($k in @("<1KB","1-10KB","10-100KB","100KB-1MB","1-10MB",">10MB")) {
    Write-Host ("  {0,-12} {1,6:N0}" -f $k, $buckets[$k].Count) -ForegroundColor Gray
}

# Export TSV
$out = New-Object System.Text.StringBuilder
$null = $out.AppendLine("idx`toffset_hex`toffset_dec`tsize_bytes`tentropy`tdepth`tkind")
$idx = 0
foreach ($sb in $global:AllSubBlobs) {
    $null = $out.AppendLine(("{0}`t0x{1:X8}`t{1}`t{2}`t{3}`t{4}`t{5}" -f $idx, $sb.Offset, $sb.Size, $sb.Entropy, $sb.Depth, $sb.Kind))
    $idx++
}
[IO.File]::WriteAllText($OutTsv, $out.ToString())

Write-Host ""
Write-Host "[+] Wrote $idx sub-blobs to $OutTsv" -ForegroundColor Green