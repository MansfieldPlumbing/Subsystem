# build.ps1 — build the vendored dp-onnx engine: the home-rolled, ORT-free .NET ONNX interpreter
# that `ss tts` (and the gemma rung, CRQ135) drives. S:-rooted (uses the on-drive dotnet; the naive
# PATH has none on purpose — this is a vendored external engine with its own toolchain build, exactly
# like native/directport/build.ps1). NO onnxruntime, NO python.
#   onnx-interp\Onnx.Interp.csproj — Program.cs over Onnx.dll -> dp-onnx.exe. It ProjectReferences
#   onnxnet\Onnx.csproj, where Grpc.Tools' protoc compiles onnx.proto -> Onnx.dll (ONNX-as-protobuf
#   objects), so ONE build pulls both. Output lands in the repo's build/ sibling (Directory.Build.props).
# Usage:  pwsh -File build.ps1 [-Configuration Release]
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Resolve dotnet: env override -> the on-drive S:\bin layout -> a literal. (PATH is intentionally naive.)
$bin    = if ($env:SS_BIN) { $env:SS_BIN } else { 'S:\bin' }
$dotnet = if ($env:SS_DOTNET) { $env:SS_DOTNET } else { Join-Path $bin 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet not found at $dotnet (set `$env:SS_DOTNET or `$env:SS_BIN)" }

$interp = Join-Path $here 'onnx-interp\Onnx.Interp.csproj'

Write-Host "dotnet: $dotnet"
Write-Host "building dp-onnx.exe (+ onnxnet -> Onnx.dll, transitively) ..."
& $dotnet build $interp -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dp-onnx build failed with exit $LASTEXITCODE" }

# Output is redirected out of the source tree (Directory.Build.props -> build/ sibling); discover the exe.
$exe = Get-ChildItem (Join-Path $here '..\..\..\..\build') -Recurse -Filter 'dp-onnx.exe' -ErrorAction SilentlyContinue |
       Where-Object FullName -match "\\$Configuration\\" | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { $exe = Join-Path $here "onnx-interp\bin\$Configuration\net11.0\dp-onnx.exe" }  # un-redirected fallback
if (Test-Path $exe) {
    Write-Host "`nbuilt: $exe ($([math]::Round((Get-Item $exe).Length/1KB,1)) KB)"
    Write-Host "selftest ..."
    & $exe selftest
} else { Write-Host "`nbuilt, but dp-onnx.exe not located (check the build/ sibling for the current TFM/RID)" }
