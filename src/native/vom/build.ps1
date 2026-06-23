# build.ps1 — compile + self-test the C VOM kernel (vom.h / vom.c / vom_test.c). Drive-portable.
#
# The C side of the dual-language VOM: the parity seam (a flat C ABI) any language drives over one heap.
# Needs a COMPLETE MSVC (full VS install has the Windows SDK libs; the W:\bin toolchain Kit has headers
# but no um libs, so its vcvars can't link). Override the env discovery with $env:SS_VCVARS.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path ([IO.Path]::GetTempPath()) 'ss-vom'
New-Item -ItemType Directory -Force $out | Out-Null

$vc = @(
    $env:SS_VCVARS,
    'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $vc) { throw "no complete vcvars64.bat found (set `$env:SS_VCVARS to a full VS install's)" }

$exe = Join-Path $out 'vom_test.exe'
cmd /c "`"$vc`" >nul 2>&1 && cl /nologo /W3 /Fe:`"$exe`" /Fo:`"$out\\`" `"$root\vom.c`" `"$root\vom_test.c`""
if ($LASTEXITCODE) { throw "cl failed ($LASTEXITCODE)" }
Write-Host "built -> $exe  (vcvars: $vc)"
& $exe                                  # prints the receipt; sets $LASTEXITCODE (0 = pass)
