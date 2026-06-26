# Vega gfx900 SD-UNet port — analysis & corrections

Reviewer pass over the qnnscripts pipeline (HTP context-binary extraction →
AMD Radeon Pro V340 / Vega 10 / gfx900 DirectCompute int8 runner).
Date: 2026-06-11.

## The big-picture finding: you have two strategies that cancel each other out

There are two independent weight-acquisition paths in this folder:

- **Strategy A — reverse-engineer the HTP blob.** `extract_weights.py` →
  `match_blobs_to_ops.py` → `deswizzle.py`, validated by `groundtruth.py`.
  Lift the already-quantized int8 weights out of the 880 MB Qualcomm HTP
  context binary by matching 5,877 weight ops to 9,946 blobs by size, then
  un-tiling the HMX swizzle.
- **Strategy B — re-quantize the public weights.** `build_vega_unet.py`
  downloads the original UNet (FP16) from HuggingFace and does your *own*
  symmetric per-channel int8 quant + Wave64 padding.

If the goal is "run this UNet on gfx900," **Strategy B makes Strategy A almost
entirely unnecessary** — and it is dramatically simpler and more numerically
correct (you control the quant scheme; no blob-matching cascade risk; no HMX
layout guessing). Strategy A is only worth finishing if the HTP blob contains
weights you *cannot* otherwise obtain — i.e. a private finetune baked into the
context binary. For a stock checkpoint the weights are public, so prefer B.

**Model-identity mismatch that will silently corrupt Strategy A's validation:**
`groundtruth.py` fetches **epiCRealism** (`emilianJR/epiCRealism`) but
`build_vega_unet.py` pulls **runwayml/stable-diffusion-v1-5**. These are
different finetunes — their weights do NOT match. Decide which model the HTP
blob was compiled from and use that one in BOTH scripts, or the
distribution-comparison validation is meaningless.

## Correctness bugs in `matmul_s8_gfx900.hlsl`

1. **No bias term.** The epilogue is `acc * scale` only. SD-1.5 ResNet convs,
   `proj_in/out`, `to_out.0`, and the time-embedding Gemms all carry biases
   (note: `attn1/attn2 to_q/k/v` are bias-free, which is why the mid-block
   `attn1.to_q` test layer happens to look correct). `build_vega_unet.py`
   already keeps biases as FP16 — wire them into the epilogue:
   `fp16_out = (float)acc * scale + bias[out_channel];`

2. **Zero-point / activation-scale folding.** `acc * scale` is correct ONLY if
   *both* operands are symmetric (zero-point 0). Weights are symmetric (good,
   from B). But the HTP source activations were `UFIXED_POINT_16` **asymmetric**
   (`offset -32907` in `model_metadata.json`). The shader consumes **int8**
   activations, so you must symmetrically re-quantize activations at runtime —
   and then the per-channel `scale` in `ScaleTable` must be the **product**
   `w_scale[oc] * a_scale`, not the weight scale alone. If `ScaleTable` holds
   only the weight scale, every output is wrong by the activation-scale factor.
   If you instead keep activations asymmetric, you owe the standard correction
   term `- a_zp * sum_k(w[oc,k])` (precompute `sum_k w` per channel once).

3. **`int16_t4` needs SM6.2 + `-enable-16bit-types`, and buys nothing for
   correctness.** int8×int8 sums fit in `int` for K≤~130k. More importantly:
   **gfx900 (Vega 10) does NOT have the int8 dot instruction** (`V_DOT4_I32_I8`
   is gfx906+/Vega 20 "DL ops"). So there is no `dp4a` fast path on a V340 —
   scalar `int` math is the correct baseline. Vega 10 *does* have rapid packed
   math (RPM) for **fp16/int16** (`V_PK_*`), so an int16x2 packed path is
   theoretically reachable, but only via the right dot/packed intrinsic — the
   current scalar `int16_t` muls won't trigger RPM and the compiler will widen
   them anyway. Recommendation: use `int` for a correct baseline; pursue
   int16 packed-math only as a measured optimization with explicit intrinsics.

4. **Hardcoded shape (`k < 320`, stride `1280`).** Fine for the single-layer
   proof, but the general runner needs `M, K, N` (and bias/scale strides) from a
   constant buffer.

## Performance note (`matmul_s8_gfx900.hlsl`)

One thread per output channel, each thread re-loads **all** activations from
`t1` → for N=1280 outputs the activation vector is read 1280× from VMEM. On
Vega, stage the activation slab into `groupshared` once per threadgroup
(`numthreads(64,…)` → 64 channels share one load), barrier, then loop. This is
the single biggest win and is a textbook GEMV-on-GCN optimization. gfx900 is
Wave64, so 64-lane thread groups map cleanly to one wavefront.

## `deswizzle.py` — correct for the 1×1/Gemm case, incomplete otherwise

The HMX un-tiling (32×32 tile = 1024 B = 8 groups of [32 OC × 4 IC], transpose
`(1,0,2)` → 32×32) is a clean, plausible recovery of the Hexagon vector
packing. Gaps before it generalizes:

- **Hardcoded 1280×1280.** Parameterize `(out_ch, in_ch)`.
- **Channel padding to the 32 tile.** HTP pads channel counts up to a multiple
  of 32 (the tile size). `conv_in` (in_ch=4) is stored as in_ch=32 — read the
  padded tiles, then slice back to the real channel count. This also explains a
  chunk of `match_blobs_to_ops.py`'s unmatched ops (size estimates use the
  unpadded dims).
- **3×3 convs.** The current routine only models a pure `out×in` matrix
  (1×1 conv / Gemm). A 3×3 conv in HMX is either im2col'd (in_ch_eff = in_ch×9)
  or stored as 9 separate `out×in` planes — confirm which by byte-size before
  trusting any 3×3 deswizzle.
- **No validation gate.** Close the loop with `groundtruth.py`: deswizzle a
  layer, dequant it, and correlate against the *same* layer's public FP16
  weights (same model!). High correlation ⇒ the layout guess is right. This is
  the only way to know the transpose order is correct rather than coincidental.

## `match_blobs_to_ops.py` — fragile greedy walk (only matters if you keep Strategy A)

- In-order greedy matching with a `[0.9×, 2.0×]` size window and a 10-blob scan:
  **one bad match cascades** — a greedy walker never recovers alignment after
  it consumes the wrong blob. The 2.0× upper bound is wide enough to grab a
  scale/bias table instead of a weight.
- Tighten by matching on **exact expected size rounded to HTP alignment**
  (typically 128 or 256 B) and by recognizing the weight→scale→bias **triplet**
  structure around each op rather than a single blob.
- Fold in the channel-padding-to-32 rule so `conv_in`/`conv_out` estimates match.

## Bottom line / recommended next moves

1. **Pick the model** (epiCRealism vs runwayml SD1.5) and use it everywhere.
2. **Go Strategy B** (`build_vega_unet.py`) unless the blob holds a private
   finetune. It sidesteps blob-matching and HMX deswizzle entirely.
3. **Fix the shader epilogue**: add bias; make `ScaleTable = w_scale*a_scale`;
   drop `int16_t` for `int` (no dp4a on gfx900); parameterize M/K/N.
4. **Add the groupshared activation stage** for the real perf win.
5. Keep `deswizzle.py` only as a research/validation tool — and gate it with
   `groundtruth.py` correlation before trusting any extracted tensor.
