<#
.SYNOPSIS
    scanner_grid.ps1 - Walk the weight stream as a 64KB-aligned slot grid.

.DESCRIPTION
    The optable scan revealed a pattern: weight pointers land on 64KB-aligned
    boundaries (0x__0000 or 0x__0800), stepping by 0x10000. This script
    assumes the compiler allocated the weight stream as a grid of 64KB slots,
    each containing:
      - Optional 2KB prefix (probably per-channel scale/offset tables)
      - Weight data filling the rest of the slot
      - Optional spill into subsequent slots for large tensors

    We classify each 64KB slot by its content profile and emit a manifest.

.PARAMETER BinPath
    Path to QNN context binary.

.PARAMETER WeightStart
    Where the weight stream begins (default 0x00081000).

.PARAMETER SlotSize
    Grid slot size (default 65536 = 64KB).

.PARAMETER PrefixSize
    Expected prefix size within each slot (default 2048 = 2KB).
#>

param(
    [string]$BinPath    = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$WeightStart  = 0x00081000,
    [long]$SlotSize     = 65536,
    [long]$PrefixSize   = 2048,
    [string]$OutTsv     = "C:\bin\qnn\unet_out\slots.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;

public class SlotInfo {
    public long SlotIdx;
    public long SlotStart;
    public long SlotEnd;
    public double PrefixEntropy;   // entropy of first 2KB
    public double BodyEntropy;     // entropy of remaining bytes
    public long  ZeroRunsInPrefix;
    public long  ZeroBytesInBody;
    public long  FirstNonZero;     // first non-zero byte offset within slot
    public long  LastNonZero;      // last non-zero byte offset within slot
    public long  UsedBytes;        // LastNonZero - FirstNonZero + 1
    public bool  PrefixLooksLikeFP32Table;
    public string Classification;  // empty | small_tensor | dense_weight | continuation
}

public static class GridScanner {
    public static unsafe double Entropy(byte[] data, long offset, long length) {
        if (length == 0) return 0;
        int[] freq = new int[256];
        fixed (byte* p0 = data) {
            byte* p = p0 + offset;
            for (long i = 0; i < length; i++) freq[*p++]++;
        }
        double ent = 0; double n = length;
        for (int i = 0; i < 256; i++) if (freq[i] > 0) {
            double pr = freq[i] / n;
            ent -= pr * Math.Log(pr, 2);
        }
        return ent;
    }

    public static unsafe (long firstNZ, long lastNZ, long zeros) ScanForNonZero(byte[] data, long offset, long length) {
        long firstNZ = -1, lastNZ = -1, zeros = 0;
        fixed (byte* p0 = data) {
            byte* p = p0 + offset;
            for (long i = 0; i < length; i++) {
                if (p[i] != 0) {
                    if (firstNZ < 0) firstNZ = i;
                    lastNZ = i;
                } else {
                    zeros++;
                }
            }
        }
        return (firstNZ, lastNZ, zeros);
    }

    // Check if first 2KB looks like packed FP32 scales (high byte in [0x30, 0x40])
    public static unsafe double FP32ScaleProbability(byte[] data, long offset, long length) {
        if (length < 16 || length % 4 != 0) return 0;
        long count = length / 4;
        long hits = 0;
        fixed (byte* p0 = data) {
            byte* p = p0 + offset;
            for (long i = 0; i < count; i++) {
                byte high = p[i*4 + 3];
                if (high >= 0x30 && high <= 0x40) hits++;
            }
        }
        return (double)hits / count;
    }

    public static SlotInfo AnalyzeSlot(byte[] data, long slotIdx, long slotStart, long slotEnd, long prefixSize, long fileSize) {
        var info = new SlotInfo();
        info.SlotIdx = slotIdx;
        info.SlotStart = slotStart;
        info.SlotEnd = slotEnd;

        long effectiveEnd = Math.Min(slotEnd, fileSize);
        long slotLen = effectiveEnd - slotStart;
        if (slotLen <= 0) {
            info.Classification = "out_of_file";
            return info;
        }

        long actualPrefix = Math.Min(prefixSize, slotLen);
        info.PrefixEntropy = Entropy(data, slotStart, actualPrefix);
        info.PrefixLooksLikeFP32Table = FP32ScaleProbability(data, slotStart, actualPrefix) > 0.85;

        if (slotLen > actualPrefix) {
            info.BodyEntropy = Entropy(data, slotStart + actualPrefix, slotLen - actualPrefix);
        }

        var (firstNZ, lastNZ, zeros) = ScanForNonZero(data, slotStart, slotLen);
        info.FirstNonZero = firstNZ;
        info.LastNonZero = lastNZ;
        info.UsedBytes = (firstNZ < 0) ? 0 : (lastNZ - firstNZ + 1);
        info.ZeroBytesInBody = zeros;

        // Classification heuristic
        if (info.UsedBytes == 0) {
            info.Classification = "empty";
        } else if (info.UsedBytes < 4096) {
            info.Classification = "small_tensor";
        } else if (info.BodyEntropy > 7.5 && info.PrefixLooksLikeFP32Table) {
            info.Classification = "weight+scales";
        } else if (info.BodyEntropy > 7.5) {
            info.Classification = "dense_weight";
        } else if (info.PrefixEntropy < 3.0 && info.BodyEntropy > 7.0) {
            info.Classification = "padded_weight";
        } else if (info.UsedBytes > 60000) {
            info.Classification = "continuation";
        } else {
            info.Classification = "mixed";
        }

        return info;
    }
}
"@

Write-Host "[*] Compiling C# grid scanner..." -ForegroundColor DarkGray
if (-not ([System.Management.Automation.PSTypeName]'GridScanner').Type) {
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

Write-Host "[*] Loading binary..." -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)
$fileSize = $bytes.LongLength
Write-Host "    File size: $($fileSize.ToString('N0')) bytes" -ForegroundColor Gray

$weightLen = $fileSize - $WeightStart
$totalSlots = [Math]::Ceiling($weightLen / $SlotSize)
Write-Host "    Weight stream: 0x$($WeightStart.ToString('X8')) -> 0x$($fileSize.ToString('X8'))  ($($weightLen.ToString('N0')) bytes)" -ForegroundColor Gray
Write-Host "    Grid: $totalSlots slots of $SlotSize bytes" -ForegroundColor Gray
Write-Host ""

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$slots = New-Object System.Collections.Generic.List[SlotInfo]
for ($i = 0; $i -lt $totalSlots; $i++) {
    $slotStart = $WeightStart + ($i * $SlotSize)
    $slotEnd   = $slotStart + $SlotSize
    $info = [GridScanner]::AnalyzeSlot($bytes, $i, $slotStart, $slotEnd, $PrefixSize, $fileSize)
    $slots.Add($info)

    if (($i % 1000) -eq 0 -and $i -gt 0) {
        Write-Host ("    Processed $i / $totalSlots slots ({0:N1}s)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
    }
}
$sw.Stop()

Write-Host "[+] Analyzed $totalSlots slots in $('{0:N2}' -f $sw.Elapsed.TotalSeconds)s" -ForegroundColor Green
Write-Host ""

# Classification distribution
$classCounts = $slots | Group-Object -Property Classification | Sort-Object Count -Descending
Write-Host "Slot classification:" -ForegroundColor Cyan
foreach ($g in $classCounts) {
    Write-Host ("  {0,-16} {1,6:N0} slots  ({2,5:N1}%)" -f $g.Name, $g.Count, (100 * $g.Count / $totalSlots)) -ForegroundColor Gray
}

# First 30 slots
Write-Host ""
Write-Host "First 30 slots:" -ForegroundColor Cyan
Write-Host ("{0,4}  {1,10}  {2,10}  {3,6}  {4,6}  {5,8}  {6,5}  {7}" -f "idx", "start", "end", "ent_p", "ent_b", "used", "fp32", "class") -ForegroundColor DarkGray
Write-Host ("-" * 90) -ForegroundColor DarkGray
for ($i = 0; $i -lt [Math]::Min(30, $slots.Count); $i++) {
    $s = $slots[$i]
    $fp32Flag = if ($s.PrefixLooksLikeFP32Table) { "YES" } else { "no" }
    Write-Host ("{0,4}  0x{1:X8}  0x{2:X8}  {3,6:N2}  {4,6:N2}  {5,8:N0}  {6,5}  {7}" -f $s.SlotIdx, $s.SlotStart, $s.SlotEnd, $s.PrefixEntropy, $s.BodyEntropy, $s.UsedBytes, $fp32Flag, $s.Classification) -ForegroundColor Gray
}

# Find runs of continuation slots (large tensors spanning multiple slots)
Write-Host ""
Write-Host "Multi-slot tensor runs (continuation chains):" -ForegroundColor Cyan
$runs = New-Object System.Collections.Generic.List[object]
$runStart = -1
$runLen = 0
for ($i = 0; $i -lt $slots.Count; $i++) {
    if ($slots[$i].Classification -in @("dense_weight", "weight+scales", "continuation", "padded_weight")) {
        if ($runStart -lt 0) { $runStart = $i }
        $runLen++
    } else {
        if ($runLen -ge 2) {
            $runs.Add([PSCustomObject]@{Start=$runStart; Len=$runLen; SizeMB=($runLen * $SlotSize / 1MB)})
        }
        $runStart = -1
        $runLen = 0
    }
}
if ($runLen -ge 2) {
    $runs.Add([PSCustomObject]@{Start=$runStart; Len=$runLen; SizeMB=($runLen * $SlotSize / 1MB)})
}
Write-Host "  Found $($runs.Count) multi-slot runs (>=2 slots dense)" -ForegroundColor Gray
$topRuns = $runs | Sort-Object -Property Len -Descending | Select-Object -First 10
Write-Host "  Top 10 longest runs:" -ForegroundColor Gray
foreach ($r in $topRuns) {
    $startOff = $WeightStart + $r.Start * $SlotSize
    Write-Host ("    slot {0,5}  start=0x{1:X8}  len={2,4} slots  {3,7:N1} MB" -f $r.Start, $startOff, $r.Len, $r.SizeMB) -ForegroundColor DarkGray
}

# Export
$out = New-Object System.Text.StringBuilder
$null = $out.AppendLine("slot_idx`tstart_hex`tend_hex`tprefix_ent`tbody_ent`tused_bytes`tfp32_prefix`tclass")
foreach ($s in $slots) {
    $null = $out.AppendLine(("{0}`t0x{1:X8}`t0x{2:X8}`t{3:N3}`t{4:N3}`t{5}`t{6}`t{7}" -f $s.SlotIdx, $s.SlotStart, $s.SlotEnd, $s.PrefixEntropy, $s.BodyEntropy, $s.UsedBytes, $s.PrefixLooksLikeFP32Table, $s.Classification))
}
[IO.File]::WriteAllText($OutTsv, $out.ToString())
Write-Host ""
Write-Host "[+] Wrote $totalSlots slot records to $OutTsv" -ForegroundColor Green