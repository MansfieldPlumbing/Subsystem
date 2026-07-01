#requires -Version 7
# test.vom.gpu-pe-factory.ps1 — Receipt for the GPU PE-factory (wip/vom-gpu-compiler): the VOM=DirectPort spine.
# Runs compile.ps1 (self-contained: Add-Type + embedded HLSL + runtime D3DCompile via d3dcompiler_47.dll -- NO MSVC,
# NO home-built DLL), which assembles a native Win64 PE on the GPU with the VOM brokering every D3D12 resource as a
# cascade-reclaimed handle. Asserts a structurally-valid PE32+ is produced. This is the same pure-C# D3D12 spine now
# productionized in src/native/dp-onnx/onnx-interp/GpuD3D12.cs (the GPU inference harness the design predicted).
#   Dogfood:  ss run tests/test.vom.gpu-pe-factory.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dir = @('S:\subsystem\src\tools\VomGpuCompiler','S:\subsystem-project\subsystem-main\src\tools\VomGpuCompiler') |
       Where-Object { Test-Path (Join-Path $_ 'compile.ps1') } | Select-Object -First 1
if (-not $dir) { Write-Host "SKIP - VomGpuCompiler not found on this box." -ForegroundColor Yellow; return }
# compile.ps1 uses the CodeDom Add-Type (/unsafe /platform:x64) — Windows PowerShell 5.1, not pwsh 7.
$ps51 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $ps51)) { Write-Host "SKIP - Windows PowerShell 5.1 not present (Add-Type CodeDom needed)." -ForegroundColor Yellow; return }

$target = Join-Path $dir 'sssd.exe'
Remove-Item $target -Force -EA SilentlyContinue
$out = (& $ps51 -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'compile.ps1') 2>&1 | Out-String)
Assert ($out -match 'Native PE executable compiled via GPU') "GPU PE-factory ran (D3D12 dispatch -> PE bytes, VOM-brokered)"
Assert ($out -match 'Live elements: 0')                       "VOM cascade-reclaimed every D3D12 handle (0 leaked)"
Assert (Test-Path $target)                                    "sssd.exe produced"
if (Test-Path $target) {
    $b = [System.IO.File]::ReadAllBytes($target)
    $peOff = [BitConverter]::ToInt32($b, 0x3C)
    Assert ($b[0] -eq 0x4D -and $b[1] -eq 0x5A)               "PE has MZ signature"
    Assert ($peOff -gt 0 -and $peOff -lt $b.Length-24 -and $b[$peOff] -eq 0x50 -and $b[$peOff+1] -eq 0x45) "PE has PE\0\0 signature"
    Assert ([BitConverter]::ToUInt16($b, $peOff+24) -eq 0x20B) "PE32+ (64-bit) optional-header magic"
}
$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - the GPU PE-factory (pure-C# D3D12, VOM-brokered) assembles a valid PE32+ on the GPU."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.gpu-pe-factory'; pass=$pass; verdict=$(if($pass){'GPU-assembled PE32+ green - VOM=DirectPort spine, productionized in dp-onnx GpuD3D12'}else{'see failures'}) }
