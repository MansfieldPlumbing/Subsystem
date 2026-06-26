# QNN Model Conversion — Exhaustive Log

**Mission:** get real models (Demucs v4 / Kokoro / Chatterbox) running on **Qualcomm Hexagon
V73 NPU** (SM8550 = "8gen2" = OnePlus 11), as deployable **QNN context binaries** (`.bin`).
A genuine first — HF "qnn" search returns essentially only local-dream's Stable Diffusion.
Endgame: understand the `.bin` format well enough (via a model we authored = the **Rosetta
Stone**) to make a **home-spun QNN model maker** — generate context binaries arbitrarily,
python-free.

Everything lives under `S:\qnn`. This file is the single source of truth.

> **REFRAME (2026-06-23, Scott): the goal is the RECIPE, not the weights.** Lifting/deswizzling int8 weights
> out of the HTP `.bin` (Strategy A — the Vega autopsy, `extract_weights`/`deswizzle`) is **backwards**: anyone
> can int8 a model and public weights are free. The scarce asset is the *method* that makes int8 **smooth** —
> the **W8A16 "sub-pixel" recipe** (int8 weights / A16 activations / int32 accumulate / per-channel
> `w_scale·a_scale` / asymmetric activation offset / calibrated ranges; read straight off the SD-UNet `.bin` we
> x-rayed) — plus the **parked-graph format** ("blob the weights, park the instructions" = const blob + serialized
> op-graph, the same shape as a TRT engine). Mario-on-NES is the mnemonic: smooth motion on 8-bit because the
> *position accumulator* carries sub-pixel bits below the display grid — i.e. compute wide (A16/int32), store
> narrow (int8), round only at the op boundary. **Pursue the recipe + the format; treat weight-extraction as
> archaeology, not the objective.** Full derivation in SESSION-COMMS 2026-06-23.

---

## TL;DR status (2026-06-15)

- ✅ **Full pipeline proven end-to-end** (ONNX → DLC → V73 HTP graph prepare).
- ✅ **Native python-free ONNX surgeon** built + verified: `S:\bin\onnx-builder\onnx-surgeon.exe`.
- ✅ **Demucs converts to DLC** and HTP-prepares through optimization. Surgery passes proven
  **bit-exact** (`verify_atom.py`: max abs diff **0.0**).
- ❌ **Demucs cannot finalize on V73**: at its architecture-locked 7.8 s segment, activation
  tensors (e.g. `[1,96,85995]` = 16.5 MB) exceed the 8 MB VTCM **as single tensors**, and HTP's
  prepare forces VTCM residency with no public DDR-spill knob. = a **handle-placement wall**.
- ✅ **WON IT with RIFE** — see "RESULT" below. First finalized QNN `.bin`, math-proven faithful.

---

## Folder map (`S:\qnn`)

```
S:\qnn\
  QNN-LOG.md                  <- this file
  reference\                  Model Conversion and Optimization Roadmap.md
  demucs\
    src\                      curated refs + all tooling:
      export_demucs_v4_pytorch_onnx.py   (orig Demucs_v4_TRT export; native STFT, opset17)
      grind_tflite.py                    (the FFT-unsupported problem, documented)
      demucs_v4_trt.cpp                  (C-ABI TRT bridge reference)
      reexport_demucs_segment.py         (re-export at smaller segment; dynamo=False)
      analyze_onnx.py                    (python ONNX inspector — superseded by onnx-surgeon)
      htp_surgery.py                     (graph surgery passes: tile_repeats_int64, squeeze_rank6_leading)
      atomize_convs.py                   (split fat convs into banal sub-convs, shared weights)
      scope_convs.py                     (list convs whose activation exceeds VTCM)
      verify_atom.py                     (onnxruntime numeric equivalence check)
      trace_node.py / check_rank.py      (forensics)
      build_v73.ps1                      (one-shot: onnxsim -> squeeze -> convert -> context-bin)
    onnx\                     (working copies)
    out\                      demucsv4*.onnx/.dlc, htp_config.json, htp_ext.json, bin\, *.log
  kokoro\  (out\, src\)       the pivot
  chatterbox\src\            exp.py (decomposed S3Gen vocoder), export.py, chatterbox_trt.cpp, ChatterboxTurboWorker.cs, config.json
  mamba\envs\                 portable conda envs (see below)
```

Big inputs kept in place (not copied): `S:\qnnscripts\demucsv4.onnx` (234 MB, the real graph;
the repo copy is a 134 B LFS pointer), kokoro onnx at `C:\dev\LocalAI\models\kokoro-en\model.onnx`.

---

## Environments (portable, on S:\, via `C:\bin\micromamba`)

**`qnn-tools`** (the workhorse — analysis + qairt-converter):
```
micromamba create -y -r S:\qnn\mamba -n qnn-tools -c conda-forge python=3.10 numpy=1.26.4 onnx onnxruntime
# then, critically:
pip install onnx==1.16.1      # QAIRT 2.42 imports `onnx.mapping`, REMOVED in onnx>=1.18 -> 1.21 nulls it
pip install pyyaml onnxsim    # converter dep + simplifier
```
- python **3.10** required by QAIRT (`bin\check-python-dependency` allows 3.8/3.10).
- **protobuf 6.33.5 is FINE** — qairt-converter imports clean with it (no downgrade needed,
  despite check-python-dependency listing 3.19.6).

**`demucs-export`** (only for torch re-exports):
```
micromamba create -y -r S:\qnn\mamba -n demucs-export -c conda-forge python=3.11
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu
pip install demucs onnx onnxsim
```
Note: torch 2.12 needs `dynamo=False` on `torch.onnx.export` (else wants `onnxscript`), and its
legacy exporter can't emit native STFT ("STFT does not support complex types").

---

