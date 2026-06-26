#Requires -Version 7.5
param([Parameter(Mandatory)][string] $ProjectRoot, [Parameter(Mandatory)][string] $ConfigFile, [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary] $Manifest)
$ErrorActionPreference = "Continue"

trap { Write-Host "`n  ❌ Preflight hit an unexpected error:`n  $_" -ForegroundColor Red; exit 1 }

function Write-Check {
    param([string]$Label, [bool]$Ok, [string]$Detail = '', [string]$Hint = '', [switch]$Info)
    $pad = 22; $status = if ($Info) { "ℹ " } elseif ($Ok) { "✅" } else { "❌" }; $color = if ($Info) { 'Cyan' } elseif ($Ok) { 'Green' } else { 'Red' }
    Write-Host "  $status  $($Label.PadRight($pad)) $Detail" -ForegroundColor $color
}
function Test-InPath ([string]$Dir) { return ([Environment]::GetEnvironmentVariable('PATH', 'Machine') -like "*$Dir*") -or ([Environment]::GetEnvironmentVariable('PATH', 'User') -like "*$Dir*") }
function Add-ToUserPath ([string[]]$Dirs) {
    $current = [Environment]::GetEnvironmentVariable('PATH', 'User')
    $toAdd   = @($Dirs | Where-Object { -not (Test-InPath $_) })
    if ($toAdd.Count -eq 0) { return $false }
    $newPath = ($current.TrimEnd(';') + ';' + ($toAdd -join ';')).TrimStart(';')
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    $env:PATH = $env:PATH.TrimEnd(';') + ';' + ($toAdd -join ';')
    return $true
}
function Get-ActualLibDir ([string]$Root, [string[]]$Candidates) { foreach ($c in $Candidates) { $full = Join-Path $Root $c; if (Test-Path $full) { return $full } }; return $null }

$NvToolkitRoot = 'C:\Program Files\NVIDIA GPU Computing Toolkit'
$allPassed = $true; $cfg = @{}

Write-Host "`n  ── [2] Preflight Checks ────────────────────────────────────────────────`n" -ForegroundColor Cyan

# 1. WINGET
$wingetOk = $false; $wingetVer = ''
try { if (Get-Command winget -ErrorAction SilentlyContinue) { $wingetVer = (winget --version 2>&1) -replace '[a-zA-Z]',''; $wingetOk = $true } } catch {}
Write-Check 'winget' $wingetOk $(if ($wingetOk) { "v$wingetVer" } else { 'not found (optional)' }) -Info

# 2. NVIDIA DRIVER
Write-Host ""
$driverOk = $false; $driverVer = ''; $gpuName = ''
try { if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { $smi = nvidia-smi --query-gpu=driver_version,name --format=csv,noheader 2>&1 | Select-Object -First 1; if ($smi -match '^([\d\.]+),\s*(.+)$') { $driverVer = $Matches[1].Trim(); $gpuName = $Matches[2].Trim(); $driverOk = [Version]$driverVer -ge $Manifest['driver'].MinVersion } } } catch {}
Write-Check 'NVIDIA Driver' $driverOk $(if ($driverOk) { "$driverVer   $gpuName" } elseif ($driverVer) { "$driverVer (need ≥ $($Manifest['driver'].MinVersion))" } else { 'nvidia-smi not found' })
if (-not $driverOk) { $allPassed = $false }
if ($gpuName)   { $cfg['gpu_name']       = $gpuName }
if ($driverVer) { $cfg['driver_version'] = $driverVer }

