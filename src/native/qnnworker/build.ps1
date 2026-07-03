#requires -Version 7
# build.ps1 — mm_qnn (the QNN Hexagon worker) for arm64 Android, staged as libqnnworker.so.
# Recipe from the 2026-06-30 receipt (S:\litert-dpx-research\qnn-receipt\qnn-htp-RECEIPT.md):
#   aarch64-linux-android21-clang++ -std=c++17 -O2 -static-libstdc++ -I <qairt>/include/QNN mm_qnn.cpp -o mm_qnn -ldl
# The .so name is deliberate: the installer extracts AndroidNativeLibrary items into
# ApplicationInfo.NativeLibraryDir WITH exec permission — the one path an app may exec from.
$ErrorActionPreference = 'Stop'

$ndk   = $env:SS_NDK  ?? 'S:\bin\ndk'
$qairt = $env:SS_QNN  ?? 'S:\qairt\2.42.0.251225'
$libs  = $env:SS_LIBS ?? 'S:\libs'
$clang = Join-Path $ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android21-clang++.cmd'
if (-not (Test-Path $clang)) { throw "NDK clang not found: $clang (set SS_NDK)" }
if (-not (Test-Path "$qairt\include\QNN")) { throw "QAIRT headers not found under $qairt (set SS_QNN)" }

$src = Join-Path $PSScriptRoot 'mm_qnn.cpp'
$out = Join-Path $PSScriptRoot 'mm_qnn'
& $clang -std=c++17 -O2 -static-libstdc++ -I "$qairt\include\QNN" $src -o $out -ldl
if ($LASTEXITCODE -ne 0) { throw "clang exit $LASTEXITCODE" }

$stage = Join-Path $libs 'arm64-v8a\libqnnworker.so'
Copy-Item $out $stage -Force
Write-Host "mm_qnn: built $((Get-Item $out).Length) bytes -> staged $stage"
