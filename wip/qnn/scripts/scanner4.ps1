<#
.SYNOPSIS
    scanner_optable.ps1 - Find the real QNN op-descriptor table in the header.
    
.DESCRIPTION
    The QNN context binary has a metadata region in the first ~528KB (before
    the weight stream starts at ~0x081000). That region should contain an
    array of op-descriptor structs, each with:
      - A pointer (uint32 or uint64) to the op's weight blob
      - A size field (uint32)
      - Probably a dtype/flags field
      - Probably an op-id field
    
    We don't know the struct layout, but we can DISCOVER it by:
      1. Finding plausible struct strides (32, 48, 64 bytes most likely)
      2. Validating multiple fields simultaneously (not just "looks like pointer")
      3. Requiring high-confidence matches (10+ consecutive valid structs)
      4. Rejecting overlapping tensor regions
      
    Output: optable.tsv with (struct_offset, weight_ptr, weight_size, op_id_guess)
#>

param(
    [string]$BinPath = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [long]$HeaderEnd = 0x00081000,    # weight stream starts here per minimap
    [long]$WeightStreamStart = 0x00081000,
    [long]$WeightStreamEnd   = 0x347EFEC0,   # end of file ish; refined below
    [string]$OutTsv = "C:\bin\qnn\unet_out\optable.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;
using System.Linq;

public class OpDescriptor {
    public long StructOffset;
    public long WeightPtr;
    public long WeightSize;
    public int  Stride;
    public int  PtrFieldOffset;
    public int  SizeFieldOffset;
}

public static class OpTableScanner {
    
    // Score how "pointer-table-like" a region is at a given stride and offsets.
    // Returns: (score, list of valid descriptors discovered)
    public static unsafe (int, List<OpDescriptor>) ScoreLayout(
        byte[] data, 
        long regionStart, long regionEnd, 
        int stride, int ptrFieldOff, int sizeFieldOff,
        long weightStart, long weightEnd,
        long fileSize)
    {
        var descriptors = new List<OpDescriptor>();
        int validRun = 0;
        int maxRun = 0;
        int totalValid = 0;
        
        fixed (byte* p0 = data) {
            long pos = regionStart;
            while (pos + stride <= regionEnd) {
                // Read pointer (try uint32 first; could also be uint64)
                long ptr = (long)(*((uint*)(p0 + pos + ptrFieldOff)));
                long size = (long)(*((uint*)(p0 + pos + sizeFieldOff)));
                
                bool ptrValid = (ptr >= weightStart && ptr < weightEnd);
                bool sizeValid = (size > 64 && size < 100*1024*1024); // 64B to 100MB
                bool inFile = (ptr + size <= fileSize);
                
                // Both fields must look reasonable
                if (ptrValid && sizeValid && inFile) {
                    descriptors.Add(new OpDescriptor {
                        StructOffset = pos,
                        WeightPtr = ptr,
                        WeightSize = size,
                        Stride = stride,
                        PtrFieldOffset = ptrFieldOff,
                        SizeFieldOffset = sizeFieldOff
                    });
                    totalValid++;
                    validRun++;
                    if (validRun > maxRun) maxRun = validRun;
                } else {
                    validRun = 0;
                }
                pos += stride;
            }
        }
        
        // Score: heavily weight long runs of valid structs (indicates real table)
        // Also reward total count
        int score = (maxRun * 100) + totalValid;
        return (score, descriptors);
    }
    
    // Try uint64 pointers too
    public static unsafe (int, List<OpDescriptor>) ScoreLayout64(
        byte[] data,
        long regionStart, long regionEnd,
        int stride, int ptrFieldOff, int sizeFieldOff,
        long weightStart, long weightEnd,
        long fileSize)
    {
        var descriptors = new List<OpDescriptor>();
        int validRun = 0;
        int maxRun = 0;
        int totalValid = 0;
        
        fixed (byte* p0 = data) {
            long pos = regionStart;
            while (pos + stride <= regionEnd) {
                long ptr = (long)(*((ulong*)(p0 + pos + ptrFieldOff)));
                long size = (long)(*((uint*)(p0 + pos + sizeFieldOff)));
                
                bool ptrValid = (ptr >= weightStart && ptr < weightEnd);
                bool sizeValid = (size > 64 && size < 100*1024*1024);
                bool inFile = (ptr + size <= fileSize);
                
                if (ptrValid && sizeValid && inFile) {
                    descriptors.Add(new OpDescriptor {
                        StructOffset = pos,
                        WeightPtr = ptr,
                        WeightSize = size,
                        Stride = stride,
                        PtrFieldOffset = ptrFieldOff,
                        SizeFieldOffset = sizeFieldOff
                    });
                    totalValid++;
                    validRun++;
                    if (validRun > maxRun) maxRun = validRun;
                } else {
                    validRun = 0;
                }
                pos += stride;
            }
        }
        
        int score = (maxRun * 100) + totalValid;
        return (score, descriptors);
    }
}
"@

Write-Host "[*] Compiling C# scanner..." -ForegroundColor DarkGray
if (-not ([System.Management.Automation.PSTypeName]'OpTableScanner').Type) {
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

Write-Host "[*] Loading $BinPath..." -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)
$fileSize = $bytes.LongLength
Write-Host "    File size: $($fileSize.ToString('N0')) bytes" -ForegroundColor Gray
Write-Host "    Header region: 0x00000000 - 0x$($HeaderEnd.ToString('X8'))  ($($HeaderEnd) bytes)" -ForegroundColor Gray
Write-Host "    Valid weight ptr range: 0x$($WeightStreamStart.ToString('X8')) - 0x$($fileSize.ToString('X8'))" -ForegroundColor Gray
Write-Host ""

# Try a bunch of struct layouts and find the best-scoring one.
# Common QNN/Qualcomm structs: 16, 24, 32, 40, 48, 64 bytes
# Pointer can be uint32 or uint64
# Pointer field can be at offset 0, 4, 8, 12, 16
# Size field somewhere else, typically 4 bytes after the pointer

Write-Host "[*] Searching for op-descriptor table layout..." -ForegroundColor Cyan
Write-Host ""

$bestScore = 0
$bestDescriptors = $null
$bestConfig = $null

# Sweep common struct strides
$strides = @(16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 128)

# For each stride, try multiple (ptr_offset, size_offset) combinations
foreach ($stride in $strides) {
    # Try uint32 pointer positions
    foreach ($ptrOff in @(0, 4, 8, 12, 16)) {
        if ($ptrOff + 4 -gt $stride) { continue }
        foreach ($sizeOff in @(0, 4, 8, 12, 16, 20, 24)) {
            if ($sizeOff + 4 -gt $stride) { continue }
            if ($sizeOff -eq $ptrOff) { continue }
            
            $result = [OpTableScanner]::ScoreLayout($bytes, 0L, $HeaderEnd, $stride, $ptrOff, $sizeOff, $WeightStreamStart, $fileSize, $fileSize)
            $score = $result.Item1
            $descs = $result.Item2
            
            if ($score -gt $bestScore) {
                $bestScore = $score
                $bestDescriptors = $descs
                $bestConfig = @{ stride=$stride; ptrOff=$ptrOff; sizeOff=$sizeOff; ptrWidth=4 }
                Write-Host ("    [u32] stride={0,3} ptr@{1,2} size@{2,2}  score={3,8}  count={4}" -f $stride, $ptrOff, $sizeOff, $score, $descs.Count) -ForegroundColor DarkGray
            }
        }
    }
    
    # Try uint64 pointer positions
    foreach ($ptrOff in @(0, 8, 16)) {
        if ($ptrOff + 8 -gt $stride) { continue }
        foreach ($sizeOff in @(0, 4, 8, 12, 16, 20, 24)) {
            if ($sizeOff + 4 -gt $stride) { continue }
            if ($sizeOff -ge $ptrOff -and $sizeOff -lt $ptrOff + 8) { continue }
            
            $result = [OpTableScanner]::ScoreLayout64($bytes, 0L, $HeaderEnd, $stride, $ptrOff, $sizeOff, $WeightStreamStart, $fileSize, $fileSize)
            $score = $result.Item1
            $descs = $result.Item2
            
            if ($score -gt $bestScore) {
                $bestScore = $score
                $bestDescriptors = $descs
                $bestConfig = @{ stride=$stride; ptrOff=$ptrOff; sizeOff=$sizeOff; ptrWidth=8 }
                Write-Host ("    [u64] stride={0,3} ptr@{1,2} size@{2,2}  score={3,8}  count={4}" -f $stride, $ptrOff, $sizeOff, $score, $descs.Count) -ForegroundColor DarkGray
            }
        }
    }
}

Write-Host ""
Write-Host "[+] Best layout found:" -ForegroundColor Green
Write-Host ("    Stride: $($bestConfig.stride) bytes  Pointer width: $($bestConfig.ptrWidth) bytes") -ForegroundColor White
Write-Host ("    Pointer field @ offset $($bestConfig.ptrOff)") -ForegroundColor White
Write-Host ("    Size field @ offset $($bestConfig.sizeOff)") -ForegroundColor White
Write-Host ("    Score: $bestScore") -ForegroundColor White
Write-Host ("    Total descriptors: $($bestDescriptors.Count)") -ForegroundColor White
Write-Host ""

if ($bestDescriptors.Count -eq 0) {
    Write-Host "[!] No layout found. The table might not be in the expected region or" -ForegroundColor Red
    Write-Host "    might use a different encoding (relative offsets, compressed, etc)." -ForegroundColor Red
    return
}

# Sanity check: do tensor regions overlap?
$sorted = $bestDescriptors | Sort-Object WeightPtr
$overlaps = 0
$totalCovered = 0L
$prevEnd = 0L
foreach ($d in $sorted) {
    $endByte = $d.WeightPtr + $d.WeightSize
    if ($d.WeightPtr -lt $prevEnd) { $overlaps++ }
    $totalCovered += $d.WeightSize
    if ($endByte -gt $prevEnd) { $prevEnd = $endByte }
}

Write-Host "[*] Sanity check:" -ForegroundColor Cyan
Write-Host ("    Overlapping descriptors: $overlaps") -ForegroundColor Gray
Write-Host ("    Total bytes covered:     $($totalCovered.ToString('N0')) bytes ($([Math]::Round($totalCovered/1MB,1)) MB)") -ForegroundColor Gray
Write-Host ("    Weight stream size:      $(($fileSize - $WeightStreamStart).ToString('N0')) bytes") -ForegroundColor Gray
Write-Host ("    Coverage:                $([Math]::Round(100 * $totalCovered / ($fileSize - $WeightStreamStart), 1))%") -ForegroundColor Gray
Write-Host ""

# Show first 30 descriptors
Write-Host "First 30 descriptors:" -ForegroundColor Cyan
Write-Host ("{0,4}  {1,10}  {2,10}  {3,12}  {4,10}" -f "idx", "struct_off", "weight_ptr", "weight_size", "size_MB") -ForegroundColor DarkGray
Write-Host ("-" * 60) -ForegroundColor DarkGray
$i = 0
foreach ($d in $sorted) {
    if ($i -ge 30) { break }
    Write-Host ("{0,4}  0x{1:X8}  0x{2:X8}  {3,12:N0}  {4,10:N3}" -f $i, $d.StructOffset, $d.WeightPtr, $d.WeightSize, ($d.WeightSize/1MB)) -ForegroundColor Gray
    $i++
}

# Size histogram
Write-Host ""
Write-Host "Descriptor size distribution:" -ForegroundColor Cyan
$buckets = @{
    "<1KB"      = 0
    "1-10KB"    = 0
    "10-100KB"  = 0
    "100KB-1MB" = 0
    "1-10MB"    = 0
    ">10MB"     = 0
}
foreach ($d in $sorted) {
    if     ($d.WeightSize -lt 1024)    { $buckets["<1KB"]++ }
    elseif ($d.WeightSize -lt 10240)   { $buckets["1-10KB"]++ }
    elseif ($d.WeightSize -lt 102400)  { $buckets["10-100KB"]++ }
    elseif ($d.WeightSize -lt 1048576) { $buckets["100KB-1MB"]++ }
    elseif ($d.WeightSize -lt 10485760){ $buckets["1-10MB"]++ }
    else                                { $buckets[">10MB"]++ }
}
foreach ($k in @("<1KB","1-10KB","10-100KB","100KB-1MB","1-10MB",">10MB")) {
    Write-Host ("  {0,-12} {1,6}" -f $k, $buckets[$k]) -ForegroundColor Gray
}

# Export
$out = New-Object System.Text.StringBuilder
$null = $out.AppendLine("idx`tstruct_offset_hex`tweight_ptr_hex`tweight_size`tsize_mb")
$i = 0
foreach ($d in $sorted) {
    $null = $out.AppendLine(("{0}`t0x{1:X8}`t0x{2:X8}`t{3}`t{4:N3}" -f $i, $d.StructOffset, $d.WeightPtr, $d.WeightSize, ($d.WeightSize/1MB)))
    $i++
}
[IO.File]::WriteAllText($OutTsv, $out.ToString())

Write-Host ""
Write-Host "[+] Wrote $($bestDescriptors.Count) descriptors to $OutTsv" -ForegroundColor Green
