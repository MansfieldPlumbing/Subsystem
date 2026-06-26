<#
.SYNOPSIS
    scanner_magic.ps1 - Sentinel-Anchored QNN Descriptor Walker
.DESCRIPTION
    Scans for 0xC0000001 sentinel, classifies Type A (direct weight ptr)
    vs Type B (tensor index ref), emits full descriptor table.
#>

param(
    [string]$BinPath    = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [string]$OutPath    = "C:\bin\qnn\unet_out\descriptors.tsv",
    [int]   $HeaderLimit = 12000000   # 12MB - plenty past the 528KB we know about
)

$ErrorActionPreference = 'Stop'

$cs = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class MagicScanner {

    // 0xC0000001 little-endian as 4 bytes
    const uint SENTINEL = 0xC0000001u;

    public struct Descriptor {
        public int    HeaderOffset;   // where the sentinel landed
        public uint   Size;           // bytes at +0 after sentinel
        public uint   Field1;         // +4  (ptr or index A)
        public uint   Field2;         // +8  (flags or index B)
        public uint   Field3;         // +12 (op_id hint or flags)
        public uint   Pre1;           // -4  (word before sentinel)
        public uint   Pre2;           // -8
        public uint   Pre3;           // -12 (often rank/dim count)
        public string Kind;           // "WEIGHT_PTR" | "TENSOR_REF" | "UNKNOWN"
        public bool   PtrInFile;      // Field1 is a valid file offset
    }

    public static List<Descriptor> Scan(byte[] data, int limit, long fileSize) {
        var results = new List<Descriptor>();
        int maxOff = Math.Min(limit, data.Length - 20);

        for (int i = 12; i < maxOff; i += 4) {
            uint val = BitConverter.ToUInt32(data, i);
            if (val != SENTINEL) continue;

            // Read fields after sentinel
            if (i + 16 >= data.Length) continue;
            uint size  = BitConverter.ToUInt32(data, i + 4);
            uint f1    = BitConverter.ToUInt32(data, i + 8);
            uint f2    = BitConverter.ToUInt32(data, i + 12);
            uint f3    = (i + 16 < data.Length) ? BitConverter.ToUInt32(data, i + 16) : 0;

            // Size sanity: 64 bytes to 50 MB
            if (size < 64 || size > 50_000_000) continue;

            // Read 3 words before sentinel
            uint pre1 = BitConverter.ToUInt32(data, i - 4);
            uint pre2 = BitConverter.ToUInt32(data, i - 8);
            uint pre3 = BitConverter.ToUInt32(data, i - 12);

            bool ptrInFile = (f1 >= 0x00081000 && (long)f1 + size <= fileSize);

            string kind;
            if (ptrInFile) {
                kind = "WEIGHT_PTR";
            } else if (f1 == 8 || f1 < 0x10000) {
                kind = "TENSOR_REF";
            } else {
                kind = "UNKNOWN";
            }

            results.Add(new Descriptor {
                HeaderOffset = i,
                Size   = size,
                Field1 = f1,
                Field2 = f2,
                Field3 = f3,
                Pre1   = pre1,
                Pre2   = pre2,
                Pre3   = pre3,
                Kind   = kind,
                PtrInFile = ptrInFile
            });
        }
        return results;
    }

    public static void WriteTsv(List<Descriptor> descs, string path) {
        var sb = new StringBuilder();
        sb.AppendLine("idx\theader_off\tkind\tsize\tfield1_ptr\tfield2\tfield3\tpre3\tpre2\tpre1\tptr_hex\tsize_MB");
        for (int i = 0; i < descs.Count; i++) {
            var d = descs[i];
            sb.AppendLine(string.Format("{0}\t0x{1:X8}\t{2}\t{3}\t0x{4:X8}\t0x{5:X8}\t0x{6:X8}\t{7}\t{8}\t{9}\t0x{10:X8}\t{11:F3}",
                i,
                d.HeaderOffset,
                d.Kind,
                d.Size,
                d.Field1,
                d.Field2,
                d.Field3,
                d.Pre3, d.Pre2, d.Pre1,
                d.Field1,
                d.Size / 1048576.0));
        }
        File.WriteAllText(path, sb.ToString());
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'MagicScanner').Type) {
    Write-Host "[*] Compiling..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $cs -Language CSharp
}

Write-Host "[*] Loading $BinPath" -ForegroundColor Cyan
$bytes    = [IO.File]::ReadAllBytes($BinPath)
$fileSize = $bytes.LongLength

Write-Host "[*] Scanning for 0xC0000001 sentinels in first $([int]($HeaderLimit/1MB)) MB..." -ForegroundColor Cyan

