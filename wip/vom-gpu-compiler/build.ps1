# build.ps1 — Compile the GPU-accelerated VOM compiler shader (VomCompiler.hlsl)
#
# Searches for the Windows SDK DXC compiler and performs a test compilation.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 1. Discover dxc.exe in Windows Kits
$dxc = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter dxc.exe -Recurse -ErrorAction SilentlyContinue | 
       Where-Object { $_.FullName -like '*\x64\*' } | 
       Select-Object -First 1

if (-not $dxc) {
    Write-Warning "dxc.exe not found in Windows Kits. Searching in Program Files..."
    $dxc = Get-ChildItem -Path 'C:\Program Files' -Filter dxc.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
}

if (-not $dxc) {
    throw "dxc.exe (DirectX Shader Compiler) could not be located on this machine."
}

Write-Host "Found HLSL Compiler: $($dxc.FullName)"

# 2. Compile VomCompiler.hlsl
$shaderFile = Join-Path $root "VomCompiler.hlsl"
$outputBytecode = Join-Path $root "VomCompiler.dxil"

Write-Host "Compiling $shaderFile..."
& $dxc.FullName /T cs_6_0 /E CSMain /Fo $outputBytecode $shaderFile

if ($LASTEXITCODE -eq 0) {
    Write-Host "SUCCESS: Compiled shader written to $outputBytecode"
    $fileInfo = Get-Item $outputBytecode
    Write-Host "Compiled size: $($fileInfo.Length) bytes"
} else {
    throw "dxc.exe failed with exit code $LASTEXITCODE"
}
