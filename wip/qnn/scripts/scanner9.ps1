<#
.SYNOPSIS
    QnnShapeSniper.ps1 - The Direct Shape Signature Locator
.DESCRIPTION
    Scans the metadata header for known Stable Diffusion 1.5 layer dimensions 
    (encoded as uint32 arrays). Dumps the surrounding memory block to visually 
    expose the Qualcomm compiler's exact OpDescriptor struct layout.
#>

param([string]$BinPath = "C:\bin\qnn\test-harness-s23-v73\unet.bin")

$ErrorActionPreference = 'Stop'

$csharpCode = @"
using System;

public static class QnnShapeSniper {
    public static unsafe void Hunt(byte[] data) {
        // Only scan the 12MB header region
        int limit = Math.Min(data.Length, 12000000); 
        
        fixed (byte* p0 = data) {
            uint* p = (uint*)p0;
            int maxWords = limit / 4;
            
            bool foundConv1 = false;
            bool foundConvIn = false;

            // Target 1: down_blocks_1_conv1[640, 320, 3, 3] in ANY order
            for (int i = 0; i < maxWords - 4; i++) {
                uint w1 = p[i], w2 = p[i+1], w3 = p[i+2], w4 = p[i+3];
                
                int c640 = 0, c320 = 0, c3 = 0;
                if (w1 == 640) c640++; else if (w1 == 320) c320++; else if (w1 == 3) c3++;
                if (w2 == 640) c640++; else if (w2 == 320) c320++; else if (w2 == 3) c3++;
                if (w3 == 640) c640++; else if (w3 == 320) c320++; else if (w3 == 3) c3++;
                if (w4 == 640) c640++; else if (w4 == 320) c320++; else if (w4 == 3) c3++;
                
                if (c640 == 1 && c320 == 1 && c3 == 2) {
                    PrintHit(data, i * 4, "down_blocks_1_conv1[640, 320, 3, 3]");
                    foundConv1 = true;
                    break;
                }
            }
            
            // Target 2: conv_in [320, 4, 3, 3] in ANY order
            for (int i = 0; i < maxWords - 4; i++) {
                uint w1 = p[i], w2 = p[i+1], w3 = p[i+2], w4 = p[i+3];
                
                int c320 = 0, c4 = 0, c3 = 0;
                if (w1 == 320) c320++; else if (w1 == 4) c4++; else if (w1 == 3) c3++;
                if (w2 == 320) c320++; else if (w2 == 4) c4++; else if (w2 == 3) c3++;
                if (w3 == 320) c320++; else if (w3 == 4) c4++; else if (w3 == 3) c3++;
                if (w4 == 320) c320++; else if (w4 == 4) c4++; else if (w4 == 3) c3++;
                
                if (c320 == 1 && c4 == 1 && c3 == 2) {
                    PrintHit(data, i * 4, "conv_in_Conv[320, 4, 3, 3]");
                    foundConvIn = true;
                    break;
                }
            }

            if (!foundConv1 && !foundConvIn) {
                Console.WriteLine("\n[!] Shapes not found. Dimensions may be encoded via reference.");
            }
        }
    }
    
    private static void PrintHit(byte[] data, int byteOffset, string label) {
        Console.WriteLine($"\n[!] SHAPE FOUND: {label} at Offset 0x{byteOffset:X8}");
        Console.WriteLine("    Dumping surrounding memory for Descriptor Struct Analysis:");
        Console.WriteLine("    Look for the OpId, Size, and MegaBlob Offset nearby.\n");
        
        int start = Math.Max(0, byteOffset - 48);
        int end = Math.Min(data.Length, byteOffset + 80);
        
        Console.WriteLine("    Address    | Hex Dump                                        | UInt32 Values");
        Console.WriteLine("    ------------------------------------------------------------------------------------------");
        
        for (int i = start; i < end; i += 16) {
            string hex = "";
            string ints = "";
            for (int j = 0; j < 16; j++) {
                if (i + j < data.Length) {
                    hex += data[i+j].ToString("X2") + " ";
                    if (j % 4 == 3) {
                        uint val = BitConverter.ToUInt32(data, i + j - 3);
                        ints += $"{val,10} ";
                    }
                }
            }
            string marker = (i <= byteOffset && i + 15 >= byteOffset) ? ">>" : "  ";
            Console.WriteLine($" {marker} 0x{i:X8} | {hex,-47} | {ints}");
        }
    }
}
"@

if (-not ([System.Management.Automation.PSTypeName]'QnnShapeSniper').Type) {
    Write-Host "[*] Compiling C# Shape Sniper..." -ForegroundColor DarkGray
    Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerOptions '/unsafe'
}

Write-Host "[*] Loading Binary Payload: $BinPath" -ForegroundColor Cyan
$bytes =[IO.File]::ReadAllBytes($BinPath)

Write-Host "[*] Sniping Metadata Header for SD 1.5 Tensors..." -ForegroundColor Cyan
[QnnShapeSniper]::Hunt($bytes)