# 3. CUDA
Write-Host ""
$cudaOk = $false; $cudaVer = ''; $cudaRoot = ''; $cudaBin = ''; $cudaLib = ''
try { if ($nvccCmd = Get-Command nvcc -ErrorAction SilentlyContinue) { if ((nvcc --version 2>&1) -join "`n" -match 'release\s+(\d+\.\d+),') { $cudaVer = $Matches[1]; $cudaOk = [Version]$cudaVer -ge $Manifest['cuda'].MinVersion; $nvccDir = Split-Path $nvccCmd.Source; $cudaRoot = Split-Path $nvccDir; $cudaBin = if (Test-Path (Join-Path $cudaRoot 'bin\x64')) { Join-Path $cudaRoot 'bin\x64' } else { $nvccDir }; $cudaLib = Get-ActualLibDir $cudaRoot @('lib\x64', 'lib') } } } catch {}
if (-not $cudaOk -and (Test-Path $NvToolkitRoot)) {
    $cudaFolders = Get-ChildItem "$NvToolkitRoot\CUDA" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^v(\d+\.\d+)$' } | Where-Object { [Version]$Matches[1] -ge $Manifest['cuda'].MinVersion } | Sort-Object { [Version]($_.Name -replace '^v','') } -Descending
    foreach ($folder in $cudaFolders) {
        $candidate = $folder.FullName; $testBin = if (Test-Path (Join-Path $candidate 'bin\x64')) { Join-Path $candidate 'bin\x64' } else { Join-Path $candidate 'bin' }
        if (Get-ChildItem $testBin -Filter 'cudart64_*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1) { $cudaRoot = $candidate; $cudaBin = $testBin; $cudaLib = Get-ActualLibDir $cudaRoot @('lib\x64', 'lib'); $cudaVer = ($folder.Name -replace '^v',''); $cudaOk = $true; break }
    }
}
Write-Check 'CUDA Toolkit' $cudaOk $(if ($cudaOk) { "$cudaVer   $cudaBin" } elseif ($cudaVer) { "$cudaVer (need ≥ $($Manifest['cuda'].MinVersion))" } else { 'not found' })
if (-not $cudaOk) { $allPassed = $false } else { if (-not (Test-InPath (Join-Path $cudaRoot 'bin'))) { Add-ToUserPath @((Join-Path $cudaRoot 'bin')) | Out-Null }; $cfg['cuda_root'] = $cudaRoot; $cfg['cuda_bin'] = $cudaBin; $cfg['cuda_lib'] = $cudaLib }

# 4. TENSORRT
Write-Host ""
$trtOk = $false; $trtVer = ''; $trtRoot = ''; $trtBin = ''; $trtLib = ''
if (-not $trtOk -and (Test-Path $NvToolkitRoot)) {
    if ($nvinferHit = Get-ChildItem -Path $NvToolkitRoot -Filter 'nvinfer_10.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1) {
        $candidateRoot = Split-Path $nvinferHit.DirectoryName
        if (Test-Path (Join-Path $candidateRoot 'include\NvInferVersion.h')) { $h = Get-Content (Join-Path $candidateRoot 'include\NvInferVersion.h') -Raw; if ($h -match '#define\s+NV_TENSORRT_MAJOR\s+(\d+)' -and $h -match '#define\s+NV_TENSORRT_MINOR\s+(\d+)') { $trtVer = "$($Matches[1]).$($Matches[1]).0" } }
        if ($trtVer) { $trtOk = [Version](($trtVer -split '\.')[0..1] -join '.') -ge $Manifest['tensorrt'].MinVersion; if ($trtOk) { $trtRoot = $candidateRoot; $trtBin = $nvinferHit.DirectoryName; $trtLib = Get-ActualLibDir $trtRoot @('lib\x64', 'lib') } }
    }
}
Write-Check 'TensorRT' $trtOk $(if ($trtOk) { "$trtVer   $trtRoot" } elseif ($trtVer) { "$trtVer (need ≥ 10.0)" } else { 'not found' })
if (-not $trtOk) { $allPassed = $false } else { if (-not (Test-InPath $trtBin)) { Add-ToUserPath @($trtBin) | Out-Null }; $cfg['tensorrt_root'] = $trtRoot; $cfg['tensorrt_bin'] = $trtBin; $cfg['tensorrt_lib'] = $trtLib }

# 5. FFMPEG
Write-Host ""
$ffmpegOk = $false; $ffmpegVer = ''
try { if (Get-Command ffmpeg -ErrorAction SilentlyContinue) { $ffmpegOk = $true; $ffmpegVer = 'found' } } catch {}
Write-Check 'ffmpeg' $ffmpegOk $(if ($ffmpegOk) { $ffmpegVer } else { 'not found — winget install Gyan.FFmpeg' })
if (-not $ffmpegOk) { $allPassed = $false }

# 6. VS BUILD TOOLS
Write-Host ""
$btOk = $false; $vcvars = ''
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Get-Command cl.exe -ErrorAction SilentlyContinue) { $btOk = $true; $vcvars = 'already active'; Write-Check 'VS Build Tools' $true 'cl.exe in PATH' } 
elseif (Test-Path $vswhere) {
    foreach ($product in @('Microsoft.VisualStudio.Product.BuildTools','Microsoft.VisualStudio.Product.Community','Microsoft.VisualStudio.Product.Enterprise')) {
        try { $info = & $vswhere -latest -products $product -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue; if ($info -and $info.installationPath) { $candidate = "$($info.installationPath)\VC\Auxiliary\Build\vcvars64.bat"; if (Test-Path $candidate) { $vcvars = $candidate; $btOk = $true; break } } } catch {}
    }
    Write-Check 'VS Build Tools' $btOk $(if ($btOk) { $vcvars } else { 'C++ workload not found' })
} else { Write-Check 'VS Build Tools' $false 'not found' }
if (-not $btOk) { $allPassed = $false } else { $cfg['vcvars'] = $vcvars }

