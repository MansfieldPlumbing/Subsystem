# Integration Prompt — Gemma 4 E2B on dp-onnx (the GraphRuntime LLM rung)

**To:** the integration specialist, **after** kokoro lands. You own dp-onnx — this is the Gemma-shaped work
plus the architecture Scott and I converged on. Not dp-onnx basics; the decisions and the exact gaps.

**FLOOR (unchanged):** .NET / PowerShell at runtime, **no python except one-time export**; dogfood `ss.exe`;
query the live binary, never trust a stale `.md`; cite-or-refuse; ground against `gemma4.cpp` + the binary,
not training priors. **ONBOARD:** read `PHASE1-STATE.md` then `SYNTHESIS.md` (both in `S:\spawn-gemma4\`),
then `ss onboard` from `subsystem-main`. The wu-wei case study is `research/demucs_v4_trt-casestudy/`.

---

## 1. WHAT'S ALREADY TRUE (receipts, reproduce before trusting)
- **dp-onnx runs E2B's ops.** `dp-onnx probe W:\gemma4\onnx\e2b_step.onnx` → **`implemented op-types=34/34,
  MISSING: none`**. The decomposed E2B text decoder (10,164 nodes) is op-complete on the engine. External-data
  weights parse.
- **E2B is exported, decomposed, on disk.** `W:\gemma4\onnx\e2b_step.onnx` (2.3 MB graph) + external-data
  sidecars on W: including `embed_tokens_per_layer.weight` **4.48 GB** (PLE) and `embed_tokens.weight` 768 MB
  (tied vocab). Produced by `S:\spawn-gemma4\export_gemma4_e2b_decomposed.py` (HF E2B, `dynamo=False`, eager
  attn, opset 17, no-cache 8-token prefill; needed a custom `aten::diff`→Slice+Sub symbolic).
- **Arch confirmed from the trace + `gemma4.cpp`** (`S:\bin\llama.cpp\src\models\gemma4.cpp`,
  `S:\deliverables\claude-memory\gemma4-architecture.md`): **dense** (no MoE in E2B/E4B), **35 layers, hidden
  1536, FFN 6144, MQA (1 KV head), head_dim 256, sliding_window 512, 4:1 local:global → 28 local + 7 global,
  vocab 262144, tied embeddings, logit softcap 30, GeGLU, sandwich-RMSNorm, QK-norm + V-norm, cross-layer KV
  sharing (last N layers compute no K/V), built-in MTP draft head (`h_nextn`)**.
- **dp-onnx blocks on RUN at:** `initializer dtype 16` (bf16) in `Program.cs` `FromProto` (~line 1857). That's
  the door. See §4.

## 2. WHERE EVERYTHING IS
- ONNX + weights: `W:\gemma4\onnx\` · HF cache: `W:\hf-cache` · export log: `W:\gemma4\export.log`.
- dp-onnx engine: `S:\qnn-project\workspace\onnx-interp\` (`Program.cs`, `publish-aot\dp-onnx.exe`). **Parallel
  copy at `S:\spawn-fusion\onnx-interp\` — confirm who owns which before editing; do NOT double-edit.**
- Models (deployment): `S:\models\gemma-4-E2B-it*.litertlm` (+ Qualcomm-targeted variant), 12B gguf.
- Oracle: llama.cpp E2B (`gemma4.cpp`) logits. Plans: `SYNTHESIS.md`, `DEMUCS-BACKFILL.md`, `PHASE1-STATE.md`.

## 3. THE ARCHITECTURE WE CONVERGED ON (internalize — these are the decisions, not options)
1. **Weights are handles/allocations, NOT managed arrays.** The 2.35 B-element PLE table can't be a `float[]`
   (past .NET's 2.1 B array cap and 2 GB `Span`). Weights live **below the object layer** as a native/mmap'd
   region (a VOM allocation, addressed by handle — CRQ130/CRQ135 shape). The managed `float[]` + `Vector<float>`
   SIMD path stays — **for activations** (small, hot, per-step). We pointed the array strategy at the wrong tier.
2. **The real axis is access pattern, because RAM is not random access.**
   - **Sequential** (matmul weight streaming): mmap-region is genuinely cheap — OS prefetch, DRAM row-buffer,
     NVMe ~3–7 GB/s. Stream the K×N matrix in order.
   - **Random** (embedding / PLE / KV gather): the villain. Cold = page-fault-per-row = seeks (10–100× the
     sequential cost); warm = TLB/cache misses. **Fix:** bound the gather to the window's *unique tokens*
     (a handful × layers = small working set), **gather once up front, then use sequentially** (= the
     PLE-per-layer stream, justified by locality, not just size). KV in a contiguous ring, never a scattered map.
3. **Virtualize the IR — park the instructions, blob the weights.** The 10,164-node graph is the *unrolled
   projection* (torch traced 35 layer copies). The truth is ~**3–4 parameterized layer-templates** (SWA-layer,
   global-layer, kv-shared-layer) dispatched per-layer over weight-handles — exactly how `gemma4.cpp` represents
   it (a layer LOOP, not an unroll). This is what makes JIT-assembly cheap (§ below) and the elastic swarm
   trivial (skip = don't dispatch the routine; federate = dispatch elsewhere).
4. **The LLM is the LOOP, not the one-shot graph.** `e2b_step.onnx` is one forward. The actual build is the
   **host-side decode loop**: tokenizer → prefill → per-token step (past_kv→present_kv) → **sampler** →
   detokenize, with **KV-cache as VOM handles**. This lives in Rb's turn-contract (`Rb.Runtime is LLM-turn-
   shaped`). "Ops covered" = one token runs; the agent is the loop around it.
5. **`Tensor` gets two backings:** managed `float[]` (activations) | native-region view (weights, bf16 upcast
   per-tile at access). That single split dissolves both engine gaps (§4) at once.
6. **JIT slightly-ahead-of-time → `cmdlet gemma4-e2b`.** From the *virtualized* IR, assembling the cmdlet is
   **compile-bound, not load-bound**: bind weight-handles ≈ 0 (mmap, no read), lift IR sub-second, Roslyn-compile
   ~3–4 small layer-templates ≈ **1–3 s cold, ~instant if the assembly is cached**. Weights contribute nothing.
   (Beware: emitting the *unrolled* 10K nodes = a ~7 MB `.cs` — Roslyn grinds; kokoro's `emit` was already
   1.8 MB `.cs` for 2463 nodes. The dedup is what keeps assembly fast. `emit` verb already exists.)

## 4. THE TWO ENGINE GAPS TO FIRST LOGIT
**Gap #1 — bf16/fp16 initializer load (trivial, ~2 lines, do first to unblock).** In `Program.cs` `FromProto`
switch (~1857), add:
```csharp
case 16: { var raw=t.RawData.Span; var f=new float[n]; for(int k=0;k<n;k++) f[k]=BitConverter.Int32BitsToSingle((int)((uint)(ushort)(raw[2*k]|(raw[2*k+1]<<8))<<16)); return Tensor.F(f,dims); } // BFLOAT16
case 10: { var raw=t.RawData.Span; var f=new float[n]; for(int k=0;k<n;k++) f[k]=(float)BitConverter.UInt16BitsToHalf((ushort)(raw[2*k]|(raw[2*k+1]<<8))); return Tensor.F(f,dims); }       // FLOAT16
```
This alone makes the 768 MB tied embed + the per-layer weight matrices load. It does NOT fix the 4.48 GB PLE
(still > array/Span limits) — that needs Gap #2.

**Gap #2 — weights-as-native-region (the real one; §3.1/3.2/3.5).** `Tensor` native-region backing + mmap of
external-data files + **gather rows without materializing** (per-row bf16 upcast). This is mandatory for the PLE
and is the right shape for ALL weights (sequential matmul streaming + bounded random gather). Duct-tape
alternative to get logits *sooner*: a graph-surgery pass that splits `embed_tokens_per_layer.weight` into 35
per-layer initializers (≈128 MB each, under limits) — small eager arrays, ~9 GB fp32 PLE RAM (fine on the
workstation, NOT the phone). Split unblocks the proof; the region is the architecture.

## 5. ORDERED INTEGRATION PLAN
1. **Gap #1 (bf16 load).** Rebuild dp-onnx, re-probe — confirm it gets past the 768 MB embed.
2. **Gap #2 (native-region `Tensor` + mmap gather)**, or the per-layer split duct-tape if you want logits first.
3. **`dp-onnx run e2b_step.onnx`** (write `input_ids.bin`) → **first E2B logits**. Diff vs llama.cpp oracle,
   **rmse ≤ 1e-3** = `bench.rb.graphruntime-gemma4-e2b-step-parity` (twin of the kokoro parity bench). THE milestone.
4. **The decode loop** in Rb: prefill + per-token, KV-cache as VOM handles, sampler, tokenizer/detokenizer →
   first end-to-end E2B generation. (Re-export a KV-cache single-step graph: `use_cache=True`, past_kv in/out.)
5. **Virtualize the IR + JIT the cmdlet** (§3.3/3.6) → `cmdlet gemma4-e2b`.
6. Then the rungs (§7).

## 6. VALIDATION / ORACLE / BENCHES
Oracle = **llama.cpp E2B logits** (no ORT for gemma). Pattern = `tests/bench.rb.graphruntime-kokoro-parity.ps1`.
Queue: `gemma4-e2b-step-parity` (rmse ≤ 1e-3), `gemma4-elastic-accept` (swarm accept-rate / run-length, §7),
`gemma4-droppath` (§8). Every graph surgery gets a bit-exact `.NET OnnxRuntime` check — never ship unverified.

## 7. THE RUNGS BEYOND FIRST LOGIT (SYNTHESIS.md has the detail)
- **W8A16 "sub-pixel" / `MatMulNBits` quantized fused matmul** — fast+small (the size/speed the decomposed graph
  defers). Keep p-RoPE/softmax/norms FP16 (the demucs Fourier-conv lesson).
- **Static C=256 ≤ 8 MB-VTCM chunk → V73 finalize** (SYNTHESIS §1.5): the 28 local layers + encoders fast-track
  onto Hexagon (RIFE/SD profile); the **7 global layers + the 262K LM head (run at chunk=1) + PLE** federate
  off-NPU. MQA + sw-512 make the locals fit at C=256 (~3 MB/op).
- **Elastic speculative swarm** (SYNTHESIS §5a): one weight set, MatFormer/layer-skip, raced over DirectPort,
  `Fence.WaitN` quorum, slot-rollback (`Vom/Slot.cs`). The **MTP draft head is shipped** — generalize it. KV =
  shared trunk (VOM handle, sw-512 ring, MQA-tiny) + ephemeral per-branch twigs; the 7 global layers are the
  floor. Optional: KV-as-D3D12-texture (sub-texel = sub-pixel) once the transport is texture-native.
- **Stacked-glyph / PQ vocab** for the 262K LM head (SYNTHESIS §1.5): post-hoc PQ+ADC (no retrain, ~2 MB head,
  table-lookup scoring, feeds the swarm candidate set) → trained compositional codes (endgame, fully-resident).
- **Multimodal** (SYNTHESIS §1/§4): vision (~150M, conv-heavy = dp-onnx's strength) + audio (~300M) encoders
  exported as their own graphs, run as **DirectPort producers** into one fenced token stream → the same decoder.
- **Federation is pipeline/tensor-parallel, NOT expert fan-out** (dense). Activations cross devices every layer
  inside the AR loop — the harder latency game; the virtualized layer-routine is the federation unit.

## 8. RESILIENCE (grounded, kokoro `--drop` test)
Best-effort UDP-drop ⇒ residual stream **holds on the DATA plane** (NaN-free, graceful: kokoro rmse 2.5e-2→
1.84e-1 across p=0→0.5, free zone p≤0.1) — it's Stochastic-Depth/LayerDrop, the *same property* as the swarm's
layer-skip, and latest-wins makes a drop a gentle *stale* read not a zero. **CONTROL plane does NOT hold**
(kokoro: dropping duration/shape Adds collapsed length, 411× amplitude). For the LLM the control plane =
**KV-coherence + position-ids + the token sequence** → must be **fenced**, while inter-layer residual activations
ride best-effort. New LLM-only risk vs kokoro: **autoregressive compounding** (a flipped token cascades) → the
**verifier/rollback is the reliability layer over the lossy transport.** Bench it: `gemma4-droppath`, split
inter-layer-residual drops (predict graceful) vs KV/position drops (predict corruption).

## 9. NON-NEGOTIABLES
- **RAM is not random access** — sequential weight streaming + bounded/prefetched gathers. Don't random-walk the
  4.48 GB table per token.
- **The loop is the build**, not the one-shot graph. Decomposed = correct-fast (the proof), not the product.
- **Weights below the object layer** (handles/regions); managed arrays for activations only.
- Coordinate on `Program.cs` (parallel `spawn-fusion` copy). Bit-exact-verify every surgery. No python at runtime.

## 10. FIRST CONCRETE MOVE
Add the bf16 case (§4 Gap #1), rebuild, `dp-onnx probe e2b_step.onnx` → confirm it clears the 768 MB embed and
reports the next stop (the 4.48 GB PLE). That single step tells you exactly how much of Gap #2 you need before
first logits, and it's ~10 minutes of work. Everything after is §5.
