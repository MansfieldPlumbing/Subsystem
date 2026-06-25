# build.ps1 — build the vendored dp-onnx engine: the home-rolled, ORT-free .NET ONNX interpreter
# that `ss tts` (and the gemma rung, CRQ135) drives. Two steps, S:-rooted (uses the on-drive dotnet;
# the naive PATH has none on purpose — this is a vendored external engine with its own toolchain build,
# exactly like native/directport/build.ps1). NO onnxruntime, NO python.
#   1. onnxnet\Onnx.csproj   — protoc (Grpc.Tools) compiles onnx.proto -> Onnx.dll (ONNX-as-protobuf objects)
#   2. onnx-interp\Onnx.Interp.csproj — Program.cs over Onnx.dll -> dp-onnx.exe (the interpreter)
# Usage:  pwsh -File build.ps1 [-Configuration Release]
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Resolve dotnet: env override -> the on-drive S:\bin layout -> a literal. (PATH is intentionally naive.)
$bin    = if ($env:SS_BIN) { $env:SS_BIN } else { 'S:\bin' }
$dotnet = if ($env:SS_DOTNET) { $env:SS_DOTNET } else { Join-Path $bin 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet not found at $dotnet (set `$env:SS_DOTNET or `$env:SS_BIN)" }

$onnxnet = Join-Path $here 'onnxnet\Onnx.csproj'
$interp  = Join-Path $here 'onnx-interp\Onnx.Interp.csproj'

Write-Host "dotnet: $dotnet"
Write-Host "1/2  building Onnx.dll (onnx.proto -> protobuf objects) ..."
& $dotnet build $onnxnet -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "onnxnet build failed with exit $LASTEXITCODE" }

Write-Host "2/2  building dp-onnx.exe (the ORT-free interpreter) ..."
& $dotnet build $interp -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "onnx-interp build failed with exit $LASTEXITCODE" }

$exe = Join-Path $here "onnx-interp\bin\$Configuration\net11.0\dp-onnx.exe"
if (Test-Path $exe) { Write-Host "`nbuilt: $exe ($([math]::Round((Get-Item $exe).Length/1KB,1)) KB)" }
else { Write-Host "`nbuilt onnx-interp (exe path may differ by TFM/RID; check bin\$Configuration)" }
Write-Host "selftest:  & '$exe' selftest"