# 7. .NET SDK
Write-Host ""
$dotnetOk = $false; $dotnetVer = ''
try { if (Get-Command dotnet -ErrorAction SilentlyContinue) { if ((dotnet --version 2>&1) -match '(\d+\.\d+)') { $dotnetVer = $Matches[1]; $dotnetOk = [Version]$dotnetVer -ge $Manifest['dotnet'].MinVersion } } } catch {}
Write-Check '.NET SDK' $dotnetOk $(if ($dotnetOk) { $dotnetVer } elseif ($dotnetVer) { "$dotnetVer (need ≥ 9.0)" } else { 'not found' })
if (-not $dotnetOk) { $allPassed = $false }

Write-Host "`n  ────────────────────────────────────────────────────────────────────────`n" -ForegroundColor DarkGray
if ($allPassed) {
    Write-Host "  ✅ All checks passed.`n" -ForegroundColor Green
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    "[machine]`npreflight_passed=true`npreflight_date=$timestamp`ngpu_name=$($cfg['gpu_name'])`ndriver_version=$($cfg['driver_version'])`ncuda_root=$($cfg['cuda_root'])`ncuda_bin=$($cfg['cuda_bin'])`ncuda_lib=$($cfg['cuda_lib'])`ntensorrt_root=$($cfg['tensorrt_root'])`ntensorrt_bin=$($cfg['tensorrt_bin'])`ntensorrt_lib=$($cfg['tensorrt_lib'])`nvcvars=$($cfg['vcvars'])`n`n[runtime]`nTRT_BIN=$($cfg['tensorrt_bin'])`nCUDA_BIN=$($cfg['cuda_bin'])" | Set-Content $ConfigFile -Encoding UTF8
    Write-Host "  + config.ini written.`n`n  You can now proceed to [5] Build.`n" -ForegroundColor Cyan
} else {
    Write-Host "  ❌ One or more checks failed. Resolve above and re-run [2] Preflight." -ForegroundColor Red
    if (Test-Path $ConfigFile) { (Get-Content $ConfigFile -Raw) -replace 'preflight_passed\s*=\s*true', 'preflight_passed = false' | Set-Content $ConfigFile -Encoding UTF8 }
}
Write-Host "  Press any key to return to menu..." -ForegroundColor DarkGray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
