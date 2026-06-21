# build.ps1 — compile the vendored DirectPort D3D12 sources into a standalone directport.dll
# that ss.exe P/Invokes. Fully S:-rooted: uses the on-drive MSVC + Windows SDK (SS_MSVC / SS_WINSDK
# / SS_WINSDK_VER from scripts/env.ps1) and sets INCLUDE/LIB/PATH directly — no vcvars64.bat.
# Usage:  pwsh -File build.ps1 [-OutDir <dir>]   (default OutDir = this folder)
[CmdletBinding()]
param([string]$OutDir = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Resolve the toolchain. Prefer the env vars (scripts/env.ps1); fall back to SS_BIN, then a literal.
$bin    = if ($env:SS_BIN) { $env:SS_BIN } else { 'S:\bin' }
$msvc   = if ($env:SS_MSVC)       { $env:SS_MSVC }       else { Join-Path $bin 'vs\VC\Tools\MSVC\14.51.36223' }
$sdkRt  = if ($env:SS_WINSDK)     { $env:SS_WINSDK }     else { Join-Path $bin 'Windows Kits\10' }
$sdkVer = if ($env:SS_WINSDK_VER) { $env:SS_WINSDK_VER } else { '10.0.26100.0' }

$clExe = Join-Path $msvc 'bin\Hostx64\x64\cl.exe'
if (-not (Test-Path $clExe)) { throw "cl.exe not found at $clExe (check `$env:SS_MSVC)" }

# Compose the include / lib search paths from the MSVC toolset + the Windows SDK (ucrt/shared/um).
$inc = @(
    (Join-Path $msvc  'include'),
    (Join-Path $sdkRt "Include\$sdkVer\ucrt"),
    (Join-Path $sdkRt "Include\$sdkVer\shared"),
    (Join-Path $sdkRt "Include\$sdkVer\um")
)
$lib = @(
    (Join-Path $msvc  'lib\x64'),
    (Join-Path $sdkRt "Lib\$sdkVer\ucrt\x64"),
    (Join-Path $sdkRt "Lib\$sdkVer\um\x64")
)
foreach ($p in ($inc + $lib)) { if (-not (Test-Path $p)) { throw "toolchain path missing: $p" } }

$env:INCLUDE = ($inc -join ';')
$env:LIB     = ($lib -join ';')
$env:PATH    = (Join-Path $msvc 'bin\Hostx64\x64') + ';' + $env:PATH

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$src = Join-Path $here 'directportd3d12.cpp'
$dll = Join-Path $OutDir 'directport.dll'

# /LD = DLL, /O2 = optimize, /EHsc = C++ exceptions, /MT = static CRT (no vcruntime/ucrt redist needed
# at ss.exe's deploy site). d3d12/advapi32 come from #pragma comment(lib,...) in the .cpp; kernel32 etc.
# from the default libs on the LIB path above.
Write-Host "cl: $clExe"
Write-Host "compiling directport.dll ..."
& $clExe /nologo /LD /O2 /EHsc /MT $src "/Fe:$dll" "/Fo:$OutDir\" /link /MACHINE:X64
if ($LASTEXITCODE -ne 0) { throw "cl failed with exit $LASTEXITCODE" }
if (-not (Test-Path $dll)) { throw "directport.dll was not produced" }

Write-Host "`nbuilt: $dll ($([math]::Round((Get-Item $dll).Length/1KB,1)) KB)"
Write-Host "exports:"
$dumpbin = Join-Path $msvc 'bin\Hostx64\x64\dumpbin.exe'
if (Test-Path $dumpbin) { & $dumpbin /nologo /exports $dll | Select-String 'dp12_|dp11_' }
else { Write-Host "(dumpbin not present; skipping export list)" }
