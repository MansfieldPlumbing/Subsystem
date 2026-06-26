# Restore-DepthTRT.ps1
# Rebuilds the Depth_TRT repo using the standard deployment architecture.
# Strips emojis and video-specific dependencies (MF/FFmpeg).

$repo = @{}

$repo['.gitignore'] = @'
bin/
obj/
dist/
*.dll
*.exe
*.lib
*.exp
*.obj
*.pdb
config.ini
models/*
!models/.gitkeep
*.engine
*.onnx
'@

$repo['.gitattributes'] = @'
models/*.engine filter=lfs diff=lfs merge=lfs -text
models/*.onnx filter=lfs diff=lfs merge=lfs -text
'@

$repo['launch.bat'] = @'
@echo off
:: Depth TRT - First-run launcher
:: MOTW immune - elevates once, unblocks everything, then opens setup.ps1 menu.

echo.
echo  Depth TRT
echo  Requesting elevation to unblock downloaded scripts...
echo.

powershell -ExecutionPolicy Bypass -Command ^
  "Start-Process pwsh -ArgumentList '-ExecutionPolicy Bypass -File ""%~dp0setup.ps1""' -Verb RunAs -Wait"

echo.
echo  Done. You can close this window.
pause
'@

$repo['README.md'] = @'
# Depth TRT

Depth Anything V2 compiled to TensorRT for native Windows inference.
High-throughput, in-memory pipeline. No Python at runtime.

---

## Quick Start

```powershell
.\Depth_TRT.exe "image.jpg"                  # outputs image_depth.png
.\Depth_TRT.exe "image.jpg" -o "custom.png"  
.\Depth_TRT.exe "image.jpg" --no-invert      # disparity convention (near=dark)
```

---

## Engine

This repo ships no pre-built engine — engines are GPU-architecture-specific.

**Build your engine once with trtexec:**

```powershell
# Build engine for sm86 (RTX 3090/3080/3070/3060 Ti)
trtexec `
  --onnx="models\depth_anything_v2_vits.onnx" `
  --saveEngine="models\depth_v2_vits_sm86_trt10.15.engine" `
  --fp16 `
  --useCudaGraph --noDataTransfers --noTF32
```

Build takes a few minutes. Run once, reuse forever. The application will auto-discover the `.engine` file if placed in the `models\` directory.

---

## Performance & Architecture

This project achieves maximum execution speed by implementing a native In-Memory Pipeline:

1. **Parallel Tensor Reshaping**: Unsafe C# `Parallel.For` threads and `LockBits` instantly transpose the packed RGB data into the normalized CHW `float32` tensors required by the TensorRT graph. ImageNet normalization is baked directly into the unmanaged C++ bridge.
2. **Asynchronous Execution**: The TRT graph executes over the target interval via `cudaMemcpyAsync`, overlapping PCIe data transfers with GPU compute.
3. **100% Native**: Built purely on C++ and C# interoperability (`P/Invoke`). No Python environment overhead.

---

## Build

```powershell
.\launch.bat   # or: pwsh -File setup.ps1
```

Menu:
```text
  [1]  Unblock scripts
  [2]  Preflight checks     (validates CUDA, TensorRT, MSVC, .NET)
  [3]  Install dependencies (winget-installable items)
  [5]  Build                (compiles depth_trt.dll + Depth_TRT.exe)
```

---

## Requirements

| Dependency | Version | Notes |
|---|---|---|
| NVIDIA Driver | >= 561.0 | https://www.nvidia.com/drivers |
| CUDA Toolkit | >= 13.0 | https://developer.nvidia.com/cuda-downloads |
| TensorRT SDK | >= 10.0 | https://developer.nvidia.com/tensorrt - zip extract |
| VS Build Tools 2022 | C++ workload | `winget install Microsoft.VisualStudio.2022.BuildTools` |
| .NET SDK 9 | >= 9.0 | `winget install Microsoft.DotNet.SDK.9` |

---

## Author

**Mr. Mansfield** - [github.com/MansfieldPlumbing](https://github.com/MansfieldPlumbing)

> *I fix the pipes.*
'@

$repo['setup.ps1'] = @'
#Requires -Version 7.5
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion -lt [Version]"7.5") {
    Write-Host "`n  [FAIL] PowerShell $($PSVersionTable.PSVersion) detected." -ForegroundColor Red
    Write-Host "  Depth TRT requires PowerShell 7.5 or later.`n" -ForegroundColor Red
    Write-Host "  winget install Microsoft.PowerShell" -ForegroundColor White
    pause; exit 1
}

$ProjectRoot = $PSScriptRoot
$ConfigFile  = "$ProjectRoot\config.ini"
$ScriptsDir  = "$ProjectRoot\scripts"

$Manifest = [ordered]@{
    winget = [PSCustomObject]@{ Label = 'winget'; MinVersion = [Version]'1.0'; WingetId = $null; WingetArgs = $null; Url = 'https://aka.ms/getwinget'; Note = $null }
    driver = [PSCustomObject]@{ Label = 'NVIDIA Driver'; MinVersion = [Version]'561.0'; WingetId = $null; WingetArgs = $null; Url = 'https://www.nvidia.com/drivers'; Note = $null }
    cuda = [PSCustomObject]@{ Label = 'CUDA Toolkit'; MinVersion = [Version]'13.0'; WingetId = $null; WingetArgs = $null; Url = 'https://developer.nvidia.com/cuda-downloads'; Note = 'custom installer - not winget' }
    tensorrt = [PSCustomObject]@{ Label = 'TensorRT SDK'; MinVersion = [Version]'10.0'; WingetId = $null; WingetArgs = $null; Url = 'https://developer.nvidia.com/tensorrt'; Note = 'zip extract' }
    buildtools = [PSCustomObject]@{ Label = 'VS Build Tools'; MinVersion = [Version]'2022.0'; WingetId = 'Microsoft.VisualStudio.2022.BuildTools'; WingetArgs = '--quiet --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'; Url = 'https://aka.ms/vs/17/release/vs_buildtools.exe'; Note = 'C++ workload only' }
    dotnet = [PSCustomObject]@{ Label = '.NET SDK'; MinVersion = [Version]'9.0'; WingetId = 'Microsoft.DotNet.SDK.9'; WingetArgs = $null; Url = 'https://dotnet.microsoft.com/download/dotnet/9.0'; Note = $null }
}

$DllManifest = @(
    [PSCustomObject]@{ Name = 'nvinfer_10.dll';                       Source = 'tensorrt_bin'; Required = $true  }
    [PSCustomObject]@{ Name = 'nvinfer_plugin_10.dll';                Source = 'tensorrt_bin'; Required = $true  }
    [PSCustomObject]@{ Name = 'nvinfer_builder_resource_sm86_10.dll'; Source = 'tensorrt_bin'; Required = $true  }
    [PSCustomObject]@{ Name = 'nvinfer_lean_10.dll';                  Source = 'tensorrt_bin'; Required = $true  }
    [PSCustomObject]@{ Name = 'cudart64_13.dll';                      Source = 'cuda_bin';     Required = $true  }
    [PSCustomObject]@{ Name = 'cublas64_13.dll';                      Source = 'cuda_bin';     Required = $true  }
    [PSCustomObject]@{ Name = 'cublasLt64_13.dll';                    Source = 'cuda_bin';     Required = $true  }
    [PSCustomObject]@{ Name = 'msvcp140.dll';                         Source = 'system';       Required = $true  }
    [PSCustomObject]@{ Name = 'vcruntime140.dll';                     Source = 'system';       Required = $true  }
    [PSCustomObject]@{ Name = 'vcruntime140_1.dll';                   Source = 'system';       Required = $true  }
)

function Read-Config {
    $cfg = @{}
    if (Test-Path $ConfigFile) {
        $section = ''
        Get-Content $ConfigFile | ForEach-Object {
            $line = $_.Trim()
            if ($line -match '^\[(.+)\]$') { $section = $Matches[1] }
            elseif ($line -match '^([^;=]+)=(.*)$') { $cfg["$section.$($Matches[1].Trim())"] = $Matches[2].Trim() }
        }
    }
    return $cfg
}

function Test-PreflightPassed { return (Read-Config)['machine.preflight_passed'] -eq 'true' }

function Get-PreflightStatus {
    if (-not (Test-Path $ConfigFile)) { return 'never' }
    $val = (Read-Config)['machine.preflight_passed']
    if ($val -eq 'true') { return 'passed' }
    if ($val -eq 'false') { return 'failed' }
    return 'never'
}

function Build-Paths {
    $cfg = Read-Config
    return [PSCustomObject]@{
        ProjectRoot = $ProjectRoot
        ConfigFile  = $ConfigFile
        ScriptsDir  = $ScriptsDir
        CudaRoot    = $cfg['machine.cuda_root']
        CudaBin     = $cfg['machine.cuda_bin']
        TrtRoot     = $cfg['machine.tensorrt_root']
        TrtBin      = $cfg['machine.tensorrt_bin']
        SrcCpp      = "$ProjectRoot\src\depth_trt.cpp"
        OutDll      = "$ProjectRoot\depth_trt.dll"
    }
}

function Show-Banner {
    Clear-Host
    Write-Host "`n  --------------------------------------------------------" -ForegroundColor Cyan
    Write-Host "                                                        " -ForegroundColor Cyan
    Write-Host "     DEPTH TRT                                          " -ForegroundColor Cyan
    Write-Host "     Native Frame Inference . Depth Anything V2         " -ForegroundColor Cyan
    Write-Host "                                                        " -ForegroundColor Cyan
    Write-Host "  --------------------------------------------------------`n" -ForegroundColor Cyan
}

function Show-Menu {
    $status = Get-PreflightStatus
    $preflightTag = switch ($status) { 'passed' { "  [+] passed" }; 'failed' { "  [x] last run failed" }; default { "  [!] not yet run" } }
    $buildColor = if ($status -eq 'passed') { 'White' } else { 'DarkGray' }
    $buildTag   = if ($status -eq 'passed') { "" } else { "  [!] preflight required" }

    Write-Host "  What would you like to do?`n" -ForegroundColor Cyan
    Write-Host "    [1]  Unblock scripts" -ForegroundColor White
    Write-Host "    [2]  Preflight checks$preflightTag" -ForegroundColor White
    Write-Host "    [3]  Install dependencies" -ForegroundColor White
    Write-Host "    [5]  Build$buildTag" -ForegroundColor $buildColor
    Write-Host "    [Q]  Quit`n" -ForegroundColor DarkGray
}

function Invoke-Unblock {
    Write-Host "`n  -- [1] Unblock Scripts -------------------------------------------------`n" -ForegroundColor Cyan
    $files = Get-ChildItem $ProjectRoot -Recurse -Include "*.ps1","*.bat" -ErrorAction SilentlyContinue
    $count = 0
    foreach ($f in $files) { try { Unblock-File $f.FullName -ErrorAction SilentlyContinue; $count++ } catch {} }
    Write-Host "  [+] $count files unblocked.`n" -ForegroundColor Green
    Start-Sleep -Milliseconds 600
}

function Invoke-Preflight { Write-Host ""; & "$ScriptsDir\setup_preflight.ps1" -ProjectRoot $ProjectRoot -ConfigFile $ConfigFile -Manifest $Manifest }
function Invoke-InstallDeps { Write-Host ""; & "$ScriptsDir\setup_deps.ps1" -Manifest $Manifest; Write-Host "`n  Press any key..."; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") }

function Invoke-Build {
    if (-not (Test-PreflightPassed)) { Write-Host "`n  [!] Run [2] Preflight first.`n" -ForegroundColor Red; pause; return }
    Write-Host ""
    $Paths = Build-Paths
    & "$ScriptsDir\build_depth_trt.ps1" -Paths $Paths -DllManifest $DllManifest
    Write-Host "`n  Press any key..."; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}

do {
    Show-Banner
    Show-Menu
    $choice = Read-Host "  Enter selection"
    switch ($choice.Trim().ToUpper()) {
        "1" { Invoke-Unblock }
        "2" { Invoke-Preflight }
        "3" { Invoke-InstallDeps }
        "5" { Invoke-Build }
        "Q" { Write-Host "  Bye.`n" -ForegroundColor DarkGray; exit 0 }
        default { Write-Host "  Invalid selection." -ForegroundColor Red; Start-Sleep -Seconds 1 }
    }
} while ($true)
'@

$repo['scripts\setup_preflight.ps1'] = @'
#Requires -Version 7.5
param([Parameter(Mandatory)][string] $ProjectRoot, [Parameter(Mandatory)][string] $ConfigFile, [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary] $Manifest)
$ErrorActionPreference = "Continue"

trap { Write-Host "`n  [x] Preflight hit an unexpected error:`n  $_" -ForegroundColor Red; exit 1 }

function Write-Check {
    param([string]$Label, [bool]$Ok, [string]$Detail = '', [string]$Hint = '', [switch]$Info)
    $pad = 22; $status = if ($Info) { "[i]" } elseif ($Ok) { "[+]" } else { "[x]" }; $color = if ($Info) { 'Cyan' } elseif ($Ok) { 'Green' } else { 'Red' }
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

Write-Host "`n  -- [2] Preflight Checks ------------------------------------------------`n" -ForegroundColor Cyan

# 1. WINGET
$wingetOk = $false; $wingetVer = ''
try { if (Get-Command winget -ErrorAction SilentlyContinue) { $wingetVer = (winget --version 2>&1) -replace '[a-zA-Z]',''; $wingetOk = $true } } catch {}
Write-Check 'winget' $wingetOk $(if ($wingetOk) { "v$wingetVer" } else { 'not found (optional)' }) -Info

# 2. NVIDIA DRIVER
Write-Host ""
$driverOk = $false; $driverVer = ''; $gpuName = ''
try { if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { $smi = nvidia-smi --query-gpu=driver_version,name --format=csv,noheader 2>&1 | Select-Object -First 1; if ($smi -match '^([\d\.]+),\s*(.+)$') { $driverVer = $Matches[1].Trim(); $gpuName = $Matches[2].Trim(); $driverOk = [Version]$driverVer -ge $Manifest['driver'].MinVersion } } } catch {}
Write-Check 'NVIDIA Driver' $driverOk $(if ($driverOk) { "$driverVer   $gpuName" } elseif ($driverVer) { "$driverVer (need >= $($Manifest['driver'].MinVersion))" } else { 'nvidia-smi not found' })
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
Write-Check 'CUDA Toolkit' $cudaOk $(if ($cudaOk) { "$cudaVer   $cudaBin" } elseif ($cudaVer) { "$cudaVer (need >= $($Manifest['cuda'].MinVersion))" } else { 'not found' })
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
Write-Check 'TensorRT' $trtOk $(if ($trtOk) { "$trtVer   $trtRoot" } elseif ($trtVer) { "$trtVer (need >= 10.0)" } else { 'not found' })
if (-not $trtOk) { $allPassed = $false } else { if (-not (Test-InPath $trtBin)) { Add-ToUserPath @($trtBin) | Out-Null }; $cfg['tensorrt_root'] = $trtRoot; $cfg['tensorrt_bin'] = $trtBin; $cfg['tensorrt_lib'] = $trtLib }

# 5. VS BUILD TOOLS
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

# 6. .NET SDK
Write-Host ""
$dotnetOk = $false; $dotnetVer = ''
try { if (Get-Command dotnet -ErrorAction SilentlyContinue) { if ((dotnet --version 2>&1) -match '(\d+\.\d+)') { $dotnetVer = $Matches[1]; $dotnetOk = [Version]$dotnetVer -ge $Manifest['dotnet'].MinVersion } } } catch {}
Write-Check '.NET SDK' $dotnetOk $(if ($dotnetOk) { $dotnetVer } elseif ($dotnetVer) { "$dotnetVer (need >= 9.0)" } else { 'not found' })
if (-not $dotnetOk) { $allPassed = $false }

Write-Host "`n  ------------------------------------------------------------------------`n" -ForegroundColor DarkGray
if ($allPassed) {
    Write-Host "  [+] All checks passed.`n" -ForegroundColor Green
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    "[machine]`npreflight_passed=true`npreflight_date=$timestamp`ngpu_name=$($cfg['gpu_name'])`ndriver_version=$($cfg['driver_version'])`ncuda_root=$($cfg['cuda_root'])`ncuda_bin=$($cfg['cuda_bin'])`ncuda_lib=$($cfg['cuda_lib'])`ntensorrt_root=$($cfg['tensorrt_root'])`ntensorrt_bin=$($cfg['tensorrt_bin'])`ntensorrt_lib=$($cfg['tensorrt_lib'])`nvcvars=$($cfg['vcvars'])`n`n[runtime]`nTRT_BIN=$($cfg['tensorrt_bin'])`nCUDA_BIN=$($cfg['cuda_bin'])" | Set-Content $ConfigFile -Encoding UTF8
    Write-Host "  + config.ini written.`n`n  You can now proceed to [5] Build.`n" -ForegroundColor Cyan
} else {
    Write-Host "  [x] One or more checks failed. Resolve above and re-run [2] Preflight." -ForegroundColor Red
    if (Test-Path $ConfigFile) { (Get-Content $ConfigFile -Raw) -replace 'preflight_passed\s*=\s*true', 'preflight_passed = false' | Set-Content $ConfigFile -Encoding UTF8 }
}
Write-Host "  Press any key to return to menu..." -ForegroundColor DarkGray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
'@

$repo['scripts\setup_deps.ps1'] = @'
#Requires -Version 7.5
param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary] $Manifest)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "  -- [3] Install Dependencies --------------------------------------------`n" -ForegroundColor Cyan
Write-Host "  Manual installs (links only):`n" -ForegroundColor DarkGray
foreach ($key in @('driver', 'cuda', 'tensorrt')) {
    $dep = $Manifest[$key]
    Write-Host "  $($dep.Label.PadRight(22)) $($dep.Url)" -ForegroundColor White
    if ($dep.Note) { Write-Host "  $(' ' * 22)   -> $($dep.Note)" -ForegroundColor DarkGray }
    Write-Host ""
}

Write-Host "  -- Install via winget --------------------------------------------------`n" -ForegroundColor DarkGray
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Write-Host "  [x] winget not found." -ForegroundColor Red; return }

foreach ($key in ($Manifest.Keys | Where-Object { $Manifest[$_].WingetId })) {
    $dep = $Manifest[$key]; $alreadyInstalled = $false
    switch ($key) {
        'buildtools' { if (Test-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe") { $alreadyInstalled = [bool](& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 2>$null) } }
        'dotnet' { if (Get-Command dotnet -ErrorAction SilentlyContinue) { if ((dotnet --version 2>&1) -match '(\d+)\.' -and [int]$Matches[1] -ge $dep.MinVersion.Major) { $alreadyInstalled = $true } } }
    }
    $tag = if ($alreadyInstalled) { " (already installed)" } else { "" }
    Write-Host "  $($dep.Label)$tag" -ForegroundColor $(if ($alreadyInstalled) { 'Green' } else { 'White' })
    if (-not $alreadyInstalled) {
        $answer = Read-Host "  Install $($dep.Label) now? [Y/N]"
        if ($answer.Trim().ToUpper() -eq 'Y') {
            $cmd = "winget install --id $($dep.WingetId) --accept-package-agreements --accept-source-agreements"
            if ($dep.WingetArgs) { $cmd += " --override `"$($dep.WingetArgs)`"" }
            Invoke-Expression $cmd; Write-Host "  [+] Done.`n" -ForegroundColor Green
        } else { Write-Host "  Skipped.`n" -ForegroundColor DarkGray }
    } else { Write-Host "" }
}
Write-Host "  After installing, run [2] Preflight to validate." -ForegroundColor Cyan
'@

$repo['scripts\build_depth_trt.ps1'] = @'
#Requires -Version 7.5
param([Parameter(Mandatory)][PSCustomObject] $Paths, [Parameter(Mandatory)][array] $DllManifest)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectRoot = $Paths.ProjectRoot
$TrtRoot     = $Paths.TrtRoot
$TrtBin      = $Paths.TrtBin
$CudaRoot    = $Paths.CudaRoot
$CudaBin     = $Paths.CudaBin
$SrcFile     = $Paths.SrcCpp
$OutDll      = $Paths.OutDll
$NvToolkitRoot = 'C:\Program Files\NVIDIA GPU Computing Toolkit'

Write-Host "  -- [5] Build -----------------------------------------------------------`n" -ForegroundColor Cyan
Write-Host "  [5.1] DLL manifest check..." -ForegroundColor Yellow`n

function Find-DllRecursive ([string]$DllName) {
    if (-not (Test-Path $NvToolkitRoot)) { return $null }
    return Get-ChildItem -Path $NvToolkitRoot -Filter $DllName -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
}

$dllResults = @(); $dllMissing = 0; $dllTotal = $DllManifest.Count
$nameWidth = ($DllManifest | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum + 2

foreach ($dll in $DllManifest) {
    $sourcePath = switch ($dll.Source) { 'tensorrt_bin' { $TrtBin }; 'cuda_bin' { $CudaBin }; 'system' { "$env:SystemRoot\System32" }; default { $null } }
    $found = $false; $fullPath = ''; $note = ''

    if ($sourcePath -and (Test-Path $sourcePath)) {
        $candidate = Join-Path $sourcePath $dll.Name
        if (Test-Path $candidate) { $found = $true; $fullPath = $candidate }
    }

    if (-not $found -and $dll.Source -ne 'system') {
        $hit = Find-DllRecursive $dll.Name
        if ($hit) {
            $found = $true; $fullPath = $hit.FullName; $note = '  (found via scan)'
            if ($dll.Source -eq 'cuda_bin') { $script:CudaBin = $hit.DirectoryName }
            elseif ($dll.Source -eq 'tensorrt_bin') { $script:TrtBin = $hit.DirectoryName }
        }
    }

    $status = if ($found) { "[+]" } else { "[x]" }
    $color  = if ($found) { 'Green' } else { 'Red' }
    $detail = if ($found) { "$fullPath$note" } else { "not found  [config: $sourcePath]" }
    Write-Host "  $status  $($dll.Name.PadRight($nameWidth)) $detail" -ForegroundColor $color

    if (-not $found -and $dll.Required) { $dllMissing++ }
    $dllResults += [PSCustomObject]@{ Dll = $dll; Found = $found; Path = $fullPath }
}

if ($dllMissing -gt 0) { Write-Host "`n  [x] Missing required DLLs. Resolve and re-run Preflight." -ForegroundColor Red; return }

Write-Host "`n  [5.2] Locating build tools..." -ForegroundColor Yellow
$vcvars64 = $null
$cfgVcvars = $Paths.PSObject.Properties['Vcvars']?.Value
if (-not $cfgVcvars -and (Test-Path $Paths.ConfigFile)) {
    $cfgVcvars = (Get-Content $Paths.ConfigFile | Where-Object { $_ -match '^\s*vcvars\s*=' } | Select-Object -First 1) -replace '^\s*vcvars\s*=\s*', ''
}
if ($cfgVcvars -and $cfgVcvars -ne 'already active' -and (Test-Path $cfgVcvars.Trim())) { $vcvars64 = $cfgVcvars.Trim() } 
else { $vcvars64 = "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" }

$nvinferLib = Get-ChildItem -Path $TrtRoot -Filter "nvinfer_10.lib" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nvinferLib) { Write-Host "  [x] nvinfer_10.lib not found."; return }
$incTrt = "$TrtRoot\include"
$libTrt = $nvinferLib.DirectoryName
$cudaRootResolved = Split-Path $script:CudaBin
$incCuda = "$cudaRootResolved\include"
$libCuda = "$cudaRootResolved\lib\x64"

Write-Host "`n  [5.3] Building depth_trt.dll..." -ForegroundColor Yellow
$clCmd = "cl /LD /O2 /EHsc `"$SrcFile`" /I `"$incTrt`" /I `"$incCuda`" /link /LIBPATH:`"$libTrt`" /LIBPATH:`"$libCuda`" nvinfer_10.lib cudart.lib /OUT:`"$OutDll`""
cmd /c "cd /d `"$ProjectRoot`" && `"$vcvars64`" > nul && $clCmd"
if (-not (Test-Path $OutDll)) { Write-Host "  [x] depth_trt.dll build failed." -ForegroundColor Red; return }

Write-Host "`n  [5.4] Building Depth_TRT.exe..." -ForegroundColor Yellow
$distDir = "$ProjectRoot\dist"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
dotnet publish "$ProjectRoot\src\Depth_TRT.csproj" -c Release -r win-x64 -o $distDir
if (-not (Test-Path "$distDir\Depth_TRT.exe")) { Write-Host "  [x] C# build failed." -ForegroundColor Red; return }
Copy-Item "$distDir\Depth_TRT.exe" -Destination $ProjectRoot -Force

Write-Host "`n  [5.5] Bundling DLLs..." -ForegroundColor Yellow
foreach ($r in $dllResults | Where-Object { $_.Found -and $_.Dll.Source -ne 'system' }) {
    Copy-Item $r.Path -Destination $ProjectRoot -Force
}

Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\src\obj" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\src\bin" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\*.lib" -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\*.exp" -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\*.obj" -ErrorAction SilentlyContinue

Write-Host "`n  --------------------------------------------------------" -ForegroundColor Green
Write-Host "  BUILD COMPLETE                                          " -ForegroundColor Green
Write-Host "  --------------------------------------------------------" -ForegroundColor Green
Write-Host "`n  Run it:" -ForegroundColor Cyan
Write-Host "    .\Depth_TRT.exe `"image.jpg`"" -ForegroundColor White
Write-Host "    .\Depth_TRT.exe `"image.jpg`" -o custom_depth.png" -ForegroundColor White
Write-Host ""
'@

$repo['scripts\publish_release.ps1'] = @'
#Requires -Version 7.5
param([PSCustomObject]$Paths = $null)

if (-not $Paths) {
    $ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path
    $ConfigFile  = "$ProjectRoot\config.ini"
    $TrtRoot = $null; $CudaRoot = $null; $TrtBin = $null; $CudaBin = $null

    if (Test-Path $ConfigFile) {
        Get-Content $ConfigFile | ForEach-Object {
            if ($_ -match "^tensorrt_root\s*=\s*(.+)$") { $TrtRoot  = $Matches[1].Trim() }
            if ($_ -match "^cuda_root\s*=\s*(.+)$")     { $CudaRoot = $Matches[1].Trim() }
            if ($_ -match "^TRT_BIN\s*=\s*(.+)$")       { $TrtBin   = $Matches[1].Trim() }
            if ($_ -match "^CUDA_BIN\s*=\s*(.+)$")      { $CudaBin  = $Matches[1].Trim() }
            if ($_ -match "^vcvars\s*=\s*(.+)$")        { $Vcvars   = $Matches[1].Trim() }
        }
    }
    
    $Paths = [PSCustomObject]@{ ProjectRoot = $ProjectRoot; ConfigFile = $ConfigFile; TrtRoot = $TrtRoot; CudaRoot = $CudaRoot; TrtBin = $TrtBin; CudaBin = $CudaBin; Vcvars = $Vcvars }
}

$ProjectRoot = $Paths.ProjectRoot
$vcvars64 = if ($Paths.Vcvars -and $Paths.Vcvars -ne 'already active') { $Paths.Vcvars } else { "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" }

Write-Host "`n------------------------------------------------------------" -ForegroundColor Cyan
Write-Host "  DEPTH TRT  -  Release Publisher" -ForegroundColor Cyan
Write-Host "------------------------------------------------------------"

Write-Host "`n  [1/4] Building C++ bridges..." -ForegroundColor Yellow
$incTrt = "$($Paths.TrtRoot)\include"; $libTrt = "$($Paths.TrtRoot)\lib"
$incCuda = "$($Paths.CudaRoot)\include"; $libCuda = "$($Paths.CudaRoot)\lib\x64"

cmd /c "cd /d `"$ProjectRoot`" && `"$vcvars64`" > nul && cl /LD /O2 /EHsc `"$ProjectRoot\src\depth_trt.cpp`" /I `"$incTrt`" /I `"$incCuda`" /link /LIBPATH:`"$libTrt`" /LIBPATH:`"$libCuda`" nvinfer_10.lib cudart.lib /OUT:`"$ProjectRoot\depth_trt.dll`""

Write-Host "`n  [2/4] Building C# executable..." -ForegroundColor Yellow
$distDir = "$ProjectRoot\dist"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
dotnet publish "$ProjectRoot\src\Depth_TRT.csproj" -c Release -r win-x64 -o $distDir
Copy-Item "$distDir\Depth_TRT.exe" -Destination $ProjectRoot -Force

Write-Host "`n  [3/4] Assembling deployment package..." -ForegroundColor Yellow
$Runtimes = @(
    @{ Dir = $Paths.TrtBin;  Name = "nvinfer_10.dll" },
    @{ Dir = $Paths.TrtBin;  Name = "nvinfer_plugin_10.dll" },
    @{ Dir = $Paths.CudaBin; Name = "cudart64_13.dll" }
)
foreach ($rt in $Runtimes) {
    if ($rt.Dir -and (Test-Path (Join-Path $rt.Dir $rt.Name))) { Copy-Item (Join-Path $rt.Dir $rt.Name) -Destination $ProjectRoot -Force }
}

Write-Host "`n  [4/4] Cleaning build artifacts..." -ForegroundColor Gray
Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\*.lib" -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\*.exp" -ErrorAction SilentlyContinue
Remove-Item "$ProjectRoot\*.obj" -ErrorAction SilentlyContinue

Write-Host "`n  SUCCESS  -  Deployment package ready.`n" -ForegroundColor Green
'@


$repo['src\Program.cs'] = @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Depth_TRT;

class Program
{
    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr Depth_Init(string enginePath);

    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int Depth_Infer(IntPtr ctx, float[] rgbChw518, float[] depthOut);

    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void Depth_Normalize(float[] depth, int count, int invert);

    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void Depth_Destroy(IntPtr ctx);

    const int SIZE = 518;
    const int PLANE = SIZE * SIZE;

    static int Main(string[] args)
    {
        ConfigureEnvironment();
        var cfg = ParseArgs(args);
        if (cfg == null) { PrintUsage(); return 1; }

        if (!File.Exists(cfg.InputPath)) { Console.WriteLine($"[Error] Input not found: {cfg.InputPath}"); return 1; }
        if (!File.Exists(cfg.EnginePath)) { Console.WriteLine($"[Error] Engine not found: {cfg.EnginePath}"); return 1; }

        Console.WriteLine("[App] Loading TensorRT Engine...");
        IntPtr ctx = Depth_Init(cfg.EnginePath);
        if (ctx == IntPtr.Zero)
        {
            Console.WriteLine("[Error] Failed to initialize TensorRT.");
            return 1;
        }

        try
        {
            Console.WriteLine($"[App] Processing {Path.GetFileName(cfg.InputPath)}...");
            var sw = Stopwatch.StartNew();

            // 1. Load and Resize image
            using Bitmap origBmp = new Bitmap(cfg.InputPath);
            using Bitmap resizeBmp = new Bitmap(origBmp, new Size(SIZE, SIZE));

            // 2. Extract to CHW [0,1] floats
            float[] inputCHW = new float[3 * PLANE];
            BitmapData data = resizeBmp.LockBits(new Rectangle(0, 0, SIZE, SIZE), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < SIZE; y++)
                {
                    for (int x = 0; x < SIZE; x++)
                    {
                        int idx = y * SIZE + x;
                        // Format24bppRgb is actually BGR in memory
                        byte b = ptr[y * stride + x * 3 + 0];
                        byte g = ptr[y * stride + x * 3 + 1];
                        byte r = ptr[y * stride + x * 3 + 2];
                        
                        inputCHW[idx]             = r / 255f;
                        inputCHW[PLANE + idx]     = g / 255f;
                        inputCHW[PLANE * 2 + idx] = b / 255f;
                    }
                }
            }
            resizeBmp.UnlockBits(data);

            // 3. Inference
            float[] depthOut = new float[PLANE];
            int res = Depth_Infer(ctx, inputCHW, depthOut);
            if (res != 0)
            {
                Console.WriteLine($"[Error] Inference failed with code {res}");
                return 1;
            }

            // 4. Normalize depth
            Depth_Normalize(depthOut, PLANE, cfg.Invert);

            // 5. Reconstruct Grayscale Bitmap
            using Bitmap depthBmp = new Bitmap(SIZE, SIZE, PixelFormat.Format24bppRgb);
            BitmapData outData = depthBmp.LockBits(new Rectangle(0, 0, SIZE, SIZE), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            unsafe
            {
                byte* ptr = (byte*)outData.Scan0;
                int stride = outData.Stride;
                for (int y = 0; y < SIZE; y++)
                {
                    for (int x = 0; x < SIZE; x++)
                    {
                        byte val = (byte)Math.Clamp(depthOut[y * SIZE + x] * 255f, 0, 255);
                        ptr[y * stride + x * 3 + 0] = val;
                        ptr[y * stride + x * 3 + 1] = val;
                        ptr[y * stride + x * 3 + 2] = val;
                    }
                }
            }
            depthBmp.UnlockBits(outData);

            // 6. Scale back to original resolution and save
            using Bitmap finalOut = new Bitmap(depthBmp, origBmp.Width, origBmp.Height);
            finalOut.Save(cfg.OutputPath, ImageFormat.Png);
            
            sw.Stop();
            Console.WriteLine($"[App] Saved to {cfg.OutputPath} in {sw.ElapsedMilliseconds}ms");
            return 0;
        }
        finally
        {
            Depth_Destroy(ctx);
        }
    }

    static void ConfigureEnvironment() {
        string baseDir = AppContext.BaseDirectory;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            path = baseDir + Path.PathSeparator + path;

        string ini = Path.Combine(baseDir, "config.ini");
        if (File.Exists(ini)) {
            try {
                bool inRuntime = false;
                foreach (var line in File.ReadAllLines(ini)) {
                    string l = line.Trim();
                    if (l.StartsWith(';') || l.Length == 0) continue;
                    if (l.StartsWith('[') && l.EndsWith(']')) {
                        inRuntime = l.Equals("[runtime]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inRuntime) continue;
                    int eq = l.IndexOf('=');
                    if (eq < 1) continue;
                    string key = l[..eq].Trim();
                    string val = l[(eq + 1)..].Trim();
                    if (!string.IsNullOrEmpty(val) &&
                        (key.Equals("TRT_BIN", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("CUDA_BIN", StringComparison.OrdinalIgnoreCase)) &&
                        Directory.Exists(val) &&
                        !path.Contains(val, StringComparison.OrdinalIgnoreCase))
                    {
                        path += Path.PathSeparator + val;
                    }
                }
            } catch { }
        }
        Environment.SetEnvironmentVariable("PATH", path);
    }

    class Config { 
        public string InputPath = "", OutputPath = "", EnginePath = ""; 
        public int Invert = 1;
    }
    
    static Config? ParseArgs(string[] args) {
        if (args.Length < 1) return null;
        var cfg = new Config { InputPath = Path.GetFullPath(args[0]) };
        string ext = Path.GetExtension(cfg.InputPath);
        string noExt = Path.GetFileNameWithoutExtension(cfg.InputPath);
        string dir = Path.GetDirectoryName(cfg.InputPath) ?? ".";
        
        for (int i = 1; i < args.Length; i++) {
            switch (args[i].ToLower()) {
                case "-e": if (i+1 < args.Length) cfg.EnginePath = args[++i]; break;
                case "-o": if (i+1 < args.Length) cfg.OutputPath = args[++i]; break;
                case "--no-invert": cfg.Invert = 0; break;
            }
        }
        if (string.IsNullOrEmpty(cfg.OutputPath)) cfg.OutputPath = Path.Combine(dir, $"{noExt}_depth.png");
        
        if (string.IsNullOrEmpty(cfg.EnginePath)) {
            var found = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "models"), "*.engine");
            if (found.Length > 0) cfg.EnginePath = found[0];
        }
        return cfg;
    }

    static void PrintUsage() { 
        Console.WriteLine("Depth TRT (In-Memory Pipeline)");
        Console.WriteLine("Usage: Depth_TRT.exe input.jpg [-o output.png] [--no-invert]");
    }
}
'@

$repo['src\depth_trt.cpp'] = @'
// src/depth_trt.cpp
// Compiles to: depth_trt.dll
// Purpose: TensorRT inference bridge for Depth Anything V2.
//          Single-graph, fixed 518x518, FP32 I/O.
//          Designed for streaming use — Init once, Infer per frame, Destroy at end.
//
// IO Contract:
//   Input:  "image"  [1, 3, 518, 518]  FP32  RGB normalized [0,1]  CHW
//   Output: "depth"  [1, 1, 518, 518]  FP32  relative depth, raw model output
//
// Normalization:
//   Depth Anything V2 expects ImageNet normalization:
//   R = (r - 0.485) / 0.229
//   G = (g - 0.456) / 0.224
//   B = (b - 0.406) / 0.225
//   Applied in the bridge so the caller just passes raw [0,1] RGB.
//
// Output:
//   Raw depth values — caller decides how to normalize for display.
//   For SD-compatible grayscale: normalize to [0,1] then scale to [0,255].
//   Near = bright (high value), far = dark (low value) — standard depth convention.

#include <iostream>
#include <fstream>
#include <vector>
#include <algorithm>
#include <NvInfer.h>
#include <cuda_runtime_api.h>

using namespace nvinfer1;

// ---------------------------------------------------------------------------
// CONSTANTS
// ---------------------------------------------------------------------------
static constexpr int   MODEL_H   = 518;
static constexpr int   MODEL_W   = 518;
static constexpr int   IN_ELEMS  = 3 * MODEL_H * MODEL_W;
static constexpr int   OUT_ELEMS = 1 * MODEL_H * MODEL_W;

// ImageNet normalization constants
static constexpr float MEAN_R = 0.485f, MEAN_G = 0.456f, MEAN_B = 0.406f;
static constexpr float STD_R  = 0.229f, STD_G  = 0.224f, STD_B  = 0.225f;

// ---------------------------------------------------------------------------
// LOGGER
// ---------------------------------------------------------------------------
class Logger : public ILogger {
    void log(Severity s, const char* msg) noexcept override {
        if (s <= Severity::kWARNING)
            std::cout << "[TRT] " << msg << std::endl;
    }
} gLogger;

// ---------------------------------------------------------------------------
// CONTEXT
// ---------------------------------------------------------------------------
struct DepthCtx {
    IRuntime*          runtime  = nullptr;
    ICudaEngine*       engine   = nullptr;
    IExecutionContext* context  = nullptr;
    void*              d_input  = nullptr;   // GPU: [1,3,518,518] FP32
    void*              d_output = nullptr;   // GPU: [1,1,518,518] FP32
    float*             h_input  = nullptr;   // CPU pinned: normalized CHW
    float*             h_output = nullptr;   // CPU pinned: raw depth
    cudaStream_t       stream   = nullptr;
};

// ---------------------------------------------------------------------------
// CUDA ERROR CHECK HELPER
// Returns nullptr from the enclosing function on failure.
// ---------------------------------------------------------------------------
#define CUDA_CHECK(call)                                                      \
    do {                                                                      \
        cudaError_t _e = (call);                                              \
        if (_e != cudaSuccess) {                                              \
            std::cout << "[Depth] CUDA error at " << __FILE__                \
                      << ":" << __LINE__ << " - "                            \
                      << cudaGetErrorString(_e) << std::endl;                \
            delete ctx; return nullptr;                                       \
        }                                                                     \
    } while (0)

// ---------------------------------------------------------------------------
// INIT
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void* Depth_Init(const char* enginePath)
{
    auto* ctx = new DepthCtx();

    // Load engine file
    std::ifstream f(enginePath, std::ios::binary);
    if (!f.is_open()) {
        std::cout << "[Depth] ERROR: Cannot open engine: " << enginePath << std::endl;
        delete ctx; return nullptr;
    }
    std::vector<char> data((std::istreambuf_iterator<char>(f)), {});

    ctx->runtime = createInferRuntime(gLogger);
    if (!ctx->runtime) { delete ctx; return nullptr; }

    ctx->engine = ctx->runtime->deserializeCudaEngine(data.data(), data.size());
    if (!ctx->engine) {
        std::cout << "[Depth] ERROR: Failed to deserialize engine." << std::endl;
        delete ctx; return nullptr;
    }

    ctx->context = ctx->engine->createExecutionContext();
    if (!ctx->context) { delete ctx; return nullptr; }

    // Discover tensor names - print all of them, then match by position/name
    int nTensors = ctx->engine->getNbIOTensors();
    std::string inputName, outputName;

    std::cout << "[Depth] Engine tensors (" << nTensors << "):" << std::endl;
    for (int i = 0; i < nTensors; i++) {
        std::string name = ctx->engine->getIOTensorName(i);
        auto mode = ctx->engine->getTensorIOMode(name.c_str());
        bool isInput = (mode == TensorIOMode::kINPUT);
        std::cout << "  [" << i << "] " << (isInput ? "INPUT " : "OUTPUT") << "  \"" << name << "\"" << std::endl;
        if (isInput  && inputName.empty())  inputName  = name;
        if (!isInput && outputName.empty()) outputName = name;
        // Also accept exact legacy names
        if (name == "image") inputName  = name;
        if (name == "depth") outputName = name;
    }

    if (inputName.empty() || outputName.empty()) {
        std::cout << "[Depth] ERROR: Could not identify input/output tensors." << std::endl;
        delete ctx; return nullptr;
    }

    std::cout << "[Depth] Using input=\"" << inputName << "\"  output=\"" << outputName << "\"" << std::endl;

    // Allocate GPU buffers - every CUDA call is checked
    CUDA_CHECK(cudaMalloc(&ctx->d_input,  IN_ELEMS  * sizeof(float)));
    CUDA_CHECK(cudaMalloc(&ctx->d_output, OUT_ELEMS * sizeof(float)));
    CUDA_CHECK(cudaMallocHost((void**)&ctx->h_input,  IN_ELEMS  * sizeof(float)));
    CUDA_CHECK(cudaMallocHost((void**)&ctx->h_output, OUT_ELEMS * sizeof(float)));
    CUDA_CHECK(cudaStreamCreate(&ctx->stream));

    // Bind tensor addresses using discovered names
    ctx->context->setTensorAddress(inputName.c_str(),  ctx->d_input);
    ctx->context->setTensorAddress(outputName.c_str(), ctx->d_output);

    std::cout << "[Depth] Engine loaded. Input: [1,3,518,518]  Output: [1,1,518,518]" << std::endl;
    return ctx;
}

// ---------------------------------------------------------------------------
// INFER
// Input:  rgbChw518 - float[3 * 518 * 518], CHW, RGB, values [0,1]
// Output: depthOut  - float[518 * 518], raw depth values
// Returns 0 on success, non-zero on error.
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
int Depth_Infer(void* hCtx, float* rgbChw518, float* depthOut)
{
    auto* ctx = (DepthCtx*)hCtx;
    if (!ctx) return -1;

    // Apply ImageNet normalization into pinned host buffer
    int planeSize = MODEL_H * MODEL_W;
    float* rPlane = rgbChw518;
    float* gPlane = rgbChw518 + planeSize;
    float* bPlane = rgbChw518 + planeSize * 2;

    float* dstR = ctx->h_input;
    float* dstG = ctx->h_input + planeSize;
    float* dstB = ctx->h_input + planeSize * 2;

    for (int i = 0; i < planeSize; i++) {
        dstR[i] = (rPlane[i] - MEAN_R) / STD_R;
        dstG[i] = (gPlane[i] - MEAN_G) / STD_G;
        dstB[i] = (bPlane[i] - MEAN_B) / STD_B;
    }

    // H2D async
    cudaMemcpyAsync(ctx->d_input, ctx->h_input,
                    IN_ELEMS * sizeof(float),
                    cudaMemcpyHostToDevice, ctx->stream);

    // Inference
    if (!ctx->context->enqueueV3(ctx->stream)) return 1;

    // D2H async
    cudaMemcpyAsync(ctx->h_output, ctx->d_output,
                    OUT_ELEMS * sizeof(float),
                    cudaMemcpyDeviceToHost, ctx->stream);

    cudaStreamSynchronize(ctx->stream);

    memcpy(depthOut, ctx->h_output, OUT_ELEMS * sizeof(float));
    return 0;
}

// ---------------------------------------------------------------------------
// NORMALIZE DEPTH  (call after Depth_Infer)
// Normalizes raw depth map to [0,1] across the frame.
// invert=1 -> near=1.0 (bright), far=0.0  (SD convention, white=close)
// invert=0 -> near=0.0 (dark),   far=1.0  (disparity convention)
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void Depth_Normalize(float* depth, int count, int invert)
{
    float minV = depth[0], maxV = depth[0];
    for (int i = 1; i < count; i++) {
        if (depth[i] < minV) minV = depth[i];
        if (depth[i] > maxV) maxV = depth[i];
    }
    float range = maxV - minV;
    if (range < 1e-6f) range = 1e-6f;

    for (int i = 0; i < count; i++) {
        float v = (depth[i] - minV) / range;
        depth[i] = invert ? (1.0f - v) : v;
    }
}

// ---------------------------------------------------------------------------
// DESTROY
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void Depth_Destroy(void* hCtx)
{
    auto* ctx = (DepthCtx*)hCtx;
    if (!ctx) return;
    if (ctx->d_input)  cudaFree(ctx->d_input);
    if (ctx->d_output) cudaFree(ctx->d_output);
    if (ctx->h_input)  cudaFreeHost(ctx->h_input);
    if (ctx->h_output) cudaFreeHost(ctx->h_output);
    if (ctx->stream)   cudaStreamDestroy(ctx->stream);
    delete ctx->context;
    delete ctx->engine;
    delete ctx->runtime;
    delete ctx;
}
'@

$repo['src\Depth_TRT.csproj'] = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Depth_TRT</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.Drawing.Common" Version="9.0.0" />
  </ItemGroup>
</Project>
'@

Write-Host "Rebuilding Depth_TRT Repository Structure..." -ForegroundColor Cyan

foreach ($path in $repo.Keys) {
    $fullPath = Join-Path $PWD $path
    $dir = Split-Path $fullPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    Set-Content -Path $fullPath -Value $repo[$path] -Encoding UTF8
    Write-Host "  [+] Restored: $path" -ForegroundColor DarkGray
}

New-Item -ItemType Directory -Path (Join-Path $PWD "models") -Force | Out-Null
Set-Content -Path (Join-Path $PWD "models\.gitkeep") -Value "" -Encoding UTF8

Write-Host "`n[+] Repository successfully restored and matched to the RIFE deployment spec!" -ForegroundColor Green
Write-Host "`nRun .\launch.bat to verify your Preflight and trigger the Build." -ForegroundColor White