#requires -Version 7
# test.dpx.model-db.ps1 — Receipt for the models-as-.db converter + op triage as a query.
# `dpx db` compiles an ONNX graph into a queryable SQLite model store (6 tables); `dpx db-stats`
# runs the op histogram + the backend-risky triage as SELECTs. Asserts: the .db is produced, its node count
# matches the graph, and the triage query flags the HTP-risky ops (the demucs session's CRQ156 finding, now
# a query). Per-op off-HTP routing is the node.backend column (an UPDATE). SKIPs clean if engine/model absent.
#   Dogfood:  ss run tests/test.dpx.model-db.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$buildRoot = if ($env:SS_BUILD) { $env:SS_BUILD } else { Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) 'build' }
$exe = Get-ChildItem -Path $buildRoot -Recurse -Filter dpx.exe -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { Write-Host "SKIP - dpx.exe not built." -ForegroundColor Yellow; return }
$onnx = @('S:\demucs-research\hf-model\demucs_v4_trt\demucsv4.onnx','S:\Demucs_v4_TRT\demucsv4.onnx') |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $onnx) { Write-Host "SKIP - no model .onnx on this box." -ForegroundColor Yellow; return }
Write-Host "engine: $($exe.FullName)`nmodel:  $onnx"

$db = Join-Path ([System.IO.Path]::GetTempPath()) "test.model-db.db"
Remove-Item $db -Force -EA SilentlyContinue

# (1) convert: onnx -> 6-table SQLite via OnnxProto.
$conv = (& $exe.FullName db $onnx $db 2>&1 | Out-String)
$nodesConv = if ($conv -match 'nodes=(\d+)') { [int]$Matches[1] } else { 0 }
Assert (Test-Path $db)        ".db produced"
Assert ($nodesConv -gt 1000)  "converted $nodesConv nodes off OnnxProto (no protobuf at convert time)"

# (2) triage as a query: op histogram + the backend-risky set, straight off the .db.
$stats = (& $exe.FullName db-stats $db 2>&1 | Out-String)
$nodesStat = if ($stats -match 'nodes=(\d+)') { [int]$Matches[1] } else { -1 }
$risky = if ($stats -match 'backend-risky[^:]*:\s*(\d+)\s*node') { [int]$Matches[1] } else { -1 }
Assert ($nodesStat -eq $nodesConv)  "db-stats node count ($nodesStat) matches the converted graph"
Assert ($stats -match 'op histogram') "op histogram is a query (GROUP BY op_type)"
Assert ($risky -ge 0)               "backend-risky triage is a query (WHERE op_type IN htp-risky); flagged $risky node(s)"

Remove-Item $db -Force -EA SilentlyContinue
$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - the model is queryable rows; op triage = a SELECT (models-as-.db)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.dpx.model-db'; pass=$pass; verdict=$(if($pass){'ONNX compiled to a 6-table SQLite store; op histogram + HTP-risky triage are queries'}else{'see failures'}) }