## Native ONNX surgeon (the python-exit, stone #1)

`S:\bin\onnx-builder\onnx-surgeon.exe` — reads/writes raw `onnx::ModelProto` via libprotobuf,
**no python/onnxruntime/torch at runtime**. Verified standalone (python off PATH) matching the
python analyzer byte-for-byte. Today: `inspect` (op histogram, IO shapes, HTP-risky/spectral
flags). Built by `build-surgeon.ps1`: conda env's `protoc` compiles `onnx.proto` → compile
`onnx-surgeon.cpp` + `onnx.pb.cc` with MSVC18 `/std:c++17 /MD /DPROTOBUF_USE_DLLS`, link
`libprotobuf.lib abseil_dll.lib`. Runtime DLLs beside the exe: `libprotobuf.dll, abseil_dll.dll,
utf8_validity.dll, zlib.dll, zlib1.dll` (omitting utf8_validity → silent exit-before-main).

---

## The conversion pipeline (proven recipe)

Always: `& S:\qairt\2.42.0.251225\bin\envsetup.ps1` first (sets QNN_SDK_ROOT, PATH, PYTHONPATH).
Tools at `S:\qairt\2.42.0.251225\bin\x86_64-windows-msvc\`.

1. **Static-ify + fold** (kills dynamic Shape/Tile/ScatterND/Range/Mod, makes pads constant):
   `python -m onnxsim model.onnx model_sim.onnx --overwrite-input-shape "input:1,2,343980"`
2. **Surgery** (HTP-safe rewrites): `python htp_surgery.py model_sim.onnx model_final.onnx --passes squeeze_rank6_leading`
   (optionally `atomize_convs.py` for fat convs).
3. **Convert → DLC**: `qairt-converter -i model_final.onnx --output_path model.dlc --source_model_input_shape input 1,2,343980`
4. **V73 context binary**: `qnn-context-binary-generator.exe --backend QnnHtp.dll --model QnnModelDlc.dll --dlc_path model.dlc --config_file htp_config.json --binary_file model_v73 --output_dir bin`

`build_v73.ps1 -Onnx <onnx> -Length <samples> -Tag <name>` chains 1–4.

### HTP config (V73 offline prepare)
`htp_config.json` (wrapper): `{ "backend_extensions": { "shared_library_path": "QnnHtpNetRunExtensions.dll", "config_file_path": "...\\htp_ext.json" } }`
`htp_ext.json`:
```json
{ "graphs":  [ { "graph_names": ["<dlc graph name>"], "fp16_relaxed_precision": 1, "vtcm_mb": 8, "O": 2 } ],
  "devices": [ { "soc_id": 43, "dsp_arch": "v73", "cores": [ { "core_id": 0, "perf_profile": "burst", "rpc_control_latency": 100 } ] } ] }
```
- **SM8550 → dsp_arch v73, soc_id 43** (from `lib/python/qti/aisw/converters/common/backend_aware_configs/htp_v2.json` `soc_model_to_arch`).
- DLC graph name = onnx file stem; must match `graph_names` or the per-graph settings won't apply.
- `libcdsprpc.dll couldn't open` during offline prepare = BENIGN (on-device RPC lib, not needed on host).

### Authoritative HTP graph knobs (`include/QNN/HTP/QnnHtpGraph.h`)
VTCM_SIZE(_IN_MB/_IN_BYTES), FINALIZE_OPTIMIZATION_FLAG (="O" 1–3), FOLD_RELU_ACTIVATION_INTO_CONV_OFF,
SHORT_DEPTH_CONV_ON_HMX_OFF, NUM_HVX_THREADS, ENABLE_DLBC(_WEIGHTS), ENABLE_SPARSE_WEIGHTS_COMPRESSION,
**ENABLE_SLC_ALLOCATOR** (System Level Cache — untried; could host big activations), SHARE_IO_BUFFER,
ADVANCED_ACTIVATION_FUSION, HMX_BOUNDING, PARALLEL_GRAPH_EXECUTION_CONFIG. **No public
"spill activations to DDR" knob** — VTCM-vs-DDR placement is baked by the prepare.

---

## ONNX surgery passes (the "make it banal" toolkit — backend-agnostic ONNX-IL)

- **tile_repeats_int64**: cast every `Tile` repeats to int64 (converter folds float-mixed Concat
  repeats to float64 → `np.tile` dies). (Moot once onnxsim folds the Tiles away.)
- **squeeze_rank6_leading**: HTP max tensor rank = 5. Demucs' local-attention window makes a
  rank-6 chain `Reshape(→6D, leading dim 1) → Transpose → Pad → Pad → Reshape`. Leading dim is
  size 1 → squeeze it (drop from Reshape target, shift Transpose perm / Pad axes down 1; exit
  Reshape untouched, count-preserving). Run AFTER onnxsim. Clears value_info on save.
- **atomize_convs.py**: split a fat conv into banal sub-convs **sharing the weight initializer**
  (instruction reuse). **Batch-split (axis 0) is EXACT and works** (the `[512,96,336]` batch-512
  convs from demucs folding freq→batch). **Spatial-tile via Slice BACKFIRES** — the Slice must
  hold the whole padded tensor (`StridedSlice` wanted ~88 MB). Proven bit-exact via `verify_atom.py`.

**Numeric verification is mandatory** (`verify_atom.py`, ORT_DISABLE_ALL, allclose 1e-3) — graph
surgery can be silently wrong; never ship audio without it.

---

## Demucs case study (the journey + every wall)

`demucsv4.onnx`: opset 17, 4616 nodes, input `[1,2,343980]` → `[1,6,2,343980]` (6 stems stereo).
**No native STFT op** — opset-17 export already decomposed it to Cos/Sin/MatMul/Range (the
Fourier basis as matmuls). My RISKY-op heuristic overcounted (ScatterND/dynamic output were
non-issues — HTP handles ScatterND; static input infers the output).

