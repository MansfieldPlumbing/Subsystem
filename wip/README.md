# wip/ — the workshop (clone-and-rebuild sovereignty)

Parked scattered projects so **the whole system lives in one repo**: a fresh `git clone` plus the
re-derivable data is enough to rebuild everything. These are **not yet integrated** into the `ss` build;
each is staged here until it is folded into `src/` (the dp-onnx pattern: vendor → mount → absorb) or, once
its capability already lives in `src/`, retired. **Only source is vendored;** heavy re-derivable artifacts
(models, runtimes, build outputs) are gitignored. Markdown is tracked **here** (the workshop) — never as
`docs/` (the binary is the docs; `/docs/` is gitignored on purpose). Each item's status is its **graduation
verdict**; `ss test <name>` is the receipt where one exists, and each blocker is logged to its CRQ.

## Graduated to `src/` (proven by a receipt)

- **SentencePiece tokenizer** — lifted from `gemma-talking-layer` into `src/native/dp-onnx/onnxnet/SentencePiece.cs`
  (the sovereign `.spm` reader + Unigram tokenizer, sibling of `OnnxProto.cs`). Receipt: `ss test sentencepiece`
  (gemma-4 vocab 262144, `<bos>`=2, encode→detokenize bit-exact, no sentencepiece/protobuf lib).
- **VOM GPU Compiler (`vom-gpu-compiler/`)** — Graduated to `src/tools/VomGpuCompiler/` to serve as the compiler for the native `sssd.exe` daemon. Receipt: `ss test gpu-pe-factory`. CRQ145.

## Retired from wip (capability already in `src/`)

- **gemma4 exporter** → `src/native/dp-onnx/tools/gemma4-export/export_gemma4_e2b_decomposed.py`. A build-time
  re-export recipe ("no python at runtime"); the Gemma-4 read/op-mapping it fed already shipped via the LiteRt
  path in `src/native/dp-onnx`. Kept in the tracked source tree so a clone can re-derive the gated graph.

## Parked (proven/partial/recon, still blocked from `src/`)

- **`directport/`** — the full upstream DirectPort C++ SDK + examples. The **load-bearing core already
  graduated**: `src/native/directport/{directport.h,directportd3d12.cpp}`, `Device/DirectPortVk.cs`,
  `windows/DirectPort{Native,Producer,Bench}.cs`. Kept as the reference. CRQ117.
- **`gemma-talking-layer/`** — a bit-exact gemma tokenizer (now graduated, above) + a C#-driven decode loop.
  **Blocked:** the decode loop is wired to a **mock VOM + a stub engine** (fabricated logits); graduates after
  rewiring to the real `DpOnnx.Interp` + real `Subsystem.Vom` behind the `Runtime` contract. CRQ159.
- **`d3d12-kernels/`** — ~55 SPIRV-Cross HLSL compute kernels + the `DieWorker` ggml dispatcher. **Blocked:**
  `DieWorker` is stubbed and bound to `C:/BUILD/llama.cpp`; several dequant kernels carry `???` placeholders
  (won't compile). The src GPU seam (`GpuD3D12.cs` + `gemm.hlsl`) is the live driver. CRQ145/off-ramp.
- **`qnn/`** — QNN/Hexagon-HTP research; `qnn/openformat/` is the open-format recon (`FINDINGS.md` + `dissect.py`
  + `grammar.py`). **Blocked:** the sovereign `.bin` emitter is unbuilt and the HMX weight swizzle is unsolved;
  the only working `.bin` path shells out to the **closed** Qualcomm generator. CRQ158.
- **`dp-onnx-research/`** — the demucs TensorRT case study (external-repo recon). Out of doctrine for `src/`
  (subsystem's dp-onnx is ORT- **and** TRT-free); kept as a tracked case study.
- **`agent-browser/`** — a standalone Windows WebView2 MCP browse agent. **Blocked:** not vendored into the
  build as buildable source, and `Rd.Consult` (`RdCmdlets.cs`) is still a stub. The Android `WebDrive`
  (`src/runspace/Device/WebDrive.cs`) is the on-device sibling, not this host. CRQ120.
