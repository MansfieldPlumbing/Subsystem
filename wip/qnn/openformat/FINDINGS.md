# QNN HTP context-binary — home-roll feasibility (banked findings)

Empirical (dissecting `AnythingV5/vae_encoder_8gen1.bin`, a real QAIRT-2.28 conversion) + a deep-research
pass over primary sources (ORT QNN EP code, ExecuTorch, llama.cpp QNN PR #12063, Qualcomm AI Hub docs).
**The container is boring; the weights are not.** Honest verdict below.

## CRUX — resolved (and more nuanced than first thought)
The `.bin` is **the output of an offline, SoC-specific HTP "preparation"/compile step** — NOT a portable raw
graph, but also **NOT frozen DSP machine code.** It's a *compiled-but-parameterized* artifact:
- Offline "preparation phase: potentially expensive SoC-specific compilation" (AI Hub; ExecuTorch AOT, soc_model required).
- Parameterized at load: `spillFillBufferSize` is read from the blob and fed back into context config; weight-sharing /
  multi-context register treat weights+spill as **runtime resources, not baked-in immutable code** (ORT QNN EP source).
- Metadata is a documented versioned struct: `QnnHtpSystemContext_GraphBlobInfo_t` via `graphInfoV3.graphBlobInfo`,
  `QNN_SYSTEM_CONTEXT_GRAPH_INFO_VERSION_3` — matches our decoded `rife_v73_info.json`.

## What's empirically BORING (solved)
- **Container** = struct header + u64 **section table** + flat **TLV op/tensor table** (`[tag u32][len u32][name][fields]`,
  names u32-length-prefixed) + trailing int8 const blob. NOT flatbuffers/protobuf.
- **Full graph is plaintext** (ONNX node names; Conv/attention/GroupNorm/Softmax markers; vtcm/spill graph-info).
- **No encrypted/compiled-DSP region** (entropy 4.7–7.3; weight bulk ~6.5 = int8).
- **Loader** = stock Qualcomm `qnn-sample-app` `createFromBinary`; local-dream's `SampleApp.patch` is pure plumbing
  (mmap the bin, zero-copy dequant). The bin is data into a signed runtime ⇒ **no signing problem.**
- **Variant patching** = `zstd --patch-from` (base bin as dict). 837 MB base → 11–15 MB resolution patch ⇒
  **weights are identical across resolutions; only shape structure changes.**

## What's actually HARD (the real wall) — corrects earlier optimism
The const-weight blob is **SoC-prepared (HMX/HVX swizzled + tiled), not raw int8.** So "emit the TLV container by
hand" is **necessary but not sufficient** — a valid bin needs weights in the prepared HTP layout.
- llama.cpp QNN (chraac #12063): builds the QNN graph programmatically via the op/graph API — but **quantized matmul
  on HTP is unsolved**; "mapping block-quantization into QNN tensor/weight layouts" was WIP. The weight swizzle is the
  open problem in the open-source world too.
- Our `qnn_laptop_handoff.md` HMX 1024-byte swizzle (32×32 → (32,8,4) → transpose) is the spec to reverse/confirm.

## THE SOVEREIGN PATH — confirmed by primary sources
**Route A (recommended, proven): build + finalize on-device, then `contextGetBinary`.**
- `contextGetBinarySize` + `contextGetBinary`: **any code holding a finalized `Qnn_ContextHandle_t` emits the same
  .bin** — the SDK's `qnn-context-binary-generator` is NOT the only path (ORT QNN EP source, primary).
- Build the graph via the **QnnGraph op/graph API** from our `.dpblob`/`model.db` (llama.cpp proves it's constructible),
  **finalize ON-DEVICE** (the runtime does the HTP prepare — "on-device otherwise"), then `getBinary` to cache the `.bin`.
- Uses on-device `libQnnHtp` (already on every device). **No qairt-converter, no qnn-context-binary-generator, no Python,
  no offline SDK.** The device's own runtime does the swizzle+compile. Sovereign at runtime.

**Route B (max sovereignty, harder): hand-emit the bytes.** Container = solved (TLV). Blocker = the HTP weight
swizzle/tiling + reproducing the prepared graph form. Crack the swizzle by triangulating a known conv weight's
pre-image (from safetensors) against its swizzled bytes in a dissected bin.

## Architecture (unchanged, clean)
`model.db` (sqlite) = the VOM truth (tensors/ops as rows, weights as blobs, queryable/diffable). The QNN `.bin`,
the `.dpblob`, a Vulkan pack are **lossy device projections**. Route A materializes the `.bin` on-device via the API;
variants ship as `zstd --patch-from`; the user supplies open weights (no copyrighted asset ever hosted).
Legal posture: non-commercial interoperability + reverse engineering = Sega v. Accolade / Sony v. Connectix /
DMCA §1201(f). Route A doesn't even circumvent anything (uses the runtime as intended).

## Next levers
1. **Route A prototype:** drive the QnnGraph API to build ONE op (a Conv), finalize on-device, `getBinary`, diff the
   emitted bin's container against a local-dream bin to confirm we produce the same shape.
2. **Weight swizzle crack (needed for B, and for correctness on A):** triangulate a known conv weight ↔ its prepared
   bytes; reverse the HMX 1024-B tile. This is THE remaining hard kernel.
3. `dissect.py`/`grammar.py` (this dir — recon tools, NOT in the dpx engine) — point them at the AnythingV5 bin
   + the AnythingV5 safetensors to map blob↔weight, then study the byte transform. This is research, not production.

_Artifacts: `dissect.py`, `grammar.py`, `vae_encoder_8gen1.bin`, `768/1024.patch.7d9838`, `SampleApp.patch` in this dir._
