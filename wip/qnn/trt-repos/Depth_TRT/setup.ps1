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
