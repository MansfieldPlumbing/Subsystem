# Gemma 4 E2B/E4B on dp-onnx, federated over DirectPort — synthesis

**Author:** onboarding/execution session, 2026-06-23. AUTHORITY: HF/Google model cards (cited) +
the binary/hive (cited) + the staged research. Cite-or-refuse (CONTRACT Rule 10): every architecture
number below is from the official Gemma 4 model card; re-ground before building.

> **READ `PHASE1-STATE.md` FIRST (2026-06-23).** It carries the live receipts (dp-onnx proven, op coverage)
> AND three accepted corrections that supersede claims here: (1) "ops run" ≠ "the LLM runs" — **the host-side
> decode loop + KV-cache + sampler + tokenizer is the build**, missing fused-or-decomposed; one token ≠ agent.
> (2) decomposed export defers cost (W8A16 = a `MatMulNBits` handler for fast+small). (3) dense ⇒
> pipeline/tensor-parallel federation, not expert fan-out. Treat §1's "loop lives in Rb" as THE deliverable, not a footnote.

---

## 0. Architecture — CONFIRMED against the model card (not the prompt's assumptions)

| | E2B | E4B |
|---|---|---|
| effective params | 2.3B | 4.5B |
| params incl. embeddings | 5.1B | 8B |
| decoder layers | 35 | 42 |
| vocab | 262K | 262K |
| context | 128K | 128K |

