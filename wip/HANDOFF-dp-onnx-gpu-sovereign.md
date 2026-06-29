# HANDOFF — dp-onnx GPU made sovereign (pure-C#, no dpgpu.dll/C++/MSVC) + wip closeouts

Resume-here for the dp-onnx GPU sovereignty work. Authority = the code + the `tests/` receipts, not this doc.
Work from **`S:\subsystem`** (local, writable, `main`). The `\\P520\s` share is **read-only** for the laptop session.

## DONE — landed + verified in `S:\subsystem` (main), build green, both backends `gpu-test` MATCH
The whole GPU path is now **managed C# + shader bytecode**. `dpgpu.dll` is gone from the build output.
- **`src/native/dp-onnx/onnx-interp/GpuD3D12.cs`** — pure-C# D3D12 GEMM dispatch (hand-rolled COM vtable interop
  + serialized 4-param root sig: RootConstants{M,N,K}@b0, SRV t0=A, SRV t1=B, UAV u0=C; dispatch (N+15)/16 x (M+15)/16).
  Persistent device/queue/root-sig/PSO/fence; per-call buffers released. `MakeDevice` = beefiest-by-VRAM (V340>P2000)
  + `DPGPU_ADAPTER` override. Reproduces dpgpu.cpp's `dpgpu_gemm` bit-for-bit.
- **`src/native/dp-onnx/onnx-interp/GpuVulkan.cs`** + **`gemm.spv`** — pure-C# Vulkan GEMM (vulkan-1.dll flat-C
  P/Invoke + precompiled SPIR-V; 3 storage buffers + push{M,N,K}; per-call resources destroyed). The cross-platform
  spine: same code + `gemm.spv` run on Android (Adreno/Mali) via libvulkan.so.
- **`Program.cs` `static class Gpu`** — backend seam: `DPGPU_BACKEND=vulkan` flips to GpuVulkan (loads gemm.spv from
  AppContext.BaseDirectory), default = GpuD3D12 (reuses the caller's gemm DXIL). Same `dpgpu_gemm`/`DeviceName`
  signatures -> zero call-site changes.
- **`Onnx.Interp.csproj`** — added `<Compile>` GpuD3D12.cs + GpuVulkan.cs and `<Content>` gemm.spv (CopyToOutputDirectory).
- Build: `& S:\bin\dotnet\dotnet.exe build src\native\dp-onnx\onnx-interp\Onnx.Interp.csproj -c Release` (NOT `ss build`).
  Verify: `dp-onnx gpu-test <gemm.dxil>` => MATCH (D3D12); `$env:DPGPU_BACKEND='vulkan'; dp-onnx gpu-test <dxil>` => MATCH (Vulkan).

## wip closeouts (this session)
- **`wip/vom-gpu-compiler/`** (the vom-project) — **CLOSED.** It IS the pure-C# D3D12 PE-factory spine; `compile.ps1`
  assembles a valid PE32+ on the GPU (VOM-brokered, 0 leaks), now productionized as `GpuD3D12.cs`. Receipt:
  **`tests/test.vom.gpu-pe-factory.ps1`** (green).
- **`wip/gemma4/`** — **CLOSED as far as possible.** Gemma-4 E2B runs on the now-dll-free dp-onnx (managed engine +
  pure-C# GPU; no dpgpu.dll). Export is one-time Python (no python at runtime). Receipt:
  **`tests/test.dp-onnx.gemma4-probe.ps1`** (SKIPs clean — only blocker is the gated ~10 GB export at `W:\gemma4\onnx\e2b_step.onnx`;
  re-export via `wip/gemma4/export_gemma4_e2b_decomposed.py`).

## wip remaining
- **`wip/d3d12-kernels/`** — 56 HLSL inference kernels (mul_mat_split_k_reduce, flash_attn_split_k_reduce, dequant_q*,
  geglu/swiglu/gelu, group_norm, im2col_3d, norm, silu, ...). NOT integrated. **The harness is done** (GpuD3D12 already
  dispatches an arbitrary DXIL). NEXT: generalize GpuD3D12 from `Gemm(...)` to a generic
  `Dispatch(dxil, rootParams, buffers, gridXYZ)` + compile each kernel to DXIL + route dp-onnx ops to the right kernel.
  This is the full GPU-inference off-ramp (CRQ117). Big, but the spine is built -> "blown past" the hard part.
- **`wip/agent-browser/`** — WebView2 browse agent (`ss browse`/`Rd.Consult`). NOT integrated; different domain (not GPU/dp-onnx).
  Mount as MCP-child driving `Invoke-RdConsult` (`src/runspace/Pwsh/Cmdlets/RdCmdlets.cs`). CRQ120.
- **`wip/directport/`, `wip/dp-onnx-research/`, `wip/qnn/`** — not touched this session.

## QNN track (open)
The QNN Kokoro context-binary build FAILED on a VTCM overflow (a decoder `noise_res.1/Pow` wants ~103 MB VTCM vs 8 MB on
v73). dp-onnx's per-op swappable backend sidesteps this (route that op off-HTP). A "qnn format unlocker" recon workflow was
launched (wf78lff81) but never reported. Devices: razr+ 2024 = Snapdragon SM8635 (Adreno 735 + Hexagon v73, the full-stack
target); moto edge 2022 = MediaTek (Mali-G610, Vulkan only, no Hexagon); 1 phone unauthorized.

## Coordination
- The `cr/agent` branch (share, the concurrent gemma node-coverage work) still carries the OLD `dpgpu.dll` seam. On merge to
  `main`, take main's pure-C# seam + add GpuD3D12.cs, GpuVulkan.cs, gemm.spv. (A cr/agent-shaped version of the swap is staged
  at `S:\dp-onnx_qnn\Program.cs.live` + `dp-onnx-gpu-csharp.patch` for a conflict-free merge.)
- Concurrent session this session = virtuacam + adb cmdlets (orthogonal); it held `ss build` while I `dotnet build`'d dp-onnx.

## Proof artifacts (S:\dp-onnx_qnn\, parked)
`gpu-selftest.ps1` (zero-install D3D12, runtime D3DCompile), `vk-probe.ps1` + `vk-selftest.ps1` (pure-C# Vulkan),
`gemm-selftest.ps1`, `gemm-verify/` + `vk-gemm-verify/` (net11/CoreCLR verification of the exact backend classes),
`INTEGRATION-dp-onnx-gpu-csharp.md`. Toolchain notes: pwsh 5.1 + d3d12.dll + d3dcompiler_47.dll + vulkan-1.dll are all
stock on Win11; SPIR-V via the LunarG SDK at `S:\bin\VulkanSDK\1.4.350.0`; DXIL via Windows Kits dxc.

## Recommended next session
1. **d3d12-kernels off-ramp** — generalize GpuD3D12 dispatch + wire the 56 kernels (GPU inference). The biggest lever.
2. **Android on-device** — `adb push` the Vulkan path to the razr+ Adreno; prove a GEMM on real mobile silicon.
3. **QNN unlocker** — resume the format/VTCM work; build `dpqnn` to the same seam.
