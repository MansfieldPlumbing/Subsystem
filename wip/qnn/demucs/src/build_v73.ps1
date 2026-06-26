# build_v73.ps1 -Onnx <model.onnx> -Length <samples> [-Tag demucs]
# Full demucs->V73 pipeline: onnxsim (static) -> rank6 squeeze -> qairt-converter (DLC)
# -> qnn-context-binary-generator (V73/SM8550 HTP context binary). The proven recipe.
param(
  [Parameter(Mandatory = $true)][string]$Onnx,
  [Parameter(Mandatory = $true)][int]$Length,
  [string]$Tag = 'demucs'
)
$ErrorActionPreference = 'Stop'
$py  = 'S:\qnn\mamba\envs\qnn-tools\python.exe'
$bin = 'S:\qairt\2.42.0.251225\bin\x86_64-windows-msvc'
& 'S:\qairt\2.42.0.251225\bin\envsetup.ps1' | Out-Null
$out = 'S:\qnn\demucs\out'
New-Item -ItemType Directory -Force "$out\bin" | Out-Null
$sim = "$out\${Tag}_sim.onnx"; $final = "$out\${Tag}_final.onnx"; $dlc = "$out\${Tag}_final.dlc"
$graph = "${Tag}_final"

Write-Host "[1/5] onnxsim (static input 1,2,$Length)"
& $py -m onnxsim $Onnx $sim --overwrite-input-shape "input:1,2,$Length"; if ($LASTEXITCODE) { throw 'onnxsim failed' }

Write-Host '[2/5] surgery: squeeze_rank6_leading'
& $py 'S:\qnn\demucs\src\htp_surgery.py' $sim $final --passes squeeze_rank6_leading; if ($LASTEXITCODE) { throw 'surgery failed' }

Write-Host '[3/5] qairt-converter -> DLC'
& $py "$bin\qairt-converter" -i $final --output_path $dlc --source_model_input_shape 'input' "1,2,$Length"; if ($LASTEXITCODE) { throw 'convert failed' }

Write-Host '[4/5] write HTP V73 configs'
$ext = "$out\htp_ext_$Tag.json"; $cfg = "$out\htp_config_$Tag.json"
@"
{
  "graphs":  [ { "graph_names": ["$graph"], "fp16_relaxed_precision": 1, "vtcm_mb": 8, "O": 3 } ],
  "devices": [ { "soc_id": 43, "dsp_arch": "v73", "cores": [ { "core_id": 0, "perf_profile": "burst", "rpc_control_latency": 100 } ] } ]
}
"@ | Set-Content -Encoding ascii $ext
@"
{ "backend_extensions": { "shared_library_path": "QnnHtpNetRunExtensions.dll", "config_file_path": "$($ext.Replace('\','\\'))" } }
"@ | Set-Content -Encoding ascii $cfg

Write-Host '[5/5] qnn-context-binary-generator -> V73 .bin'
& "$bin\qnn-context-binary-generator.exe" --backend QnnHtp.dll --model QnnModelDlc.dll --dlc_path $dlc --config_file $cfg --binary_file "${Tag}_v73" --output_dir "$out\bin"
if ($LASTEXITCODE) { throw 'context-binary-generator failed' }
Write-Host "DONE -> $out\bin\${Tag}_v73.bin"