$descs = [MagicScanner]::Scan($bytes, $HeaderLimit, $fileSize)

Write-Host "[+] Found $($descs.Count) descriptors" -ForegroundColor Green

# Breakdown by kind
$wp  = @($descs | Where-Object { $_.Kind -eq 'WEIGHT_PTR'  })
$tr  = @($descs | Where-Object { $_.Kind -eq 'TENSOR_REF'  })
$unk = @($descs | Where-Object { $_.Kind -eq 'UNKNOWN'      })

Write-Host "    WEIGHT_PTR  : $($wp.Count)"  -ForegroundColor Yellow
Write-Host "    TENSOR_REF  : $($tr.Count)"  -ForegroundColor Cyan
Write-Host "    UNKNOWN     : $($unk.Count)" -ForegroundColor DarkGray

# Size distribution for WEIGHT_PTR
if ($wp.Count -gt 0) {
    $bins = @{ '<1KB'=0; '1-10KB'=0; '10-100KB'=0; '100KB-1MB'=0; '1-10MB'=0; '>10MB'=0 }
    foreach ($d in $wp) {
        $s = $d.Size
        if     ($s -lt 1024)        { $bins['<1KB']++ }
        elseif ($s -lt 10240)       { $bins['1-10KB']++ }
        elseif ($s -lt 102400)      { $bins['10-100KB']++ }
        elseif ($s -lt 1048576)     { $bins['100KB-1MB']++ }
        elseif ($s -lt 10485760)    { $bins['1-10MB']++ }
        else                        { $bins['>10MB']++ }
    }
    Write-Host "`nWEIGHT_PTR size distribution:" -ForegroundColor Yellow
    foreach ($k in '<1KB','1-10KB','10-100KB','100KB-1MB','1-10MB','>10MB') {
        Write-Host ("  {0,-12}: {1}" -f $k, $bins[$k])
    }
}

# First 30 WEIGHT_PTR descriptors
Write-Host "`nFirst 30 WEIGHT_PTR descriptors:" -ForegroundColor Yellow
Write-Host ("{0,-5} {1,-12} {2,-14} {3,-10} {4,-10} {5,-10} {6}" -f `
    "idx","hdr_off","weight_ptr","size","size_MB","pre3","pre2")
Write-Host ("-" * 80)
$shown = 0
foreach ($d in $descs) {
    if ($d.Kind -ne 'WEIGHT_PTR') { continue }
    Write-Host ("{0,-5} 0x{1:X8}  0x{2:X8}  {3,10}  {4,7:F3}  {5,10}  {6,10}" -f `
        $shown,
        $d.HeaderOffset,
        $d.Field1,
        $d.Size,
        ($d.Size / 1048576.0),
        $d.Pre3,
        $d.Pre2)
    $shown++
    if ($shown -ge 30) { break }
}

# First 10 TENSOR_REF
if ($tr.Count -gt 0) {
    Write-Host "`nFirst 10 TENSOR_REF descriptors (index-based, no direct ptr):" -ForegroundColor Cyan
    Write-Host ("{0,-5} {1,-12} {2,-10} {3,-10} {4,-10}" -f "idx","hdr_off","size","idxA","idxB")
    Write-Host ("-" * 55)
    $shown2 = 0
    foreach ($d in $descs) {
        if ($d.Kind -ne 'TENSOR_REF') { continue }
        Write-Host ("{0,-5} 0x{1:X8}  {2,10}  {3,10}  {4,10}" -f `
            $shown2, $d.HeaderOffset, $d.Size, $d.Field1, $d.Field2)
        $shown2++
        if ($shown2 -ge 10) { break }
    }
}

# Check for pointer overlap / gaps in WEIGHT_PTR set
$sorted = $wp | Sort-Object { $_.Field1 }
Write-Host "`nWEIGHT_PTR pointer range:" -ForegroundColor Yellow
if ($wp.Count -gt 0) {
    Write-Host ("  First ptr: 0x{0:X8}" -f $sorted[0].Field1)
    Write-Host ("  Last  ptr: 0x{0:X8}" -f $sorted[-1].Field1)
    $overlaps = 0
    for ($i = 0; $i -lt $sorted.Count - 1; $i++) {
        $end = $sorted[$i].Field1 + $sorted[$i].Size
        if ($end -gt $sorted[$i+1].Field1) { $overlaps++ }
    }
    Write-Host "  Overlapping pairs: $overlaps"
    Write-Host "  Non-overlapping  : $($sorted.Count - 1 - $overlaps)"
}

[MagicScanner]::WriteTsv($descs, $OutPath)
Write-Host "`n[+] Full table -> $OutPath" -ForegroundColor Green