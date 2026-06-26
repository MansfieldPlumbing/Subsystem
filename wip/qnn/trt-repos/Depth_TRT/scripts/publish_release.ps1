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