**E2B micro-architecture (from `config.json`, the binary — re-confirm E4B's at export):**
hidden 1536 · FFN(intermediate) 6144 · attn heads 8 · **KV heads 1 (MQA)** · head_dim 256 · sliding_window 512 ·
layer pattern **4 local : 1 global → 28 local + 7 global** (NOT 1 global — the model card's "final layer is
global" is true but incomplete). These numbers drive the §1.5 NPU budget.

- **MatFormer (Matryoshka):** E2B is a *true sub-model nested inside E4B* (35-layer / narrower-FFN slice
  of the 42-layer E4B). Shared weights. This is the single most important fact for the wins (§5).
- **Per-Layer Embeddings (PLE):** each decoder layer has its own small per-token embedding, **lookup-only,
  built to be offloaded to CPU.** This is the bulk of the "incl. embeddings" delta (E2B 5.1B vs 2.3B = ~2.8B
  sits in offloadable tables). PLE *is* the natural federation seam — not a problem to solve, a port to use.
- **Hybrid attention:** sliding window **512 tokens** on the 28 local layers; **7 full-global layers** (4:1
  pattern, last layer global); global layers use **unified K/V + Proportional RoPE (p-RoPE)**. **MQA (1 KV
  head)** → the KV cache is small by construction (one 256-d head, not 8).
- **Mix-n-Match:** custom sizes by slicing per-layer FFN width + skipping layers, between E2B and E4B.
- **Multimodal = SEPARATE encoders, NOT encoder-free** (this overturns the prompt's open question):
  **vision encoder ~150M** (MobileNet-v5 lineage), **audio encoder ~300M** (USM lineage), both feeding the
  LLM token stream. Only **Gemma 4 12B Unified is encoder-free.** So for E2B/E4B there are THREE token
  producers (text-embed, vision, audio) → one LLM consumer. (DELIVER #3 resolved — see §4.)
- Deployment path Google ships: **LiteRT-LM** (the .litertlm container — see the Roadmap).

Sources: [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4),
[google/gemma-4-E2B](https://huggingface.co/google/gemma-4-E2B),
[MatFormer in Gemma 3n](https://huggingface.co/blog/rishiraj/matformer-in-gemma-3n).

---

## 1. DELIVER 1 — ONNX-FIRST export path (Python allowed for EXPORT; runtime is .NET/pwsh)

Export **four graphs**, deliberately (this is federation, not the Chatterbox fragmentation anti-pattern —
the seams here are the model's own, see §3):

1. **`vision_encoder.onnx`** (~150M) — MobileNet-v5-class. Conv-dominated → rides the dp-onnx GPU-Conv lever
   (§5) directly. Static image shape.
2. **`audio_encoder.onnx`** (~300M) — USM-class. Static/chunked audio frames.
3. **`text_embed + decoder_step.onnx`** — the LLM body as a **single-token-step** graph (KV-cache in, logits +
   new KV out). **Do NOT internalize the autoregressive loop with `onnx::Loop`** (that's the Chatterbox move,
   needed only when the host loop is the bottleneck). Here the loop already lives in Rb's turn-contract —
   *"Rb.Runtime is LLM-turn-shaped (prompt→AgentDelta tokens)"* (`research/SESSION-COMMS.md`). So: **dp-onnx
   evaluates ONE decoder step; Rb's turn-contract IS the loop.** Add-don't-replace, clean seam.
4. **PLE tables** → exported as **external-data** TensorProtos (the 2 GB-protobuf sidestep,
   `research/QNN-LOG.md` "protobuf 2 GB limit"): the graph holds Gather references; the big tables live on
   disk/CPU and are **DirectPort-streamed per layer** (§3).

Export specifics carried from the demucs/kokoro work (`research/QNN-LOG.md`):
- `dynamo=False`, opset ≥ 17, `do_constant_folding=True`.
- **FULLY STATIC, chunked — NOT dynamic axes** (Scott, 2026-06-23, the NPU-fast-track decision). HTP/qairt
  needs compile-time shapes; dynamic axes force reallocation and block finalize. Fix a static chunk length `C`
  and a static pre-allocated KV block; the **causal mask + a slice index** blind the model to unfilled slots
  (the Roadmap's static-chunking pattern). Fold every dynamic `Range`/`Shape` with the dp-onnx `fold` verb
  (already built for kokoro — `research/QNN-LOG.md`). The KV ring is fixed-512 (sliding layers) by construction.
- **Size `C` to the 8MB VTCM budget — see §1.5.** This is what puts the fittable layers on the Hexagon
  fast-track (the proven RIFE/SD path) instead of the Mario-hybrid.
- Validate every surgery bit-exact via .NET `Microsoft.ML.OnnxRuntime`, never python (the floor).

## 1.5 NPU FAST-TRACK BUDGET — static chunk sized to ≤ 8 MB VTCM (Scott, 2026-06-23)

Goal: every op's resident activation ≤ **8 MB** (V73 / 8gen2 VTCM) so the layer **finalizes on stock qairt,
spill≈0, and runs on the Hexagon — the proven RIFE/SD path** (`research/QNN-LOG.md`), no Mario-hybrid. Budget at
**A16 (2 bytes/activation elem)**; W8A16 int8 weights cost no activation VTCM.

**Per-op working set at static chunk `C`, with E2B's real dims** (hidden 1536, FFN 6144, MQA 1-KV-head, sw 512):

| op (per local layer) | activation working set | at C=256 | at C=512 |
|---|---|---|---|
| FFN gate/up out `[C, 6144]` (the binding op) | `C·6144·2` | **3.0 MB** | 6.0 MB |
| attn scores `[8, C, 512]` (MQA, window 512) | `8·C·512·2` | 2.0 MB | 4.0 MB |
| local KV resident `[512,1,256]·2` (MQA!) | fixed | **0.5 MB** | 0.5 MB |
| q/k/v/o projections `[C, ≤2048]` | `C·2048·2` | 1.0 MB | 2.0 MB |

→ **A static chunk of `C = 256` keeps every local-layer op ≤ ~3 MB** — comfortable even after the ~2–2.5×
im2col/layout inflation qairt added to demucs's convs (matmuls inflate less, but budget for it). `C = 512` still
fits (≤6 MB) but leaves no margin. **Recommend C = 256.** So the **28 sliding-window layers static-chunk straight
onto the NPU fast-track** — MQA + window-512 make their per-op footprint RIFE-sized.

**The two things that do NOT fit — the federate-off / special-case set ("where possible" = these aren't):**
1. **The 262K-vocab LM head `[C, 262144]`** = `C·512 KB`; at C=256 that's **128 MB — busts hard.** Fixes,
   cheapest first:
   - (i) **chunk = 1 for logits** (last token only, autoregressive) → 512 KB. The floor rule; never run the head
     at full chunk.
   - (ii) **Shard the vocab into key-clusters** (free, export-time) → each head op ≤ 8 MB, so the head **joins
     the NPU set** instead of being shoved off: frequent cluster resident, rare keys DirectPort-streamed by handle
     (adaptive-softmax as a VOM namespace), scored only where the §5a swarm points.
   - (iii) **PHASE-2 — compositional "stacked-glyph" vocab** (Scott, a *bet*, NOT a first-move): a token = a stack
     of m small code-glyphs. *Post-hoc PQ+ADC* (no retrain, approximate→verify): split the 1536-d row into ~8
     subvectors × 256-entry codebooks → **8 bytes/token, head 400 MB → ~2 MB, scoring = 8 table-lookups** (FAISS
     ADC), which also produces the swarm's candidate set. *Trained compositional codes* (a distill/retrain) = the
     exact tiny head, the endgame for a fully-resident vocab on the phone. **Cost classes are distinct: (i)/(ii)
     are free now; (iii) is a research lane.** Confirm `tie_word_embeddings` — a tied head lets ONE stacked-glyph
     keyset serve press-in (Gather) and score-out (dot).
2. **The 7 global (full-attention) layers** at long context `L`: scores `[8, C, L]` and KV `[L,1,256]` blow past
   8 MB once `L` is large (L=128K KV ≈ 64 MB/layer). Fast-track only at *bounded* context; for long context they
   **federate off-NPU over DirectPort** (run the 7 global layers on CPU/GPU) or use streamed/online-softmax
   attention. This is the §5a "global-layer floor," now correctly **7 layers, not 1**.

**Net partition:** 28 local layers + both encoders (RIFE-class, small-spatial) → **Hexagon fast-track via
static C=256 + stock qairt**. The 7 global layers (long ctx) + the LM head (chunk=1) + the offloaded
embedding/PLE tables → **host/DirectPort federation**. That is the maximal "on the NPU where possible."

## 2. Why ONNX-first and not LiteRT-first
LiteRT-LM is Google's path and stays the **add-don't-replace** baseline (keep the working LiteRT LLM backend).
dp-onnx is the new GraphRuntime rung *beneath* the turn-contract (CRQ109) — it gives us the ORT-free, all-.NET,
D3D12-on-Radeon path Google's stack doesn't, and it's the same interpreter already running kokoro's 2463 nodes.

---

## 3. DELIVER 2 — FEDERATION MAP (the core: cut on the model's OWN seams)

The demucs lesson generalized (`research/demucs_v4_trt-casestudy/CASE-STUDY.md`): **federate on the model's
own seams, never on an artificially-clean cut.** Gemma 4's seams are unusually clean — Google built them to
be offloaded:

| Model seam | What federates | DirectPort role | KV / state |
|---|---|---|---|
| **3 encoders → 1 stream** | vision(150M), audio(300M), text-embed each on its own device/graph | 3 **producers** → 1 fenced **consumer** (the LLM). `Fence.WaitN(n=present-modalities)` gates the merge — wait for exactly the modalities this turn carries (`Vom/Fence.cs:38`, the 2-of-3 consensus primitive, literally) | encoder outputs are stream frames, latest-wins |
| **PLE per-layer tables** | the offloadable per-layer embeddings | **per-layer DirectPort stream** — table host (CPU) pushes layer L's embedding as a frame; decoder layer L consumes it. Best-effort/one-to-many = many decoder devices subscribe | tables are read-only; pure projection |
| **MatFormer nest (E2B ⊂ E4B)** | E2B sub-model = device A (the fast common core); E4B's extra 7 layers + wider FFN = device B | activations routed A→B device-to-device, best-effort/fenced | per-branch KV as VOM handles |
| **Mix-n-Match layer skips** | the layer-distribution knob | decides WHICH device holds which layers at mount time | — |
| **Sliding-window (512) KV** | each of the 28 local layers' KV | a **fixed-512 ring = one VOM handle, latest-512-wins** — DirectPort's exact shape; MQA (1 KV head) makes it tiny. The **7 global layers** need full-128K KV (federated off-NPU, §1.5) | bounded by construction — the federation's memory win |

The whole model becomes a **VOM mesh of GraphRuntime nodes wired by DirectPort fences** — on-thesis by the
coherence test (VOM-shaped nodes, DirectPort transport, intraprocess where co-resident, thin per-head seams).
Route activations device-to-device (CPU + Radeon + Hexagon + phone); hold each branch's KV as a VOM handle so
rollback (§5) is a handle reset.

**The sliding-window + PLE combo is the unlock:** because 41 of 42 layers only ever attend to 512 tokens and
pull a lookup-only embedding, each is a *bounded, stateless-ish* unit — exactly what federates well over a
best-effort latest-wins port. Gemma 4 is, by accident of Google's on-device design, pre-shaped for DirectPort.

---

## 4. DELIVER 3 — SINGLE-ENCODER, resolved

**E2B/E4B are not encoder-free** — they run a 150M vision + 300M audio encoder (only 12B Unified folds them
out). So the prompt's "unified single stream" does NOT apply at E2B/E4B. **But the federation gets the same
simplification a unified stream would give**, without needing 12B: treat the three encoders as **three
DirectPort producers writing one fenced token stream**. The LLM consumer does
`Fence.WaitN(producers, targets, n = modalities-present-this-turn)` and evaluates once the quorum holds — text
only (n=1), text+image (n=2), all three (n=3). The "one token stream" is reconstructed *at the transport*, not
baked into the graph — which keeps export simple (each encoder is its own small static graph) AND keeps
federation simple (add/drop a modality = add/drop a producer). If a true single-stream is wanted later, the 12B
Unified is the drop-in; the producer/consumer wiring is identical, just one producer instead of three.

---

## 5. DELIVER 4 — APPLY THE WINS

### 5a. The elastic speculative SWARM — one weight set, many short-circuit paths (Scott, 2026-06-23)

**Not two models — one elastic model swarmed.** MatFormer/Mix-n-Match makes E4B a *continuum* of valid
shared-weight sub-models (skip layers + slice FFN width, no retrain). So instead of a single E2B draft + E4B
verify, run a **swarm of elastic configs** through the one weight set and race them. This is the documented
*advantage* of the nested architecture, not a hack: the [MatFormer paper](https://arxiv.org/abs/2310.07707)
notes independently-trained draft/verify models are *behaviorally inconsistent* (bad acceptance), while nested
sub-models *agree* — higher accept rate, bigger speedup. It's the self-speculative / layer-skip family
([Draft & Verify](https://arxiv.org/pdf/2309.08168), 1.99× no-retrain; [LayerSkip](https://huggingface.co/blog/layerskip);
[CLaSp](https://arxiv.org/html/2505.24196v1)) — but swarmed and distributed, which is the part that's ours.

**Mario-hop:** each config short-circuits layers where the token is easy (shallow path guesses right → leap),
touches down to full depth where it misses (verify + correct). Accept test = shortcut argmax == full-depth
argmax. Map to primitives: each elastic path drafts into its **own KV branch (VOM handle)** → full-depth verify
→ **`Fence.WaitN` quorum** on the agreed prefix → **DirectPort latest-wins** publishes the winner →
**slot-rollback** (`Vom/Slot.cs`, CRQ135) discards losers by handle reset.

**The swarm IS the federation map.** It is *not* M paths on one GPU — it's **M devices each running a
different-depth slice of the same weights**: CPU @ depth-12, Radeon @ depth-24, Hexagon @ E2B-exact, phone @
depth-8, racing over DirectPort. Distributed self-speculative decoding over a best-effort fenced fabric — §3 and
this section are one mechanism.

**KV cost is a trunk + confetti, NOT memory × paths** (the objection, corrected):
- The **committed prefix is shared** — all branches extend the same accepted context, so its KV is computed
  **once**, held as **one VOM handle**, *referenced not copied* (standard shared-prefix / tree-attention KV à la
  SpecInfer/Medusa). `× M` hits only the **speculative tail**: `k` tokens, ephemeral, discarded on accept/reject.
- **Bounded:** the 28 local layers (E2B) are sliding-window-512 → each shares a fixed 512-ring regardless of
  128K context, and MQA (1 KV head) shrinks it further. The **7 global layers are the real memory floor**
  (full-context KV) — shared (paid once, not × M), can live host-side and stream (the PLE-offload pattern).
  This is the same set §1.5 federates off the NPU.
- Shallow paths hold **less**, not equal-separate — a depth-12 draft never materializes KV for skipped layers.
- Honest catches: **commit is the one fenced write** (verifier's full-depth KV writes the trunk while branches
  read — a reader/writer hazard `Fence` resolves, monotonic, no torn read); and **skip-sets must be
  self-consistent across a branch's run** (predetermined/context-picked à la CLaSp), not random per token, or the
  branch desyncs its own cache.

### 5b. KV as a DirectPort texture (RGBA) — store/move/mutate, not matmul (Scott, 2026-06-23)

On a generic runtime "KV as RGBA" is cute reframing; **on THIS stack it's right, because DirectPort is a
D3D12/Vulkan _texture_ fabric** (BufferToTexture→ShaderFilter→TextureToBuffer+fence). Storing the KV trunk as a
texture makes it a **first-class DirectPort citizen**: zero-copy shared handle across devices/processes, the
512-window is a 512-wide texture, latest-wins is a texture write, multi-branch read is a shared SRV.
- **Sub-texel = sub-pixel:** W8A16 int8 KV packs 4 values/texel into **RGBA8**; A16 → **RGBA16F**. Texture
  samplers do **scale/bias on read** → free dequant. The Mario "compute-wide/store-narrow" mnemonic closes
  literally at the texel. (Prior art: WebGL-era GPU-ML, e.g. TF.js, stored *all* tensors as packed RGBA textures
  with fragment-shader kernels — natural again here.)
- **"Subjectify" = projection made physical.** One shared KV texture (the trunk); each branch/device applies a
  **cheap shader pass** to get its subjective view — p-RoPE rotation (a per-element complex multiply = trivial
  shader), masking, attention-bias. One truth, many lossy maps, at texture rate — don't copy-and-edit, project
  via shader. (The stronger reading — *editing* KV to steer generation, activation/representation engineering — is
  a real but **unproven** research lane; sandbox it, don't put it on the correctness path.)
- **Don't oversell:** the win is **storage / transport / mutation** of KV, not the `QK^T` matmul itself —
  attention still reads it into a compute-friendly layout. And texture axis limits (16384) + head_dim packing
  force a tiling scheme (the demucs tiling discipline). Real, solvable, not free.

### 5c. Record every variation — the swarm is a telemetry harness

"See what sticks to the wall": instrument the swarm as a data-collection pass *before* committing to a policy.
Per `(config, token-position, token-type)` log: **accept/reject, accept run-length, skipped-layer set,
draft-vs-full logit gap/confidence**, and optionally the **KV-delta texture** (so you can literally *look* at
where branches diverge as images). Mine it for a **learned skip policy** — maybe whitespace/punctuation accept at
depth-8, code needs full depth, certain layers are always-skippable. CLaSp/ConfLayers do this adaptively; we
*discover* it empirically first, then exploit. This subsumes the §6 accept-rate bench.

### 5d. The other wins (unchanged)
- **W8A16 "sub-pixel" quant** (`research/QNN-LOG.md`, read off the SD-UNet `.bin`): int8 weights / A16 acts /
  int32 accumulate / per-channel `w_scale·a_scale` / asymmetric activation offset. Apply to the FFN + attention
  matmuls. **Selective-precision caveat (from the Roadmap, the demucs Fourier-conv lesson): keep the
  p-RoPE / softmax / any phase-or-normalization-sensitive node in FP16.** PLE/vocab tables stay int8 lookup,
  offloaded — they never hit the accumulator.
- **GPU-Conv lever** (`research/bench…kokoro-parity.result.md`: 63% conv, lever = D3D12 dpgpu mount): the
  transformer body is matmul-bound, BUT the **vision encoder is MobileNet-v5 = pure conv** → it rides the exact
  GPU-Conv mount we build for kokoro/demucs. Audio encoder likely conv-front too. So the encoders backfill the
  demucs GPU-Conv work and vice-versa (see `DEMUCS-BACKFILL.md`).
- **Add-don't-replace:** keep LiteRT LLM backend; dp-onnx is a new GraphRuntime rung beneath the turn-contract.
  Nothing in Rb forks.

---

## 6. CONCRETE FIRST MOVE (what the next actor does, in order)

1. **Start with E2B (the NPU target), export ONE local decoder block, fully static at C=256** (hidden 1536,
   FFN 6144, MQA, sw 512, p-RoPE; static pre-alloc KV-512 ring + causal mask/slice). PLE tables external-data,
   LM head excluded (separate, chunk=1). Python env per `research/QNN-LOG.md` `demucs-export` recipe
   (`dynamo=False`, opset 17). **One block first** — prove the per-op budget empirically before the whole stack.
2. **Static-ify → qairt → V73 finalize that one block** (`onnxsim --overwrite-input-shape`, `fold` any dynamic
   Range, `build_v73.ps1`) and **read the finalize spill number** (`qnn-context-binary-utility --json`,
   `spillFillBufferSize`). Target: **spill≈0, no op > 8 MB** — i.e. it lands like RIFE, not like demucs. This is
   the §1.5 budget's empirical receipt; if an op busts, the real number tells you to drop C.
3. **In parallel, run the same block on dp-onnx for ONE step** and diff logits vs ORT (kokoro parity method,
   rmse ≤ 1e-3). Implement any missing op-types (p-RoPE, sliding-window mask) — kokoro proved the interpreter
   extends cleanly. dp-onnx is the x86 oracle; the `.bin` is the device target.
4. **Wire the turn-loop in Rb** over the validated block ×28 (+ the 7 global layers off-NPU, LM head chunk=1)
   → first end-to-end E2B generation, local NPU where it fits. **Verified-done milestone to bench.**
5. THEN split on the seams (§3): PLE per-layer DirectPort stream, MatFormer E2B/E4B split, encoders as producers,
   then the elastic swarm (§5a).

**Milestone bench to file as the receipt:** `bench.rb.graphruntime-gemma4-e2b-step-parity` — dp-onnx single
decoder step logits vs ORT, rmse gate ≤ 1e-3, the twin of the kokoro parity bench.

5. **THEN the elastic-swarm measurement** (§5a/§5c) — `bench.rb.graphruntime-gemma4-elastic-accept`: take E4B,
   pick ~5 configs (E2B-exact, E2B−4 layers, two Mix-n-Match width slices, depth-12); over a few hundred real
   token positions record per-config **accept rate** + **accept run-length distribution**, bucketed by token
   type, under the **trunk-shared + streamed-512-window** KV assumption (so the reported memory is real, not
   copy-per-branch). dp-onnx can run this today (early-exit / narrow FFN over its per-node eval) — **no DirectPort
   plumbing yet.** Decision gate: cheap-config run-length **≥ ~2** ⇒ the swarm is real, build the DirectPort
   race + the KV-texture trunk (§5b); **~1** ⇒ we learned it cheaply, fall back to single-E2B-draft. This is the
   experiment that settles "does it still guess right."
