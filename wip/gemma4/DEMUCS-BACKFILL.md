# Backfilling the gemma4/kokoro gains into your Demucs ONNX → high-perf on the sovereign stack

**Question (Scott):** how to backfill the gains we make into the demucs ONNX model to make it high-perf.

**The one-line answer:** stop treating demucs as an NVIDIA-TRT island and make it a first-class **dp-onnx
GraphRuntime citizen** — all-convolution, GPU-Conv-fused on **D3D12/Radeon**, fat-ops federated over
**DirectPort**, **W8A16**-quantized. Every rung you build for gemma4/kokoro is a rung demucs climbs for free,
because demucs is the *most* conv-dominated of the three (63%+) — it's the ideal backfill target, not an
afterthought. AUTHORITY: `research/QNN-LOG.md`, `research/Model Conversion and Optimization Roadmap.md`,
`research/bench.rb.graphruntime-kokoro-parity.result.md`, the demucs case study. Re-ground before building.

## Where demucs is today (the honest baseline)
- **Fast but captive:** ~5 s/song, but only on **NVIDIA + CUDA + TensorRT** (RTX 3090 sm86). Per-GPU `.trt`
  engines, NVIDIA-only. Off the sovereign AMD/Hexagon path entirely.
- **Won't fit Hexagon NPU:** the per-op VTCM wall — a single time-branch dconv needs **42–84 MB** resident vs
  **8 MB VTCM**, and demucs is architecture-locked at 7.8 s (343980 samples, `valid_length` snaps up), so you
  can't shrink the tensors by chunking. Stock qairt can't tile a single op (`research/QNN-LOG.md`).
- The canonical asset is already right: a **single-graph ONNX with STFT internalized** (the
  `WaveformOnlyWrapper` — `demucsv4.onnx`, opset-17, the STFT already decomposed to Cos/Sin/MatMul).

## The five backfills (each is a gemma4/kokoro rung demucs inherits)

### 1. dp-onnx + GPU-Conv on D3D12 — THE lever (biggest single win)
The kokoro parity bench proved it: **63% of time is Conv+ConvTranspose; the lever is the dpgpu D3D12 Conv mount,
not GPU MatMul.** Demucs is *even more* convolutional (53 fat convs, the whole tencoder/tdecoder). So the moment
the D3D12 Conv mount exists for kokoro/gemma's vision encoder, **run the same `demucsv4.onnx` on dp-onnx and its
convs land on the Radeon** — high-perf source separation on AMD, **no CUDA, no TRT, no NVIDIA.** That is the
sovereignty win: demucs stops needing the 3090. *Backfill cost ≈ 0* — it's the same interpreter + the same mount.

### 2. DirectPort "Mario-hybrid" tiling for the fat ops → unlocks Hexagon (and big AMD tensors)
The per-op VTCM wall and kokoro's `m_source` wall have the **same fix**, already designed in `research/QNN-LOG.md`
route (b) + the Mario-hybrid: **tile the fat op at the runtime level, not the graph level** (HTP re-fuses a
graph-level `Pad/Slice/Conv/Concat` back into the busting conv — proven; the runtime can't be re-fused).
- The tile is **born at the tencoder input** (`[1,2,343980]`, 2.6 MB), propagates through the time branch with
  exact halos (k8/s4 entry + k3 d1/d2 dconv), **merges at the transformer bottleneck** (tiny), and the **U-Net
  skips are held in the C# runner as VOM handles** crossing graph boundaries. This is *identical* to gemma4's
  MatFormer activation routing and PLE per-layer streaming (`SYNTHESIS.md` §3) — one tiling primitive, three
  models. The `Add-TileBranch` object-surgery in `Qnn.psm1` (`research/QNN-LOG.md`) is the seed.
- Delegate each tile's conv to the GPU via **DirectPort** (`BufferToTexture → ShaderFilter → TextureToBuffer +
  fence`) — the exact out-of-core op-delegate scaffolding. Fence the gather. → demucs finally **finalizes and
  runs on the V73 NPU**, the wall it's been stuck behind.

### 3. W8A16 "sub-pixel" quant — ~2× throughput, half the footprint
Read straight off the SD-UNet `.bin` (`research/QNN-LOG.md`): int8 W / A16 acts / int32 accumulate / per-channel
scale. RIFE proved fp16→W8A16 roughly halves NPU time. **Selective-precision is mandatory for demucs** (the
Roadmap's explicit warning): **keep the STFT/ISTFT Fourier-basis layers in FP16** — quantizing the deterministic
Fourier kernels injects phase-alignment error → metallic artifacts. Quantize the separation backbone (the fat
conv U-Net + transformer + LSTM) to int8; exclude `stft_surrogate`/`istft_surrogate` via the qspec map. Same
W8A16 recipe you apply to gemma4's FFN/attention while sparing its p-RoPE.

### 4. STFT → Conv1d/ConvTranspose1d frozen-kernel surrogate — makes it 100% convolution
The Roadmap's mathematical surgery: replace STFT with a `Conv1d` whose frozen weights are the precomputed
Fourier basis (cos/sin), ISTFT with a `ConvTranspose1d` (overlap-add = transposed conv by construction). Your
opset-17 export already half-did this (STFT → Cos/Sin/MatMul). Finishing it to real conv ops:
- makes demucs **fully all-convolution** → maximally fed by backfill #1 (GPU-Conv) and portable to **every**
  backend (dp-onnx / DirectML / HTP) with **no native-STFT dependence** — the thing that blocks LiteRT and
  constrains HTP today.
- it's the *inverse* of the kokoro atan2-phase problem: demucs wants the Fourier basis frozen and exact; kokoro
  is debugging where that same basis goes ill-conditioned. Shared STFT surface (`research/SESSION-COMMS.md`).

### 5. The all-.NET runtime seam you already own
Your `demucs_v4_trt.cpp` C-ABI bridge (`Trt_Init/Process/Destroy`, push→enqueue→copy+fence) is the template;
the dp-onnx path drops the vendor runtime entirely — demucs runs in the **same .NET interpreter as kokoro**,
mounted as `ss tts`-style cmdlet over the GraphRuntime substrate (CRQ109/CRQ121 shape), fed by DirectPort. One
runner, every model.

## Order of operations (cheapest win first)
1. **Run `demucsv4.onnx` on dp-onnx as-is** → baseline number on CPU, confirm op-coverage (kokoro's 49 ops
   likely already cover demucs; fill gaps). *Verified-done = a `bench.rb.graphruntime-demucs-parity` receipt.*
2. **Point the D3D12 Conv mount at it** (backfill #1) → the big AMD perf jump, sovereign. **This alone is your
   "high-perf demucs without NVIDIA."**
3. **W8A16 the backbone, FP16 the Fourier convs** (backfill #3) → footprint + throughput.
4. **Finish STFT→Conv surrogate** (backfill #4) → all-conv, full portability.
5. **DirectPort tile-branch** (backfill #2) → Hexagon NPU + arbitrarily long audio.

## The throughline
You don't optimize demucs *separately*. You build the dp-onnx GraphRuntime rungs **once** for gemma4/kokoro —
GPU-Conv, DirectPort tiling, W8A16, STFT-surrogate — and demucs, being the most conv-heavy and already
single-graph, is the model that benefits **most** from each. The backfill is the architecture: one VOM + one
DirectPort, N models. (This note pairs with `SYNTHESIS.md`; file a hive Request to mount it as the demucs
GraphRuntime track when you start.)
