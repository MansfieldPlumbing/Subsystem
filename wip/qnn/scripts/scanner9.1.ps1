<#
.SYNOPSIS
    QnnPlaintextSniper.ps1 - The Direct Memory Anchor
.DESCRIPTION
    Bypasses all heuristics. Uses a Known-Plaintext Attack to search the 
    metadata region for the exact Absolute Pointers, Relative Pointers, 
    and Known SD 1.5 Tensor Sizes. Dumps the surrounding hex to visually 
    expose the Qualcomm compiler's exact OpDescriptor struct layout.
#>

param([string]$BinPath = "C:\bin\qnn\test-harness-s23-v73\unet.bin")

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;
using System.Collections.Generic;

public static class QnnPlaintextSniper {
    public static unsafe void Hunt(byte[] data) {
        int limit = Math.Min(data.Length, 12000000); // 12MB Header Region
        
        // The exact start offsets of the massive blobs from your TSV
        uint[] absolutePointers = {
            0x075E3E58, // 371MB Megablob
            0x00CAE058, // 15MB Blob
            0x0400FE58, // 31MB Blob
            0x06911058  // 13MB Blob
        };

        // Standard SD 1.5 layer byte sizes (unpadded)
        uint[] knownSizes = { 
            409600,  // 1280 x 320  (time_embedding)
            921600,  // 320 x 320 x 3 x 3 (resnet convs)
            1638400, // 1280 x 1280 (time_embedding)
            3686400  // 640 x 640 x 3 x 3 (resnet convs)
        };

        Console.WriteLine("    Address    | Hex Dump                                        | UInt32 Values");
        Console.WriteLine("    ------------------------------------------------------------------------------------------");

        int ptrHits = 0;
        int sizeHits = 0;

        fixed (byte* p0 = data) {
            uint* p = (uint*)p0;
            int maxWords = limit / 4;

            for (int i = 0; i < maxWords; i++) {
                uint val = p[i];

                // 1. Check for Absolute Pointers
                foreach (uint target in absolutePointers) {
                    if (val == target && ptrHits < 5) {
                        Console.WriteLine($"\n[!] EXACT POINTER MATCH: 0x{target:X8} found at Header Offset 0x{i * 4:X8}");
                        PrintDump(data, i * 4);
                        ptrHits++;
                    }
                }

                // 2. Check for Relative Pointers (Base = 0x00081000)
                foreach (uint target in absolutePointers) {
                    if (val == (target - 0x00081000) && ptrHits < 5) {
                        Console.WriteLine($"\n[!] RELATIVE POINTER MATCH: 0x{val:X8} found at Header Offset 0x{i * 4:X8}");
                        PrintDump(data, i * 4);
                        ptrHits++;
                    }
                }

                // 3. Check for Known SD 1.5 Tensor Sizes
                foreach (uint target in knownSizes) {
                    if (val == target && sizeHits < 5) {
                        Console.WriteLine($"\n[!] KNOWN SIZE MATCH: {target} bytes (0x{target:X8}) found at Header Offset 0x{i * 4:X8}");
                        PrintDump(data, i * 4);
                        sizeHits++;
                    }
                }
            }
        }
        
        if (ptrHits == 0 && sizeHits == 0) {
            Console.WriteLine("\n[!] No direct matches found. Pointers may be scaled or obfuscated.");
        }
    }

    private static void PrintDump(byte[] data, int hitOffset) {
        int start = Math.Max(0, hitOffset - 16);
        int end = Math.Min(data.Length, hitOffset + 32);
        
        for (int i = start; i < end; i += 16) {
            string hex = "";
            string ints = "";
            for (int j = 0; j < 16; j++) {
                if (i + j < data.Length) {
                    hex += data[i + j].ToString("X2") + " ";
                    if (j % 4 == 3) {
                        uint val = BitConverter.ToUInt32(data, i + j - 3);
                        ints += $"{val,10} ";
                    }
                } else {
                    hex += "   ";
                }
            }
            string marker = (i <= hitOffset && i + 15 >= hitOffset) ? ">>" : "  ";
            Console.WriteLine($" {marker} 0x{i:X8} | {hex,-47} | {ints}");
        }
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnPlaintextSniper').Type) {
    Write-Host "[*] Compiling C# Plaintext Sniper..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

Write-Host "[*] Loading Binary Payload: $BinPath" -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($BinPath)

Write-Host "[*] Sniping Metadata Header for Direct Object Anchors..." -ForegroundColor Cyan
[QnnPlaintextSniper]::Hunt($bytes)