Walls hit, in order:
1. **Convert**: `/crosstransformer/Tile` repeats folded to float64 → `tile_repeats_int64` (or onnxsim). FIXED.
2. **HTP prepare rank-6**: `squeeze_rank6_leading`. FIXED, verified 0 rank-6 tensors.
3. **HTP finalize VTCM**: a single conv needs ~44 MB TCM > 4 MB (default) / 8 MB (8gen2 max).
   `vtcm_mb:8` + `O:1/2/3` don't help (44 MB >> 8 MB).
4. **Segment can't shrink**: `model.valid_length(44100)` snaps UP to 343980 — HTDemucs is
   architecture-locked at 7.8 s. No smaller chunk without retraining-level surgery.
5. **Scope**: **53 convs > 8 MB** — batch-512 `[512,96,336]`=33 MB + long-time `[1,96,85995]`=16.5 MB.
6. **Atomization**: batch-split solved the batch-heavy ones (bit-exact). Long-time tensors
   (16.5 MB) exceed VTCM **as single tensors** → spatial tiling can't shrink the *tensor*, only
   the compute, and the Slice it needs is itself > VTCM. **Hard wall.**

Conclusion: **demucs@7.8 s is a genuinely poor fit for *pure* HTP** (long audio = big tensors
everywhere; no DDR-residency knob). Needs the ubershader (`W:\dev\native_hlsl` / MegaMath
gfx900) for the fat ops, or QNN C-API handle-placement control. demucs DLC + atomization stand
as proven assets.

---

## The protobuf 2 GB limit (Scott's flag) — real, and how we sidestep

- **Still real**: a single serialized protobuf message caps at INT_MAX ≈ **2 GB**, including
  protobuf 6.x. ONNX is protobuf, so a single-file `.onnx` with embedded weights can't exceed 2 GB.
- **Sidestep = external data**: `onnx.save(m, path, save_as_external_data=True, all_tensors_to_one_file=True)`
  writes the graph proto (tiny) + a sidecar (e.g. `.onnx_data`) holding weights. The graph then
  holds only TensorProto *references*. (Chatterbox's `conditional_decoder.onnx` + 768 MB
  `.onnx_data` is exactly this.)
- **Our tools are immune**: surgery operates on the *graph* (small). `onnx.load(path,
  load_external_data=False)` and the native surgeon parse only the graph; weights stay on disk.
  For large *outputs*, save with `save_as_external_data=True` (TODO in htp_surgery/atomize when a
  model approaches 2 GB — demucs 234 MB / kokoro 310 MB are well under, so moot for now).
- **Downstream formats are NOT protobuf**: DLC and the QNN context binary have their own
  containers (no 2 GB limit). And the native onnx-surgeon only hits the limit on a >2 GB *single
  file* — which external data avoids by construction.
- Relevant later for: fully-fused single-graph models, and big LLMs (gemma4-qnn target).

---

## QNN binary format — the Rosetta Stone plan

Scott's prior art: `C:\bin\qnn` + `S:\qnnscripts` = his QNN `.bin` reverse-engineering lab
("handles + blobs within blobs"; partial crack; `scanner9.1.ps1` + several python scripts were
the good ones). The `.bin` was for liberating gfx900 weights (`unet_out\vega_unet.safetensors`,
untested). **The half-key Qualcomm already gives us**: `qnn-context-binary-utility.exe`,
`qairt-dlc-to-json`, `qairt-dlc-info` introspect the DLC/context format. Plan: produce ONE `.bin`
from a graph **we authored** (kokoro), point those tools + the scanners at it → decode the format
→ build a native writer = arbitrary QNN model generation.

---

## Kokoro (the pivot) — the tractable target

**Use the loop-free build:** `C:\dev\LocalAI\models\Kokoro-82M-v1.0-ONNX\onnx\model.onnx`
(2463 nodes) — already mask-based (NO Loop/If/Sequence/Random). The OTHER builds
(`kokoro-en\model.onnx`, `kokoro-multi\model.onnx`) are the loop-based ones (16 risky ops,
onnxsim dies: "SequenceEmpty→Loop not topologically sorted") — AVOID.

