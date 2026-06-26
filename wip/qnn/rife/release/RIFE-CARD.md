# RIFE 4.9 → Hexagon V73 QNN context binary — model card

**`rife_v73.bin` (12 MB)** — RIFE 4.9 frame interpolation as a deployable Qualcomm **QNN context
binary** for the Hexagon **V73** NPU (SM8550 / Snapdragon 8 Gen 2 / OnePlus 11). A model with no
prior QNN existence — built, validated, and **run bit-exact on the phone**. This bundle is the
model plus everything that proves it.

## What it does
Given two frames `img0`, `img1` and a `timestep` t∈[0,1], produces the interpolated middle frame —
the core of 2× framerate / motion smoothing.

| Tensor | Role | Shape (NCHW) | dtype |
|---|---|---|---|
| `img0` | first frame | `[1,3,256,256]` | float32 |
| `img1` | second frame | `[1,3,256,256]` | float32 |
| `timestep` | interpolation t | `[1]` | float32 |
| `output` | middle frame | `[1,3,256,256]` | float32 |

Fixed resolution **256×256**. Graph name `rife`. Backend HTP (backendId 6), contextBlobVersion 3.3.4.

## How it was built (the proven recipe)
1. `onnxsim` static-ify: `img0:1,3,256,256 img1:1,3,256,256 timestep:1` (from
   `rife49_ensemble_True_scale_1_sim.onnx`).
2. `qairt-converter` → `rife.dlc` (graph `rife`). **qairt + HTP ate all 16 `GridSample` natively** —
   no decomposition needed.
3. `qnn-context-binary-generator` for V73 with `recipe/htp_ext.json` (`vtcm_mb:8, O:2,
   fp16_relaxed_precision:1, soc_id 43, dsp_arch v73`). Finalize: Parallelization → DDR spill
   119 MB / fill 163 MB → Completion. CLEAN. (`logs/host_convert.log`, `logs/host_ctxbin.log`.)

## Validation — bit-exact
- **Host (`recipe/validate_rife.py`):** ran the DLC on the x86 **QnnCpu** backend vs onnxruntime →
  `max|diff| = 3.26e-3` (sub-pixel, < 1/255). Conversion is faithful.
- **On device (the real proof):** `qnn-net-run --retrieve_context rife_v73.bin` on the OnePlus V73 →
  device output vs the host QnnCpu reference = **`max|diff| = 0.000`** once the device's native
  **NHWC** output is transposed to NCHW. The NPU computes it *exactly*. (`sample_io/dev_output.raw`
  vs `sample_io/ref_out.npy`.)

## Performance (profiled on V73 — `logs/device_profiling.log`)
- **Pure NPU execute: ~81 ms/frame** (QNN accelerator 81,377 µs, 4 HVX threads).
- `qnn-net-run` wall: ~103 ms/frame (the +22 ms is its per-frame disk IO — gone in an in-memory runner).
- Context load (one-time): ~411 ms.
- **Ceiling ≈ 12 fps at 256×256** (NPU-bound). The lever below 81 ms is W8A16 quantization, not the
  pipeline.

## Run it on device (reproduce)
Push the runtime + this bundle to `/data/local/tmp/rife`, then:
```
export LD_LIBRARY_PATH=/data/local/tmp/rife
export ADSP_LIBRARY_PATH=/data/local/tmp/rife        # the unsigned hexagon-v73 Skel lives here
./qnn-net-run --backend libQnnHtp.so --retrieve_context rife_v73.bin \
              --input_list sample_io/input_list_dev.txt --output_dir output
```
Needs (aarch64-android, from QAIRT): `libQnnHtp.so`, `libQnnHtpV73Stub.so`, `libQnnSystem.so`,
`libQnnHtpNetRunExtensions.so`, and `hexagon-v73/unsigned/libQnnHtpV73Skel.so`. Stock OnePlus, no root.

## Bundle contents
```
rife_v73.bin                         the model
rife_v73_info.json                   manifest (qnn-context-binary-utility --json) = the format Rosetta Stone
recipe/htp_ext.json                  the exact HTP V73 config used
recipe/validate_rife.py              the host QnnCpu-vs-onnx validator
logs/host_convert.log                qairt-converter (onnx -> DLC)
logs/host_ctxbin.log                 context-binary-generator (DLC -> V73 .bin)
logs/device_execution_metadata.yaml  on-device run record (graph, I/O, inferences_completed)
logs/device_profiling.log            on-device --profiling_level basic (the 81 ms number)
sample_io/img0.raw,img1.raw,timestep.raw   seeded inputs
sample_io/input_list_dev.txt         on-device input list
sample_io/ref_out.npy                host QnnCpu reference output
sample_io/dev_output.raw             on-device NPU output (NHWC) — bit-exact to ref after transpose
```
Full project log: `S:\qnn\QNN-LOG.md`.
