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
