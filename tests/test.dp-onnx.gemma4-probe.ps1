#requires -Version 7
# test.dp-onnx.gemma4-probe.ps1 — Receipt for reading the Gemma-4 E2B model on dp-onnx, sovereignly.
# The live model is gemma-4-E2B-it.litertlm (a LITERTLM container of .tflite FlatBuffers). dp-onnx reads it
# with the home-rolled FlatBuffers reader (LiteRt.cs) — no FlatBuffers lib, no LiteRT lib — translating to the
# same Onnx.ModelProto IR so `probe` enumerates the op set and maps it onto the dp-onnx kernels. The decomposed
# ONNX export is the alternate projection of the same gemma4 truth (re-export via wip/gemma4/ when its gated
# weights exist). SKIPS clean without the ~2.4 GB model.
#   Dogfood:  ss run tests/test.dp-onnx.gemma4-probe.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# Locate the engine the same way the other dp-onnx receipts do: the build/ sibling, freshest dp-onnx.exe.
$buildRoot = if ($env:SS_BUILD) { $env:SS_BUILD } else { Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) 'build' }
$exe = Get-ChildItem -Path $buildRoot -Recurse -Filter dp-onnx.exe -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { Write-Host "SKIP - dp-onnx.exe not built (build src\native\dp-onnx\onnx-interp\Onnx.Interp.csproj)." -ForegroundColor Yellow; return }

$model = @($env:SS_GEMMA_LITERTLM,'S:\subsystem-pull\models\gemma-4-E2B-it.litertlm','S:\modeldb\gemma-4-E2B-it.litertlm') |
         Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $model) { Write-Host "SKIP - gemma-4 E2B .litertlm not present (set SS_GEMMA_LITERTLM)." -ForegroundColor Yellow; return }
Write-Host "engine: $($exe.FullName)"
Write-Host "model:  $model"

$pr = (& $exe.FullName probe $model 2>&1 | Out-String)
$sections = if ($pr -match 'container:\s*(\d+)\s*sections') { [int]$Matches[1] } else { 0 }
$hasSP    = [bool]($pr -match 'SP_Tokenizer')
# the main LLM graph = the TFLiteModel section with the most ops; read its kernel-coverage ratio
$cov = [regex]::Matches($pr, 'ops=(\d+) distinct=\d+\s+mapped-types=(\d+)/(\d+)') |
       ForEach-Object { [pscustomobject]@{ ops=[int]$_.Groups[1].Value; mapped=[int]$_.Groups[2].Value; distinct=[int]$_.Groups[3].Value } } |
       Sort-Object ops -Descending | Select-Object -First 1

Assert ($sections -ge 10)             "LITERTLM container parsed: $sections sections off the home-rolled FlatBuffers reader (no lib)"
Assert $hasSP                         "SP_Tokenizer section present (the sovereign tokenizer path)"
Assert ($cov -and $cov.ops -gt 10000) "main LLM graph parsed: $(if($cov){$cov.ops}else{0}) ops across $(if($cov){$cov.distinct}else{0}) op-types"
Assert ($cov -and $cov.mapped -ge 20) "op set maps onto dp-onnx kernels: $(if($cov){"$($cov.mapped)/$($cov.distinct)"}else{'0/0'})"
# Slice 1 (the op gap): the quant/norm ops the transformer graph needs now map to real dp-onnx kernels.
$nowMapped = @('DEQUANTIZE','QUANTIZE','RSQRT','NOT_EQUAL') | Where-Object { $pr -notmatch ($_ + '\s.*MISSING') }
Assert ($nowMapped.Count -eq 4) "DEQUANTIZE/QUANTIZE/RSQRT/NOT_EQUAL map to dp-onnx kernels (mapped: $($nowMapped -join ','))"
Write-Host "  gemma-4 E2B: $sections sections; main graph ops=$(if($cov){$cov.ops}) coverage=$(if($cov){"$($cov.mapped)/$($cov.distinct)"})"

# Slice 2 (probe -> executable): the sovereign tflite->Onnx.ModelProto translator. The embedder section [2]
# (token-mask + 2-bit per-row-quantized EMBEDDING_LOOKUP -> scale -> reshape) is translated and RUN through Interp.
$rr = (& $exe.FullName run $model --section 2 2>&1 | Out-String)
$ran     = if ($rr -match '\[2\]\s+RAN\s+nodes=(\d+)')        { [int]$Matches[1] } else { 0 }
$rOuts   = if ($rr -match '\[2\]\s+RAN\s+nodes=\d+\s+inputs=\d+\s+outputs=(\d+)') { [int]$Matches[1] } else { 0 }
$rFinite = [bool]($rr -match '\[2\]\s+RAN\s+nodes=\d+.*finite=True')
$rEmbed  = [bool]($rr -match 'EMBEDDING_LOOKUP\s+-> Gather')
Assert ($ran -ge 8)  "section [2] translated tflite->ModelProto and RAN end-to-end through Interp: $ran nodes dispatched"
Assert ($rEmbed)     "EMBEDDING_LOOKUP lowered to Gather (2-bit per-row table dequantized to a float initializer)"
Assert ($rOuts -ge 1) "section [2] produced $rOuts output tensor(s) off the sovereign translation"
Assert ($rFinite)    "section [2] output is finite (no NaN/Inf) — the translated embedder graph is numerically live"
Write-Host "  gemma-4 E2B slice 2: section [2] embedder ran $ran nodes -> $rOuts finite output(s) on dp-onnx"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - dp-onnx reads the gemma-4 E2B .litertlm sovereignly (no FlatBuffers lib, no LiteRT lib); op coverage measured."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.dp-onnx.gemma4-probe'; pass=$pass; sections=$sections; spTokenizer=$hasSP; coverage=$(if($cov){"$($cov.mapped)/$($cov.distinct)"}); ops=$(if($cov){$cov.ops}); verdict=$(if($pass){'gemma-4 E2B read off the sovereign litertlm/tflite reader; coverage measured'}else{'see failures'}) }
