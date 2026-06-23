#requires -Version 7
# test.staging.ai-transport-assets.ps1 — Receipt that the staged DirectPort/VOM/AI-transport assets
# are present and the safely-runnable ones actually run. Authority = the binary; this comment is not.
# NOT device-bound (the RIFE-on-NPU execution lives in QNN-LOG.md + the OnePlus, out of scope here);
# this verifies the host-side, cold-runnable proofs. Dot-source-safe; no mutation.
#   Dogfood:  ss run tests/test.staging.ai-transport-assets.ps1
#
#   A  STAGED    the central staging tree + its subtrees + the RIFE proof are present. Asserted.
#   B  SURGEON   onnx-surgeon.exe (native, python-free) runs and prints its verbs. Asserted.
#   C  ONNXOBJ   Onnx.dll loads as live .NET objects (ModelProto/NodeProto present). Asserted.
#   D  QNNBIN    rife_v73.bin reads as a valid QNN HTP context binary via the host utility. Asserted.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$stage = 'S:\subsystem-project\staging'

# A — staging present
$dirs = 'directport','virtuacam','onnx-surgeon','onnxnet','qnn'
$present = @($dirs | Where-Object { Test-Path (Join-Path $stage $_) }).Count
$rife = Join-Path $stage 'qnn\rife_v73.bin'
Write-Host "A  staged: $present/$($dirs.Count) subtrees; rife_v73.bin $([bool](Test-Path $rife))"
Assert ($present -eq $dirs.Count) "all $($dirs.Count) staging subtrees present"
Assert (Test-Path $rife) "rife_v73.bin staged (the QNN proof)"

# B — native ONNX surgeon runs
$surgeon = 'S:\onnxsurgeon-project\bin\onnx-surgeon.exe'
$bOk = $false
if (Test-Path $surgeon) { $o = & $surgeon 2>&1 | Out-String; $bOk = $o -match 'onnx-surgeon' -and $o -match 'inspect' }
Write-Host "B  onnx-surgeon: runs=$bOk"
Assert $bOk "onnx-surgeon.exe runs and lists its verbs"

# C — Onnx.dll loads as objects
$dll = 'S:\qnn-project\workspace\onnxnet\bin\Release\netstandard2.0\Onnx.dll'
$cOk = $false; $types = 0
if (Test-Path $dll) { $asm=[Reflection.Assembly]::LoadFrom($dll); $types=$asm.GetTypes().Count; $cOk = [bool]$asm.GetType('Onnx.ModelProto') -and [bool]$asm.GetType('Onnx.NodeProto') }
Write-Host "C  Onnx.dll: loaded types=$types ModelProto+NodeProto=$cOk"
Assert $cOk "Onnx.dll exposes ModelProto + NodeProto (ONNX as live objects)"

# D — rife_v73.bin reads as a QNN HTP context binary
$util = 'S:\qnn-project\sdk\qairt-2.42\bin\x86_64-windows-msvc\qnn-context-binary-utility.exe'
$dOk = $false
if ((Test-Path $util) -and (Test-Path $rife)) {
    $out = "S:\tmp\rife_info_test.json"
    & $util --context_binary $rife --json_file $out 2>&1 | Out-Null
    if (Test-Path $out) { $dOk = (Get-Content $out -Raw) -match 'HTP_CONTEXT_INFO_BLOB_VERSION' }
}
Write-Host "D  rife_v73.bin: reads as QNN HTP context = $dOk"
Assert $dOk "rife_v73.bin parses as a valid QNN HTP context binary"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — the staged AI-transport assets are present and the host-runnable proofs run (surgeon, Onnx objects, RIFE .bin)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.staging.ai-transport-assets'; pass=$pass
    stagedSubtrees=$present; surgeonRuns=$bOk; onnxObjects=$cOk; rifeBinReads=$dOk
    verdict=$(if($pass){'staged + host proofs live; device (RIFE-on-NPU) per QNN-LOG'}else{'see failures'})
}
