# Phase-1 state — Gemma 4 E2B on dp-onnx (offramp, 2026-06-23)

The handoff doc. If context ran out, resume here. AUTHORITY: the binary + measured runs (below).

## PROVEN this session (receipts, not claims)
- **dp-onnx is a live, verified inference engine.** `dp-onnx selftest` → PASS (max|diff| 0.0).
  `dp-onnx run model_all.onnx` (kokoro) → **all 2463 nodes, 6.9s wall**, wrote `dp-onnx-live.wav`
  (39000 samples / 1.62s audio), rmse vs oracle **2.504E-2 = the known atan2-phase floor** (matches the
  parity bench exactly — perceptually clean, "sounds lovely", NOT a regression).
  Exe: `S:\qnn-project\workspace\onnx-interp\publish-aot\dp-onnx.exe`. CLI: `selftest|probe|run|addoutput|emit`.
- **Op coverage ≈ there for a DECOMPOSED Gemma decoder.** dp-onnx implements ~70 op-types (Program.cs:827+).
  E2B needs: MatMul/Gemm ✓ · GeGLU=Gelu×Mul ✓ · RMSNorm=Pow+ReduceMean+Sqrt+Div+Mul ✓ · RoPE=Sin+Cos+Slice+Neg+Concat ✓ ·
  Softmax ✓ · PLE=Gather+gating ✓ · softcap=Tanh×Mul ✓. **No new handlers for a decomposed export.**
  Gap = FUSED ops only (GroupQueryAttention/RotaryEmbedding/RMSNormalization/MatMulNBits) IF the exporter fuses.
- **Swarm/federation substrate tested green:** `test.vom.fence-waitn.ps1`, `test.vom.slot-rollback.ps1`,
  `test.vom.no-gc.ps1`. V73 NPU finalize proven (RIFE). Models on disk: E2B/E4B/12B `.litertlm` + 12B `.gguf`.
- **Arch grounded** from `S:\bin\llama.cpp\src\models\gemma4.cpp` (see `deliverables\claude-memory\gemma4-architecture.md`):
  E2B 35L / E4B 42L are **DENSE+PLE** (MoE is the 26B-A4B). Tied embeddings. **Built-in MTP draft head** (`h_nextn`,
  `*-MTP.gguf`) — shipped speculative decoding. Cross-layer KV sharing (last N layers compute no K/V).

## CORRECTIONS accepted from the sibling (Antigravity/qnn) session — carry these, they fix overstatements
1. **"ops run" ≠ "the LLM runs."** kokoro is one-shot; an LLM is the LOOP: single-step graph (past_kv→present_kv)
   + host-driven prefill→decode→**sampler→tokenizer→detokenize**. dp-onnx `run` is one-shot — **no KV loop, no
   sampler, no tokenizer.** "95% ops in" = ONE token runs. The agent is the loop, missing fused-or-decomposed.
   **The loop is the build.**
2. **Decomposed defers cost, doesn't delete it.** Decomposed decode = many small ops × layers × every token; fp16
   E2B ≈ 10 GB. The W8A16 "sub-pixel" recipe IS a `MatMulNBits`-shaped handler → fast+small eventually wants that
   one quantized fused matmul. Decomposed = correct-fast (the PROOF); not the product.
