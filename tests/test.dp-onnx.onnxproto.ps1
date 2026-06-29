#requires -Version 7
# test.dp-onnx.onnxproto.ps1 — Receipt for the home-rolled ONNX protobuf reader/writer (OnnxProto.cs).
# OnnxProto replaces Google.Protobuf + Grpc.Tools so dp-onnx folds into the in-proc Roslyn build with no
# NuGet/codegen (CRQ143). Asserts: (1) the write->read->run roundtrip is bit-exact (selftest, max|diff|=0),
# and (2) a real .onnx parses end-to-end (full node graph + initializers + attributes), if one is present.
#   Dogfood:  ss run tests/test.dp-onnx.onnxproto.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# Locate the engine: the build sibling ($SS_BUILD or ..\build), else anywhere under the drive's build/.
$buildRoot = if ($env:SS_BUILD) { $env:SS_BUILD } else { Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) 'build' }
$exe = Get-ChildItem -Path $buildRoot -Recurse -Filter dp-onnx.exe -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { Write-Host "SKIP - dp-onnx.exe not built (build src\native\dp-onnx\onnx-interp\Onnx.Interp.csproj)." -ForegroundColor Yellow; return }
Write-Host "engine: $($exe.FullName)"

# (1) roundtrip: graph build -> OnnxProto.ToByteArray -> OnnxProto.Parser.ParseFrom -> interp run.
$st = (& $exe.FullName selftest 2>&1 | Out-String)
$diff = if ($st -match 'max\|diff\|=([0-9.eE+\-]+)') { [double]$Matches[1] } else { [double]::NaN }
Assert ($st -match 'PASS')        "selftest verdict PASS"
Assert ($diff -eq 0.0)            "write->read->run roundtrip bit-exact (max|diff|=$diff)"

# (2) real-model parse: walk the full graph + read initializers/attributes through OnnxProto.
$model = @('S:\Demucs_v4_TRT\demucsv4.onnx') | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($model) {
    $pr = (& $exe.FullName probe $model 2>&1 | Out-String)
    $nodes = if ($pr -match 'nodes=(\d+)') { [int]$Matches[1] } else { 0 }
    $ran   = if ($pr -match 'ran (\d+)/') { [int]$Matches[1] } else { 0 }
    Assert ($nodes -gt 1000)      "real model parsed: $nodes nodes off OnnxProto (no protobuf)"
    Assert ($ran -gt 0)           "interp ran $ran real nodes (initializers + attributes decoded)"
    Write-Host "  real model: $(Split-Path $model -Leaf)  nodes=$nodes  ran=$ran"
} else {
    Write-Host "  (no real .onnx on this box; roundtrip-only receipt)" -ForegroundColor DarkGray
}

if ($fails.Count -eq 0) { Write-Host "PASS  test.dp-onnx.onnxproto  (Google.Protobuf + Grpc.Tools dropped; reader/writer verified)" -ForegroundColor Green }
else { Write-Host "FAIL  test.dp-onnx.onnxproto  ($($fails.Count) failed)" -ForegroundColor Red; exit 1 }
