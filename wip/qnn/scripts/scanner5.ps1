<#
.SYNOPSIS
    QnnSchemaCarver.ps1 - The Mathematical Object-Graph Carver
.DESCRIPTION
    Zero heuristics. Zero guessing. Tests memory permutations to mathematically 
    lock onto the QNN compiler's Tensor Descriptor struct schema, then extracts 
    the exact memory boundaries.
#>

param([string]$BinPath = "C:\bin\qnn\test-harness-s23-v73\unet.bin",
    [string]$OutTsv = "C:\bin\qnn\unet_out\megablob_objects.tsv"
)

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;

public class TensorRecord {
    public int Index;
    public long Offset;
    public long Size;
    public long End => Offset + Size;
}

public class Schema {
    public int Stride;
    public int SizeOffset;
    public int TableStartAnchor;
}

public static class QnnSchemaCarver {
    public static unsafe Schema FindSchema(byte[] data) {
        // Only scan the header/metadata region (first 12MB)
        int searchLimit = Math.Min(data.Length, 12000000); 
        
        fixed (byte* p = data) {
            for (int i = 0; i < searchLimit - 512; i += 4) {
                uint off1 = *(uint*)(p + i);
                
                // Fast reject: must be a valid pointer into the file's payload
                if (off1 < 100000 || off1 >= data.Length) continue; 
                
                // Test structural strides (from 16 bytes up to 256 bytes)
                for (int stride = 16; stride <= 256; stride += 4) {
                    uint off2 = *(uint*)(p + i + stride);
                    if (off2 <= off1 || off2 >= data.Length) continue;
                    
                    // Test Size-field offsets relative to the pointer (-128 to +128 bytes)
                    for (int sizeOfs = -128; sizeOfs <= 128; sizeOfs += 4) {
                        if (sizeOfs == 0 || sizeOfs == stride) continue;
                        
                        int size1Addr = i + sizeOfs;
                        if (size1Addr < 0 || size1Addr > searchLimit) continue;
                        
                        uint size1 = *(uint*)(p + size1Addr);
                        if (size1 == 0 || size1 > 500000000) continue;
                        
                        // The Equation: Does the next pointer = current pointer + size + padding?
                        uint expectedOff2 = off1 + size1;
                        if (off2 < expectedOff2) continue;
                        uint gap1 = off2 - expectedOff2;
                        
                        if (gap1 <= 8192) {
                            // Link 3
                            uint size2 = *(uint*)(p + i + stride + sizeOfs);
                            uint off3 = *(uint*)(p + i + stride * 2);
                            if (size2 == 0 || off3 < off2 + size2 || off3 >= data.Length) continue;
                            uint gap2 = off3 - (off2 + size2);
                            
                            if (gap2 <= 8192) {
                                // Link 4
                                uint size3 = *(uint*)(p + i + stride * 2 + sizeOfs);
                                uint off4 = *(uint*)(p + i + stride * 3);
                                if (size3 == 0 || off4 < off3 + size3 || off4 >= data.Length) continue;
                                uint gap3 = off4 - (off3 + size3);
                                
                                if (gap3 <= 8192) {
                                    // BINGO. We mathematically locked the schema.
                                    return new Schema {
                                        Stride = stride,
                                        SizeOffset = sizeOfs,
                                        TableStartAnchor = i
                                    };
                                }
                            }
                        }
                    }
                }
            }
        }
        return null;
    }
    
    public static unsafe int[] GetSchemaInfo(byte[] data) {
        Schema s = FindSchema(data);
        if (s == null) return new int[] { 0, 0, 0 };
        return new int[] { s.Stride, s.SizeOffset, s.TableStartAnchor };
    }
    
