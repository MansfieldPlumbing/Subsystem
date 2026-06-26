# Case study — Demucs v4 TRT (Scott's OC, self-derived, no tutorial)

This is the worked example of the move this whole mission is asking for: **suss out the
mechanical-sympathy win the silo was too clever to see.** Read the three artifacts beside this file
(`README.md`, `export_htdemucs__WaveformOnlyWrapper.py`, `demucs_v4_trt__C-ABI-bridge.cpp`) — they are
the primary source. This note distills why it matters here.

## The model
HTDemucs (Hybrid Transformer Demucs, Rouard et al. ICASSP 2023) — music source separation. **Dual-path:**
a time-domain encoder AND a frequency-domain (STFT) encoder that **cross-attend through a Transformer at
the bottleneck** before decoding. 6 stems out. The two branches MUST stay coupled — that coupling is the
model's quality.

## The silo move (what everyone did)
ONNX has no native complex-tensor support, so the STFT/ISTFT is "obviously" a preprocessing step → run
the FFT in host code and feed the spectrogram as a **second input** (sevagh/demucs.onnx, Mixxx GSoC,
ZFTurbo/MSS). Correct, sophisticated, and it **severs the seam the compiler needs whole**: TensorRT never
sees the FFT, can't fuse it with the surrounding convolutions, and the two cross-attending encoders get
compiled apart. Result: correct output, several MINUTES per song. They were so busy solving "unsupported
op" they couldn't see the simpler thing.

## The wu-wei (what Scott did)
**Don't cut there.** `WaveformOnlyWrapper` calls `model._spec()` *inside* the forward pass → a single
waveform input, all 6 stems out. TRT now sees the complete dataflow (STFT → dual encoders → cross-attention
→ ISTFT) as one subgraph and **fuses the FFT kernel chains with the convs**, co-compiles both encoders,
FP16 on Tensor Cores. Minutes → **~5 s on a 3090.** The work didn't get done faster — it *disappeared*,
because the model's own shape was left intact and the optimizer was handed the whole picture. Leave the
machine nothing to do.

> Scott's own words (README): *"understanding why the two-input approach blocks TRT fusion was the key
> insight that led to `WaveformOnlyWrapper`."* He did not read a tutorial — he sussed out that the
> university silo was too smart to see the wu-wei of it.

Two wu-wei artifacts in one repo:
1. **The export seam.** Keep `_spec()` in-graph; single-input; the ONNX is the one canonical checkpoint and
   every per-GPU `.trt` is just that graph re-compiled (one truth, N projections — invariant 3/9).
2. **The runtime seam.** `demucs_v4_trt.cpp` flattens TRT's C++ classes to a 3-call C ABI
   (`Trt_Init`/`Trt_Process`/`Trt_Destroy`) for C# P/Invoke; the hot path is push→fence→copy
   (`cudaMemcpyAsync → enqueueV3 → cudaMemcpyAsync → streamSynchronize`). Study the recipe, reimplement
   the seam, do not import the framework. This is the vom.h C-ABI parity pattern (CRQ130) and the DirectPort
   push/fenced/latest-wins shape, in the wild.

## Why it's load-bearing for Gemma-4 E2B/E4B (maps to the three deliverables)
- **Deliver 2 — FEDERATION MAP.** The lesson, generalized: **federate on the model's OWN seams (MatFormer
  slices, PLE tables, Mix-n-Match layer distribution), never on an artificially-clean preprocessing cut.**
  An externalization that looks tidy to a silo severs exactly the cross-unit links the runtime/transport
  needs whole. Keep subgraphs whole; let the compiler + DirectPort do the fusing/routing.
- **Deliver 1 — ONNX-FIRST export.** Internalize, don't externalize. Whatever the multimodal front-end is,
  keep it in-graph so the export carries the complete dataflow (the same reason kokoro's STFT stays exact
  and in-graph — see `../SESSION-COMMS.md`).
- **Deliver 3 — SINGLE-ENCODER.** This is the *direct* rhyme: HTDemucs proved a unified, single-input graph
  is both the export win AND the fusion win. If E2B/E4B fold modalities into one token stream / are
  encoder-free, that is the same simplification — confirm it and exploit it.
- **The STFT theme is already live here.** The kokoro/dp-onnx work (`../bench…result.md`, `../SESSION-COMMS.md`)
  is fighting the exact STFT/atan2-phase surface from the other side. Demucs is the "keep it whole and let
  the compiler eat it" precedent; carry it forward.

## Source of record
Canonical repo: `S:\reference\MansfieldPlumbing-Github\Demucs_v4_TRT` (published, github.com/MansfieldPlumbing).
QNN-side reuse: `S:\qnn-project\workspace\demucs\` (the HTP/DLC export experiments).
