#requires -Version 7
# test.gpu.dpgpu-gemm-csharp.ps1 — Receipt for dpx's PURE-C# D3D12 GPU seam (src/native/dpx/onnx-interp/GpuD3D12.cs).
# The engine's `Gpu.dpgpu_gemm` no longer P/Invokes a native dpgpu.dll — it dispatches D3D12 compute from managed
# code (COM vtable interop + serialized root sig), reproducing dpgpu.cpp's GEMM. This builds dpx with dotnet,
# compiles gemm.hlsl -> DXIL with dxc, runs `dpx gpu-test <dxil>`, and asserts MATCH vs the CPU MatMul AND that
# no dpgpu.dll is in the build output. SKIPS clean on a box without dxc/dotnet (the proof is the receipt, not this wrapper).
#   Dogfood:  ss run tests/test.gpu.dpgpu-gemm-csharp.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# resolve the dpx project across boxes (laptop S:\subsystem, workstation S:\subsystem-project\subsystem-main, or share)
$dp = @('S:\subsystem','S:\subsystem-project\subsystem-main','\\P520\s\subsystem-project\subsystem-main') |
      ForEach-Object { Join-Path $_ 'src\native\dpx' } |
      Where-Object { Test-Path (Join-Path $_ 'onnx-interp\Onnx.Interp.csproj') } | Select-Object -First 1
if (-not $dp) { Write-Host "SKIP - dpx project not found on this box." -ForegroundColor Yellow; return }

$dxc = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\dxc.exe' -EA SilentlyContinue |
       Sort-Object FullName -Descending | Select-Object -First 1
$dotnet = @('S:\bin\dotnet\dotnet.exe', (Get-Command dotnet -EA SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $dxc -or -not $dotnet) { Write-Host "SKIP - need dxc (Windows Kits) + dotnet to exercise the GPU receipt." -ForegroundColor Yellow; return }

# 1. compile the kernel to DXIL (sovereign: no MSVC, just the shader compiler)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("gpu-gemm-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$dxil = Join-Path $tmp 'gemm.dxil'
& $dxc.FullName -T cs_6_2 -E main (Join-Path $dp 'gpu\gemm.hlsl') -Fo $dxil 2>&1 | Out-Null
Assert (Test-Path $dxil) "gemm.hlsl compiled to DXIL with dxc"

# 2. build dpx (managed; NO native dpgpu.dll)
$csproj = Join-Path $dp 'onnx-interp\Onnx.Interp.csproj'
$build = & $dotnet build $csproj -c Release --nologo 2>&1 | Out-String
Assert ($build -match 'Build succeeded' -or $build -notmatch 'error ') "dpx builds with the pure-C# GPU seam"
$exe = Get-ChildItem 'S:\build\Onnx.Interp\bin\Release\net*\dpx.exe','S:\subsystem\src\native\dpx\onnx-interp\bin\Release\net*\dpx.exe' -EA SilentlyContinue | Select-Object -First 1
if (-not $exe) { Write-Host "SKIP - built dpx.exe not found (output redirect)." -ForegroundColor Yellow; return }
Assert (-not (Test-Path (Join-Path $exe.DirectoryName 'dpgpu.dll'))) "no dpgpu.dll in the build output (native dependency dropped)"

# 3. dispatch a MatMul through the managed seam and diff vs the CPU kernel
$out = (& $exe.FullName gpu-test $dxil 2>&1 | Out-String).Trim()
Write-Host $out
Assert ($out -match 'MATCH') "D3D12 backend dispatched a MatMul via pure-C# and matched the CPU kernel"

# Vulkan backend (the cross-platform spine -> Adreno/Mali): same engine, DPGPU_BACKEND=vulkan, uses the shipped gemm.spv
Assert (Test-Path (Join-Path $exe.DirectoryName 'gemm.spv')) "gemm.spv shipped to the build output (Vulkan kernel)"
$env:DPGPU_BACKEND = 'vulkan'
$outVk = (& $exe.FullName gpu-test $dxil 2>&1 | Out-String).Trim()
$env:DPGPU_BACKEND = $null
Write-Host $outVk
Assert ($outVk -match 'MATCH') "Vulkan backend (vulkan-1.dll + gemm.spv) dispatched the same GEMM and matched the CPU kernel"

$pass = $fails.Count -eq 0
Remove-Item $tmp -Recurse -Force -EA SilentlyContinue
Write-Host ""
Write-Host ($(if($pass){"PASS - dpx's GPU seam is pure-C# (D3D12 + Vulkan, no dpgpu.dll / no MSVC) and both match the CPU GEMM."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.gpu.dpgpu-gemm-csharp'; pass=$pass; verdict=$(if($pass){'managed D3D12 + Vulkan GEMM dispatch green - dpgpu.dll dropped, cross-platform spine ready'}else{'see failures'}) }