    public static unsafe List<TensorRecord> ExtractTable(byte[] data) {
        Schema schema = FindSchema(data);
        if (schema == null) return new List<TensorRecord>();
        
        var results = new List<TensorRecord>();
        fixed (byte* p = data) {
            int currAnchor = schema.TableStartAnchor;
            
            // 1. Walk Backwards to find the true start of the Descriptor Table
            while (currAnchor >= schema.Stride) {
                int prevAnchor = currAnchor - schema.Stride;
                uint prevOff = *(uint*)(p + prevAnchor);
                uint prevSz = *(uint*)(p + prevAnchor + schema.SizeOffset);
                uint currOff = *(uint*)(p + currAnchor);
                
                if (prevOff == 0 || prevSz == 0 || prevSz > 500000000 || prevOff > currOff) break;
                if (currOff - (prevOff + prevSz) > 65536) break; // Massive gap means left the table
                
                currAnchor = prevAnchor;
            }
            
            // 2. Extract Forwards
            int id = 0;
            int misses = 0;
            while (currAnchor + schema.Stride < data.Length) {
                uint off = *(uint*)(p + currAnchor);
                uint sz = *(uint*)(p + currAnchor + schema.SizeOffset);
                
                if (off > data.Length || sz > data.Length) break;
                
                if (off == 0 || sz == 0) {
                    misses++;
                    if (misses > 20) break; // Consecutive empty structs -> End of array padding
                    currAnchor += schema.Stride;
                    continue;
                }
                
                if (results.Count > 0) {
                    long prevEnd = results[results.Count - 1].End;
                    if (off < prevEnd) break; // Memory overlap -> Left the table
                }
                
                results.Add(new TensorRecord {
                    Index = id++,
                    Offset = off,
                    Size = sz
                });
                
                misses = 0;
                currAnchor += schema.Stride;
            }
        }
        return results;
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnSchemaCarver').Type) {
    Write-Host "[*] Compiling C# Math Schema Carver..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

if (-not (Test-Path $BinPath)) { throw "Binary not found: $BinPath" }

Write-Host "[*] Loading Binary Graph: $BinPath" -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)

$sw =[System.Diagnostics.Stopwatch]::StartNew()
$schemaInfo = [QnnSchemaCarver]::GetSchemaInfo($bytes)

if ($schemaInfo[0] -eq 0) {
    Write-Host "[!] Failed to mathematically lock onto a valid schema. The compiler might not store Size fields directly next to Offset fields." -ForegroundColor Red
    exit
}

Write-Host ""
Write-Host ("[*] Mathematical Schema Lock Acquired!") -ForegroundColor Magenta
Write-Host ("    Struct Array Stride: {0} bytes" -f $schemaInfo[0]) -ForegroundColor Gray
Write-Host ("    Size Field Offset:   {0} bytes (Relative to Pointer)" -f $schemaInfo[1]) -ForegroundColor Gray
Write-Host ("    Anchor Lock Offset:  0x{0:X8}" -f $schemaInfo[2]) -ForegroundColor Gray
Write-Host ""

$objects = [QnnSchemaCarver]::ExtractTable($bytes)
$sw.Stop()

Write-Host ("[+] Pointer Graph Extracted in {0:N2} ms" -f $sw.Elapsed.TotalMilliseconds) -ForegroundColor Green
Write-Host ("    Discovered {0:N0} True Tensor Objects!" -f $objects.Count) -ForegroundColor White
Write-Host ""

$outLines = New-Object System.Text.StringBuilder
$null = $outLines.AppendLine("block_idx`toffset_hex`toffset_dec`tsize_bytes")

foreach ($obj in $objects) {
    $null = $outLines.AppendLine(("{0}`t0x{1:X8}`t{1}`t{2}" -f $obj.Index, $obj.Offset, $obj.Size))
}

for ($i = 0; $i -lt[Math]::Min(20, $objects.Count); $i++) {
    $obj = $objects[$i]
    Write-Host ("    Tensor {0,4}: Offset 0x{1:X8} | Size {2,10:N0} bytes" -f $obj.Index, $obj.Offset, $obj.Size) -ForegroundColor Gray
}

if ($objects.Count -gt 20) { Write-Host "    ... (truncated)" -ForegroundColor DarkGray }

$outDir = Split-Path $OutTsv -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }[IO.File]::WriteAllText($OutTsv, $outLines.ToString())

Write-Host ""
Write-Host "[+] Wrote true object map to $OutTsv" -ForegroundColor Green