3. **Dense ⇒ pipeline/tensor-parallel federation, not expert fan-out.** No MoE = no embarrassingly-parallel expert
   routing. E2B/E4B federate by MatFormer-slice / layer parallelism — activations cross devices EVERY layer inside
   the AR loop (harder per-token latency than kokoro's independent-chunk fan-out). Still DirectPort; honest about kind.
   Good news: the loop is exactly where VOM/DirectPort earn it — KV as handles, speculative = slot-rollback.

## BLOCKER (why the swing didn't run here) — weight-acquisition, NOT dp-onnx
demucs-export env has torch 2.12 + onnx 1.22, but **transformers MISSING, no HF token, no cached gemma safetensors**,
and Gemma-4 is gated. On-disk models are litertlm/gguf, not PyTorch. To run `export_gemma4_e2b_decomposed.py`:
1. `…\demucs-export\python.exe -m pip install transformers`
2. `huggingface-cli login` + accept the Gemma-4 license (gated, ~10 GB E2B). 59 GB free on S: — fine.

## NEXT (in order) — the actual phase-1 path
1. Unblock (above) → run `export_gemma4_e2b_decomposed.py` → `e2b_step.onnx` (decomposed, external-data).
2. `dp-onnx probe e2b_step.onnx` → confirm op histogram is all-covered; implement any fused gaps (or re-export eager).
3. `dp-onnx run` one step → **diff logits vs the llama.cpp E2B oracle** (rmse ≤ 1e-3). = "Gemma's math runs on our engine."
4. **THE BUILD (the loop):** host-side decode loop in Rb's turn-contract — prefill → per-token step, **KV-cache as VOM
   handles**, sampler, tokenizer/detokenizer. This is the LLM, and the deliverable after logits match.
5. Then: static C=256 chunk → V73 finalize for the NPU-fast-track layers (SYNTHESIS §1.5); add the W8A16/`MatMulNBits`
   quantized fused matmul for fast+small; MTP head + elastic swarm for speculative decode (SYNTHESIS §5a).

## MILESTONE (2026-06-23, late) — E2B EXPORTED, op-coverage CONFIRMED, 2 engine gaps to run
- **`export_gemma4_e2b_decomposed.py` works.** E2B (gated, ~10 GB bf16) downloaded to `W:\hf-cache`, decomposed
  (`dynamo=False`, eager attn, opset 17) → `W:\gemma4\onnx\e2b_step.onnx` (no-cache 8-tok prefill, **10,164 nodes**,
  external-data sidecars on W: incl. `embed_tokens_per_layer.weight` 4.48 GB = PLE, `embed_tokens.weight` 768 MB).
  Export needed an `aten::diff` ONNX symbolic (Gemma masking calls `.diff()`; opset-17 has no built-in) →
  registered `Slice+Sub`. Trace re-confirmed arch: tied lm_head, softcap=30, MQA kv_heads=1, sandwich RMSNorm.
- **`dp-onnx probe e2b_step.onnx` → `implemented op-types=34/34, MISSING: none`.** Every op in the real E2B graph
  is dp-onnx-native. External-data weights load. The "decomposed Gemma runs on our engine" claim is now a receipt.
- **TWO engine gaps before it RUNS** (both in `onnx-interp\Program.cs` `FromProto`, ~line 1857; coordinate with the
  dp-onnx owner — there's a parallel copy in `spawn-fusion\onnx-interp`, do NOT double-edit):
  1. **bf16 (dtype 16) + fp16 (dtype 10) initializer load.** Add to the `FromProto` switch:
     `case 16: { var raw=t.RawData.Span; var f=new float[n]; for(int k=0;k<n;k++) f[k]=BitConverter.Int32BitsToSingle((int)((uint)(ushort)(raw[2*k]|(raw[2*k+1]<<8))<<16)); return Tensor.F(f,dims); }`
     (`case 10` likewise via `BitConverter.UInt16BitsToHalf`). bf16 = top 16 bits of fp32.
  2. **Lazy PLE load (the real one).** `embed_tokens_per_layer.weight` = **2.35 B elements > int.MaxValue (2.15 B)
     and > 2 GB Span/ByteString** → cannot be one `float[]`. Must load external-data lazily and **Gather rows without
     materializing** (upcast bf16→fp32 per-row at gather). This IS the PLE offload (SYNTHESIS §3) — now mandatory,
     not optional. The thesis was right: the table is un-holdable; gather-by-handle is the only way.
- **NEXT after those land:** `dp-onnx run e2b_step.onnx --inputs <dir>` (write `input_ids.bin`) → logits → diff vs
  llama.cpp E2B oracle (rmse ≤ 1e-3). THEN the host decode loop (the actual LLM).

## Open offer
File a hive CRQ ("Gemma-4 E2B GraphRuntime track: decomposed export blocked on HF auth; dp-onnx op-coverage proven;
deliverable = host decode loop + KV-as-VOM-handles") so the cross-session truth is in the hive, not just here.