Inputs: `input_ids[1,seq]` (int64), `style[1,256]`, `speed[1]` → `waveform[1,num_samples]`.
The math magic (loop→static) is already done in the community export, per
[adrianlyjak/kokoro-onnx-export](https://github.com/adrianlyjak/kokoro-onnx-export): the
length-regulator `repeat_interleave` loop is replaced by a **mask-based alignment**
(`frame_idx` vs `duration.cumsum()` → mask → MatMul), and `torch.rand` noise → fed-in vector.
QNN HTP does NOT support Loop/If ([ORT QNN EP docs](https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html)), so this is mandatory — and free for us here.

Status: onnxsim static (input_ids:1,256) OK → 2020 nodes, **3 risky left**: `STFT` +
`NonZero` (both in `/decoder/decoder/generator` (i)STFT — the NonZero is the iSTFT window-norm
`Greater→NonZero→Transpose`) + 1 `ScatterND` (HTP handled ScatterND fine for demucs).
qairt-converter then blocks on `/encoder/Range: Dynamic value for /encoder/Cast_1_output_0`
(shape-derived position range onnxsim left dynamic).

Remaining kokoro grind (all tractable):
1. **Fold the dynamic `Range` to static** — e.g. ORT-evaluate its output at static shape and
   replace with a Constant (reliable "fold one op" technique), or a targeted surgery pass.
2. **Decompose the (i)STFT → Conv/ConvTranspose** (the `exp.py` `brutal_decomposed_stft/istft`
   trick — uses `clamp(min=1e-11)` for overlap-add normalization instead of the `NonZero` mask).
   Kills STFT + NonZero together. Graph surgery, or re-export with the monkeypatch + the
   community loop-free export.
3. Then convert → V73 prepare. Kokoro tensors are SMALL (TTS seqs) so no VTCM wall expected.
   - ⚠ **FALSIFIED (2026-06-19): kokoro DOES hit the per-op VTCM wall — same class as demucs.** Pipeline got
     further than ever: `dp-onnx fold` (new verb) froze the dynamic `/encoder/Range` (= 421 frames for a "hello
     world" fold) → **qairt-converter SUCCEEDED** (first kokoro DLC ever, 345.8 MB) → **finalize FAILED**:
     `q::ConvLayer.opt.activations_to_vtcm` for `/decoder/decoder/generator/noise_res.1/Pow` needs **0x62ac000
     = 103 MB TCM > 8 MB VTCM** (err 1002). The fat op = the generator's harmonic/noise source (`m_source`,
     SineGen) computed at full AUDIO resolution (~25.9M elem). Length is foldable (unlike demucs's locked 7.8s),
     BUT linear extrap says only ~34 frames (~0.13s) fits 8 MB — too short to be useful, so chunk-to-fit won't
     rescue a single `.bin`. Real routes = demucs-class: **(a) Mario-hybrid** — run `m_source` off-NPU (CPU/GPU
     via DirectPort, it's just deterministic sinusoids) and feed the NPU the conv stacks (clean seam); (b)
     runtime multi-graph tiling; (c) Hexagon-MLIR. **Net: the CONVERTER wall is permanently broken (reusable
     `fold` verb); kokoro is now a PARTITIONING problem, not a conversion problem.**

## Open problems & next steps

1. **kokoro → V73 .bin** (current): static-ify, surgery the exotic ops (STFT→Conv, Loop/If/
   NonZero/Sequence/RNG/LSTM — see how many survive onnxsim static), convert, finalize.
2. **Map the .bin** once we have it (introspection tools + scanners).
3. **Demucs**: ubershader the long-sequence ops, or QNN C-API to control handle placement.
4. **Untried HTP lever**: `ENABLE_SLC_ALLOCATOR` (System Level Cache for big activations).
5. **Transferability**: the surgery/atomization passes + native surgeon are backend-agnostic
   ONNX-IL tooling — applies to TensorRT (Scott's demucs_v4_trt), DirectML (3090/Vega), TFLite.

---

## ✅ RESULT — RIFE 4.9 on Hexagon V73 (first finalized QNN model, 2026-06-15)

**`S:\qnn\rife\out\bin\rife_v73.bin` (12 MB)** — a deployable QNN context binary for a model that
had no prior QNN existence. From `C:\dev\RIFE_TRT\models\rife49_ensemble_True_scale_1_sim.onnx`.

**The validated recipe (the proven small-spatial path — generalize via `build_v73.ps1`):**
1. `python -m onnxsim rife.onnx rife_sim.onnx --overwrite-input-shape "img0:1,3,256,256" "img1:1,3,256,256" "timestep:1"`
2. `qairt-converter -i rife_sim.onnx --output_path rife.dlc --source_model_input_shape img0 1,3,256,256 ...`  → DLC (graph "rife", 3 inputs). **qairt + HTP both ate all 16 `GridSample` natively — no decomposition.**
3. `qnn-context-binary-generator --backend QnnHtp.dll --model QnnModelDlc.dll --dlc_path rife.dlc --config_file htp_config.json --binary_file rife_v73 --output_dir bin` (htp_ext: `vtcm_mb:8, O:2, fp16_relaxed_precision:1`, soc_id 43 / dsp_arch v73). Finalize log: Parallelization → **DDR spill 119 MB / fill 163 MB** → Graph Sequence → Completion. CLEAN.

**Validated (`qnn-context-binary-utility` → JSON):** backendId 6 (HTP), contextBlobVersion 3.3.4, graphName "rife", inputs img0/img1/timestep APP_WRITE FLAT_BUFFER, I/O kept **NCHW** (`preserve_io=[['layout']]`).

**MATH PROOF (`validate_rife.py`):** ran the DLC on the x86 **QnnCpu** backend vs onnxruntime on
seeded inputs → **max|diff| = 3.26e-3** (NCHW; outputs in [0.01,0.98]). That's **< 1/255 (one
8-bit pixel)** — sub-pixel faithful. Not bit-exact (qairt fusion/layout/interp rounding leaves
~3e-3) but correct for a vision model. NHWC-interp diff = 0.88 confirms output layout is NCHW.

**THE physics learned (explains every success/failure):** the VTCM wall is **PER-OP**, not total.
HTP auto-spills to DDR *between* ops (RIFE spilled 119 MB and still finalized), but a *single* op
must fit VTCM. → **small-spatial models (per-op fits) finalize on the stock qairt flow; long-audio
(one op busts, e.g. demucs's 44 MB conv) does not.**

## Model taxonomy (where each goes)
- **Small-spatial (per-op fits VTCM)** → stock qairt flow → `.bin`. PROVEN: RIFE. Next: **inswapper-128**
  (needs the Mario hybrid — fp16 + precision-critical identity path), **depth** (re-export; only a `.trt` on disk).
- **Long-audio (single op busts VTCM)** → demucs (architecture-locked at 7.8 s, internally pads any
  input to 343980 — CANNOT temporal-chunk), kokoro (chunk-tunable but 268 MB resblock convs). →
  **Hexagon-MLIR auto VTCM-tiling** or **C-API model-maker** (port `VTCMTiling.cpp`/`DoubleBufferGenericS1.cpp`). NOT ggml (Scott: "i dont want ggml i want qnn").
- **Unsupported op / precision-sensitive node** → **Mario hybrid**: slice the graph, run that op on the
  GPU via DirectPort (`BufferToTexture`→`ShaderFilter`→`TextureToBuffer`+fence), rest on NPU. The
  DirectPort fabric (`C:\dev\DirectPort-main`) IS the out-of-core op-delegate scaffolding.

## Runtime plan (deploy the .bin)
Scott's `RIFE_TRT` C# loop is ~1:1: a QNN C-ABI runner = the twin of his `demucs_v4_trt.cpp`:
`Qnn_Init(bin)` = QnnBackend_create + QnnContext_createFromBinary + QnnGraph_retrieve("rife");
`Qnn_Process(img0,img1,timestep,out)` = set tensors + QnnGraph_execute (per-frame); `Qnn_Destroy`.
Frame I/O (ffmpeg→f32 frames→memory) in C#. **V73 `.bin` executes on the Hexagon DSP → real runs
need the OnePlus (adb-push + on-device aarch64 QNN runtime); the x86 host validates via QnnCpu only.**
DirectPort = the I/O transport feeding the graph; VirtuaCam = consumer/sink.

## NEXT (resume here)
1. **Autopsy RIFE → xray the QNN `.bin` format + HTP compiler** (Scott's chosen direction — see the
   AUTOPSY section above). The Rosetta Stone is `rife_v73.bin` + `rife_v73_info.json`.
2. **Demucs round 2** armed with the autopsy (SLC allocator / forced spillFillBuffer / activation-
   slicing / Hexagon-MLIR tiling) — proof-of-work indexed in the DEMUCS section above.
3. Deploy `rife_v73.bin` on the OnePlus V73 (on-device `qnn-net-run`, aarch64-android) — "runs", not just "finalizes".
4. inswapper-128 via the Mario hybrid (need `inswapper_128.onnx`).

---

## 🔒 DEMUCS PROOF-OF-WORK INDEX (pick up here)

All on disk under **`S:\qnn\demucs\`** — nothing to regenerate. Two folders:

**`demucs\out\` — the evidence (logs + artifacts):**
- **`ctxbin_atom.log`** = THE smoking gun (latest, post-atomization finalize attempt). The exact wall:
  `q::ConvLayer.opt.activations_to_vtcm` (Op ID 1e157400000389) **requires 0x53fa000 = 88,055,808 B (~84 MiB) of TCM > 0x800000 = 8 MiB VTCM**. Failing op pre-opt: `q::QNN_StridedSlice` (ID 389) = `/tencoder.1/conv/Conv_atom13_slice1`. Err 1002, RouterX86 prepare failed 13. **Got all the way through Graph Optimizations + Post-Graph-Optimization, died in Graph Sequencing for Target** (so: converts, optimizes, won't sequence).
- `ctxbin.log/2/3/4.log` = earlier finalize attempts (pre-atomize, different ops bust). `convert*.log` / `reexport1s.log` = the DLC conversions + the 1 s re-export experiment.
- Artifacts: `demucsv4.dlc` (272 MB, graph "demucsv4"), `demucsv4_atom.dlc` (atomized), `demucsv4_*.onnx` (sim/final/htp/atom stages), `htp_config.json` + `htp_ext.json`.

**`demucs\src\` — the tooling (all reusable):** `atomize_convs.py` `scope_convs.py` `htp_surgery.py` `verify_atom.py` (bit-exact gate) `reexport_demucs_segment.py` `build_v73.ps1` `trace_node.py` `check_rank.py` + the orig TRT refs (`demucs_v4_trt.cpp`, `export_demucs_v4_pytorch_onnx.py`).

**State in one line:** demucs converts→DLC + HTP-optimizes clean, but **a single op's activation needs ~84 MiB resident vs 8 MiB VTCM** and HTP exposes no DDR-spill-within-an-op knob. Architecture-locked at 7.8 s (`valid_length` snaps to 343980), so can't shrink the tensors by chunking. This is the **per-op VTCM wall** — the exact thing the autopsy below is meant to defeat.

---

## 🗺️ CARTOGRAPHY PROOF — eyeballed a real SD1.5 UNet QNN `.bin` (2026-06-15)

Pointed `qnn-context-binary-utility --json` at `C:\bin\qnn\test-harness-s23-v73\unet.bin`
(839 MB, never seen before) → full read from a 7 KB manifest (`S:\tmp\unet_s23_info.json`):
- **It's SD1.5, unambiguously.** Inputs `sample[1,4,64,64]` (latent) + `timestamp[1]` (int32 step)
  + `text_embedding[1,77,768]` → **768 = CLIP ViT-L/14 = SD1.5** (SD2=1024, SDXL=2048). Output
  `[1,4,64,64]` (predicted noise). Single fused graph "model", ~8462 tensors.
- **Quantization fully readable: W8A16.** All activations `UFIXED_POINT_16` (uint16), SCALE_OFFSET
  asymmetric, and I can read the actual scales (sample 2.09e-4/off -32907; output 1.37e-4/off
  -32311 → real range ≈ [-4.4,+4.5], sane noise-pred). 840 MB / ~860 M params ≈ 1 B/param → int8
  weights + A16 acts (the standard HTP diffusion recipe).
- **Build: QAIRT 2.28** (`buildId v2.28.0`, contextBlobVersion **3.2.0**) — local-dream lineage,
  OLDER than our 2.42/3.3.4. **We read across SDK versions** (graphBlobInfoV2 empty in 3.2.0, V1
  present). backendId 6 (HTP), O3, dspArch 73, socModel 43.
- **🎯 THE MONEY FIELD — `spillFillBufferSize: 0`.** SD1.5 UNet finalized on the SAME 8 MB VTCM
  V73 with **ZERO spill**. Why: SD keeps high channel counts only at TINY spatial res (1280ch @
  16×16 = 0.66 MB; 320ch @ 64×64 = 2.6 MB) → no single activation busts 8 MB. **This is the
  per-op VTCM theory, confirmed from the OUTSIDE on a model we didn't build.** The clean gradient:
  **SD1.5 spill 0  <  RIFE spill 27.6 MB  <  demucs single op 84 MiB (can't fit).** Demucs loses
  because it holds 96ch × 86k-sample tensors (16.5 MB) at FULL time-resolution. Fix = make demucs
  look like SD: cut the fat conv-stem tensors into VTCM-sized tiles so every op fits → spill 0.

**Cartography grade (demonstrated, not claimed):** A on architecture / I-O / quantization (incl.
reading + range-checking the scale-offsets) / memory strategy, across SDK versions. Still D on raw
weight extraction (int8 packed in the proprietary 840 MB blob) + compiled-program disasm.

---

## 📱 DEPLOYED ON THE PHONE — RIFE runs ~~bit-exact~~ **allclose (~8e-3)** on the OnePlus V73 NPU (2026-06-15)
<!-- HEADLINE CORRECTED 2026-06-19: "bit-exact" was a transpose artifact. Device-vs-ORT = ~8e-3 (allclose, sub-3/255), NOT 0.000. Execution on V73 stands. See the CORRECTION note below. -->

- **adb-pushed** `rife_v73.bin` + aarch64 QNN runtime (`libQnnHtp` / `V73Stub` / `System` /
  `NetRunExtensions` / `Prepare`) + the **`hexagon-v73/unsigned/libQnnHtpV73Skel.so`** + `qnn-net-run`
  to `/data/local/tmp/rife` on the OnePlus 11 (serial `<redacted>`). Ran with
  `LD_LIBRARY_PATH`+`ADSP_LIBRARY_PATH`=the dir:
  `qnn-net-run --backend libQnnHtp.so --retrieve_context rife_v73.bin --input_list … --output_dir …`
  → **Creating context from binary → Executing Graphs → Finished. CLEAN.** (Unsigned skel loaded on
  the stock OnePlus — no root, no signed PD needed.)
- **CORRECT — ~~BIT-EXACT~~ ALLCLOSE ~8e-3 (corrected 2026-06-19 — see note below).** Device-HTP output
  vs host-QnnCpu reference = **max|diff| ~~0.000~~ → 8.1e-3 (multiset lower bound)** once the
  device's native **NHWC** output is transposed to NCHW (device writes NHWC, host QnnCpu writes NCHW;
  the computation is ~~identical~~ *close, ~8e-3*). The `.bin` we authored runs *faithfully (allclose)* on the real V73 Hexagon NPU.
  - ⚠ **CORRECTION (2026-06-19, recomputed from the raw output bytes, layout-independent multiset diff):**
    the "0.000" above is **not supported by the bytes.** Real numbers: ORT-vs-QnnCpu = **3.26e-3** (confirmed),
    ORT-vs-HTP(device) = **8.0e-3**, HTP-vs-QnnCpu = **8.1e-3**. Multiset is a *lower bound* on the aligned
    diff, so an 8e-3 floor means no layout bit-matches — the device run is **allclose (~8e-3, sub-3/255),
    NOT bit-exact.** The device output IS a genuine independent compute (8e-3 from both ORT and QnnCpu, so
    not a copy). EXECUTION on the V73 stands (device_execution_metadata.yaml + device_profiling.log). Use
    "allclose ~8e-3", not "bit-exact 0.000", in any handoff. (The original 0.000 was a transpose artifact.)
- **Perf (profiled, `--profiling_level basic`):** pure NPU execute = **~81 ms/frame** (QNN accelerator
  execute 81,377 µs, 4 HVX threads); `qnn-net-run` wall = **103 ms/frame** (extra ~22 ms = per-frame
  disk IO + RPC setup, **removable** by an in-memory pipelined runner); one-time context load ≈ 411 ms.
  → **~12 fps NPU ceiling at 256×256.**
- **Framerate verdict:** interpolates correctly (doubles framerate by construction). Ceiling ~12 fps
  (NPU-bound). **Pipelining (Scott's instinct, right):** async double-buffered in-memory runner (the
  `demucs_v4_trt.cpp` twin over the QNN C-API) removes the 22 ms overhead → sustained ~81 ms/frame.
  **For true 30 fps the lever is the MODEL:** it's fp16 with a heavy ~160 MB DDR spill; quantize to
  **W8A16** (like the SD UNet that finalized spill=0) → roughly halve to ~40 ms → ~25 fps. Pipeline +
  quantize = real-time territory.
- **NEXT:** pipelined QNN C-ABI runner (in-memory frames, async overlap); quantize RIFE → W8A16; real
  video demo (ffmpeg → frames → NPU → frames).

---

## 🧩 TILING PROOF + PWSH/.NET PIVOT (2026-06-15)

- **Proved on V73 HTP (host):** length-tiling a fat op is **BIT-EXACT** — `split → conv → concat`,
  `max|diff| = 0.0` — for both a 1×1 conv chain (mid tensor 33 MB) AND a dilated k3 conv over
  `[1,96,85995]`. Tiling is free correctness. (Scratch scripts `tile_proof.py`/`dconv_proof.py` →
  to be **re-implemented native**, see pivot below.)
- **BUT isolated synthetic ops DON'T reproduce the bust:** a standalone 33 MB 1×1 chain and a
  standalone `[1,96,85995]` dilated-k3 conv **both finalize fine** (HTP auto-streams them — 1×1 has
  no im2col; even the k3 streams in isolation). ⇒ **the demucs VTCM wall is CONTEXTUAL, not a
  single-op-size property** — it comes from co-resident skip-connections + the op's in/out staged
  together in the *full* graph, or a shape my surgery (rank-6 squeeze / atomize) introduced.
- **GROUND TRUTH (reproduced 2026-06-15 via `Qnn.psm1` → `Invoke-QnnFinalize` through `ss.exe`):**
  on the clean `demucsv4_final.dlc` the FIRST op to bust is **`/tencoder.0/dconv/layers.1/layers.1.0/Conv_2d`**
  (Op ID `be`), pre-opt type **`q::QNN_Conv2d`** (a real spatial conv, NOT the StridedSlice — that
  was the *atomized* graph), requiring **0x29fd000 = 42.0 MB** TCM > 8 MB VTCM, dying in **Graph
  Sequencing for Target** after 102 s. So the target class = the **time-branch (`tencoder`/`tdecoder`)
  dconv convs** over the 85995-long axis (im2col working set ~42 MB). NB my synthetic k3 conv over
  `[1,96,85995]` finalized at 16.5 MB — the real one is 42 MB (qairt Conv1d→Conv2d + dilation im2col
  inflation), so isolated cells under-estimate; trust the real-DLC number. Sequencer stops at the
  FIRST unplaceable op, so expect siblings behind it (53 convs >6 MB).
- **Tiling caveat to solve:** can't just insert a `Split` at this conv's input — that input is a
  16.5 MB MID-activation (slicing it busts, like the atomize detour). The tile must be **born at the
  tencoder input** (small) and **propagate** through the time branch, merging at the transformer
  bottleneck (tiny). That's the structural surgery the native `onnx-surgeon tile` verb must do.
- **PIVOT (Scott, emphatic): stop python.** ONNX **is protobuf**, already cracked natively
  (`S:\bin\onnx-builder\onnx-surgeon.exe` + `onnx.proto` + `gen/onnx.pb.*`). Do build / tile /
  orchestration in **PowerShell + .NET with OBJECTS + ♠**, dogfooded through **`ss.exe`**; validate
  via **.NET `Microsoft.ML.OnnxRuntime`**, not python. Closed Qualcomm tools (`qairt-converter`,
  `qnn-context-binary-generator.exe`) stay external only until the format is cracked. **Endgame:
  natively "make" qnn `.bin` files as a subsystem capability/leaf.** See auto-memory
  `prefer-pwsh-dotnet-over-python`.
- **FOUNDATION BUILT (2026-06-15, the pwsh/object way — chosen by Scott):**
  - `S:\qnn\onnxnet\Onnx.csproj` → **`Onnx.dll`** (`bin\Release\netstandard2.0\`, self-contained
    with `Google.Protobuf.dll`): the ONNX schema (proto2, package `onnx` → namespace `Onnx`) as
    .NET objects, generated from `onnx.proto` via Grpc.Tools. **ONNX is now editable as live
    objects in PowerShell** — proven through `ss.exe`: parse → enumerate nodes → construct a
    `NodeProto` (with attrs) + an int64 `TensorProto` initializer → serialize → reparse. Full
    read/construct/write cycle GREEN.
  - `S:\qnn\Qnn.psm1` — object-returning cmdlets: `Invoke-QnnFinalize` (DLC→V73, structured
    result), `New-QnnHtpConfig`, + the ONNX-object layer `Import-Onnx`/`Export-Onnx`/`New-OnnxNode`/
    `New-OnnxInt64Init`. Dogfooded via `ss -Command "ipmo …"`.
  - **tencoder.0 topology mapped (via `Import-Onnx` through `ss.exe`):** split point =
    **`/Div_3_output_0`** (`[1,2,343980]`, 2.6 MB) → `/tencoder.0/conv/Conv` (k8 stride4) →
    `[1,96,85995]` → GELU (pointwise) → dconv `layers.0` (dil1) + `layers.1` (dil2 = the 42 MB
    bust). Exit `[1,96,85995]` → tencoder.1 + a U-Net skip to tdecoder.3. **Crux to nail in the
    build = EXACT halo through the strided entry conv (k8/s4) + dilated dconv (k3 d1/d2).** Plan:
    tile all of tencoder.0 per-tile, concat at its output, finalize, see the next bust, iterate
    (symmetric for tdecoder.3). Synthetic cells CAN'T validate (they won't reproduce the 42 MB
    bust) → must test on the real graph each step.
  - **NEXT: `Add-TileBranch`** — the structural time-branch tiler over these objects (born-at-input
    `[1,2,343980]` → fork tencoder/dconv into K halo'd tiles → merge before the transformer), then
    `Import-Onnx → Add-TileBranch → Export-Onnx → qairt-converter → Invoke-QnnFinalize`, validate
    tiled-vs-untiled on QnnCpu. API gotcha: serialize via
    `[Google.Protobuf.MessageExtensions]::ToByteArray($m)` (extension, not instance method).

---

## ⛔ GRAPH-LEVEL TILING DOESN'T BEAT HTP — experiment + research (2026-06-15)

- **Built + ran the object tiler:** `Edit-OnnxConvToTiled` (in `Qnn.psm1`, over `Onnx.dll` objects,
  through `ss.exe`) rewrote the busting conv into `Pad → Slice×7 → Conv×7 → Concat` (bit-exact
  blocked convolution). Convert + finalize on V73.
- **RESULT:** the conv bust is GONE, but the bust **moved to the tile `…/Conv/t/Slice6` at the
  IDENTICAL `0x29fd000 = 42 MB`.** Same exact TCM number = same computation ⇒ **the HTP optimizer
  fuses `Pad/Slice/Conv/Concat` back into the equivalent fat conv** and re-runs
  `activations_to_vtcm`. Graph-level "our own spills" is **re-fused away.**
- **WHY (grounded in `S:\reference\hexagon-mlir` source — browsed the repo + read the cloned pass):**
  VTCM tiling is a **compiler/IR codegen transform, NOT a graph rewrite.** The `vtcm-tiling` pass
  runs on MLIR memref/linalg → emits `scf.for` tile loops + `memref.subview` tiles + `memref.copy`
  **DDR(space 0) ↔ VTCM(space 1) DMA** (proof: `test/.../vtcm_tiling_static.mlir` tiles a
  2048×8192 matmul into 64×4096 VTCM tiles). The stock qairt/HTP backend does `activations_to_vtcm`
  = **stage-whole-or-FAIL**, with NO tiled-DMA fallback for one oversized op (and `QnnHtpGraph.h`
  exposes no per-op DDR knob — verified). Hexagon-MLIR exists *precisely* to add this.
- **⇒ Single-`.bin`-via-stock-qairt is physically blocked. Two real routes:**
  - **(a) Hexagon-MLIR** — compiler-level VTCM tiling for free, but emits a **`.so` + its own
    runtime, NOT a QNN `.bin`** (Scott wants QNN). WSL2/LLVM build.
  - **(b) RUNTIME MULTI-GRAPH TILING (recommended — stays QNN + fits subsystem):** split demucs into
    small QNN graphs (each `.bin` has no op > VTCM) and let our **C# runner orchestrate the tiles** —
    call the encoder graph K times on overlapping windows, gather in C#, run the transformer once on
    the small bottleneck, tile the decoder. "Our own spills" done at the **runtime** level (we
    control it) not the graph level (HTP re-fuses). U-Net skips cross graph boundaries, held in C#.
    Meatier runner redesign, but stays QNN and IS the object-runner model.
  - **(c) Quantization** shrinks footprint but int8 im2col (≈12.4 MB) still > 8 MB for this conv at
    7.8 s → insufficient alone.

---

## 🔬 AUTOPSY → XRAY → DEMUCS-ROUND-2 (the new plan)

Goal: dissect the RIFE `.bin` we authored (the **Rosetta Stone**), map its structure onto the QNN
context-binary format + Qualcomm's HTP compiler behavior, and use that to crack the per-op VTCM
wall that stops demucs. Order:

1. **Autopsy RIFE** — we already have the metadata layer decoded: `rife\out\rife_v73_info.json`
   (`qnn-context-binary-utility --json`). Key fields it exposes: `graphBlobInfo`
   {`spillFillBufferSize: 27,656,192` (~27.6 MB — the DDR spill region HTP baked in!),
   `optimizationLevel: 2`, `vtcmSize: 8`}, `graphBlobInfoV2` {`constSize: 11.2 MB`,
   `opDataSize: 3.06 MB`, `ioTensorSize: 2.37 MB`, `ddrTensorSize: 0`, `nativeKChannelSize: 256`,
   `nativeVChannelSize: 64`}, `dspArch: 73`, `socModel: 43`, `contextBlobVersion: 3.3.4`.
   → **`spillFillBufferSize` is the lever**: RIFE finalized *with a 27.6 MB DDR spill buffer*. The
   question for demucs = can we force a spillFillBufferSize big enough to host the 84 MiB op, or
   does HTP refuse because it's a *single-op* working set (not cross-op spill)?
2. **Xray below the JSON** — the JSON is the *system-context info descriptor*; the real 12 MB blob
   (weights + the serialized HTP op-graph) is the opaque proprietary part. Point Scott's prior-art
   scanners (`C:\bin\qnn` + `S:\qnnscripts` — `scanner9.1.ps1` et al, the "handles + blobs within
   blobs" crack) + `qairt-dlc-to-json`/`qairt-dlc-info` at `rife_v73.bin` (a graph WE authored, so
   every byte is attributable) → decode the container → toward a native python-free `.bin` writer.
3. **Map the compiler** — diff RIFE's CLEAN finalize log (Parallelization → DDR spill 119 MB/fill
   163 MB → Completion) against demucs's `ctxbin_atom.log` (dies in Graph Sequencing). Isolate
   *why* HTP spills RIFE's activations to DDR between ops but refuses to for demucs's one fat op.
   That's the compiler's placement policy — the thing to defeat.
4. **Demucs round 2**, armed with (1)–(3). Candidate levers, in order of cheapness:
   `ENABLE_SLC_ALLOCATOR` (System Level Cache — UNTRIED, could host the big activation);
   force a large `spillFillBufferSize`; finer atomization that keeps each slice's working set
   < 8 MiB (the current `atom13_slice1` slice itself is 84 MiB — slice the *activation*, not just
   the conv); else **Hexagon-MLIR auto VTCM-tiling** / **C-API model-maker** (port
   `VTCMTiling.cpp`/`DoubleBufferGenericS1.cpp`). NOT ggml.

**First concrete move next session:** `qnn-context-binary-utility` deep-dump RIFE + run the
`S:\qnnscripts` scanners on `rife_v73.bin`; in parallel re-read `ctxbin_atom.log` against RIFE's
`ctxbin.log` to pin the spill-vs-refuse boundary.

---

## Key gotchas (bite-marks)

- onnx must be **1.16.1** for qairt-converter (1.18+ removed `onnx.mapping`).
- onnxsim **bakes value_info** → `graph.ClearField('value_info')` before re-inferring true shapes
  (else stale shapes mislead you / break ORT load). htp_surgery clears it on save now.
- DLC graph name = onnx stem; keep `htp_ext.json` `graph_names` in sync.
- `model.valid_length` may snap up to a fixed architecture segment (demucs 343980).
- torch.onnx.export needs `dynamo=False` on torch 2.12+.
- inline python with `/Foo/Bar`-looking strings + `del` can trip the shell's path-deletion guard
  → put such scripts in files.
