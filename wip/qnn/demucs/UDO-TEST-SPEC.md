# Pass-through UDO test — does HTP leave a custom op out of the VTCM cram?

**Galaxy-brain hypothesis (Scott):** wrap the busting op as a **custom op (UDO / op-package)** so HTP
treats it as an opaque black box — it **won't fuse across it** and **won't apply
`activations_to_vtcm`** to it (that pass only fires on native ops). Then the rest of the graph still
fuses into one optimized context binary. "Treat it like a split, still fuse it." The UDO becomes the
single injection point for our own tiling / DDR placement / GPU delegate.

This spec is the **cheapest test that proves the premise** before we write the real tiled UDO.

## ⛔ GATE — Hexagon SDK (not currently installed)
An HTP op-package is a **DSP-side `.so`** built with `hexagon-clang++`. The QAIRT HTP Makefile
(`examples/QNN/OpPackage/HTP/Makefile`) requires:
- **`HEXAGON_SDK_ROOT`** → for V73: **`hexagon-sdk-5.5.5`** + **`HEXAGON_Tools 8.7.06`**
  (`.../tools/HEXAGON_Tools/8.7.06/Tools/bin/hexagon-clang++`).
- `ANDROID_NDK_ROOT` → for the `aarch64-android` host-side stub (we have an NDK from the Android head).
- `QNN_SDK_ROOT` = `S:\qairt\2.42.0.251225` (have it — provides the BE headers).

Checked `C:\Program Files (x86)\Qualcomm`, `C:\bin\qnn`, the QAIRT trees → **no `hexagon-clang++`.**
Only the **Qualcomm Software Center (QPM)** is present, which is the installer. **Install
`hexagon-sdk-5.5.5` via QPM (multi-GB, Qualcomm account) — that's step 0.** Everything below is gated on it.

## The two-step test (smallest → decisive)

### Step A — toolchain smoke test (prove we can build+register+run ANY UDO on V73)
1. Pick the simplest example op: `examples/QNN/OpPackage/HTP/ExampleOpPackageRelu.cpp` (same-shape unary).
2. `make htp_v73 htp_aarch64` with the env above → `libQnnHtpOpPackageExample.so` for **hexagon-v73**
   (DSP) + **aarch64-android** (host stub).
3. Build a tiny ONNX (our `Onnx.dll` pwsh tooling) that uses that custom Relu, convert → DLC, finalize
   with `qnn-context-binary-generator --op_packages <so>:<interface>` , push + run on the OnePlus.
   - **Pass = the op-package path works end-to-end on our device.** (De-risks the toolchain before the
     real op.)

### Step B — the cram test (the actual question)
1. Define a custom op **`PassConv`** with the busting conv's exact signature:
   in `[1,48,85995]`, weight `[6,48,3]`, out `[1,6,85995]` (k3, dil2, pad2). Config via
   `qnn-op-package-generator` (XML/JSON op-def → scaffolds the package).
2. Implement it **minimally** — even a DDR-naive loop. **Correctness is NOT required for this test**,
   only the right *output shape* and that it *finalizes*. (The real tiled/HVX impl comes after.)
3. Build the v73 + aarch64 `.so`.
4. **Surgery** (our pwsh `Onnx.dll` tooling): in `demucsv4_final.onnx`, replace the node
   `/tencoder.0/dconv/layers.1/layers.1.0/Conv` with a `PassConv` custom-op node (our package
   namespace). Convert → DLC.
5. Finalize with the op-package registered → `Invoke-QnnFinalize` (capture TCM + bust op).

### Decision criterion
- **Finalizes / no 42 MB at PassConv** → HTP leaves the UDO out of the cram → **hypothesis TRUE.**
  Go build the real UDO (our VTCM tiling à la Hexagon-MLIR, or the DirectPort GPU delegate inside it).
- **Still ~42 MB / busts on PassConv's input** → HTP crams the UDO's I/O too → the UDO boundary does
  NOT escape the wall → fall back to the multi-graph / region-split path.

## Why this is the right gamble
If Step B passes, *every* later idea collapses into "the busting op is ours, behind a UDO, in one
fused bin" — the atomize-not-tile fence, our own VTCM tiling, the zero-copy GPU hot-potato, even the
~4 KB footer stash to carry the op's metadata. One boundary, HTP keeps its fusion everywhere else.

## Reference paths
- HTP op-package example + Makefile: `S:\qairt\2.42.0.251225\examples\QNN\OpPackage\HTP\`
  (`ExampleOpPackageRelu.cpp`, `ExampleOpPackageInterface.cpp`, `Makefile`, `README`).
- Conv2D UDO (closer to PassConv): `examples\SNPE\NativeCpp\UdoExample\Conv2D\src\HTP`.
- Generator: `qnn-op-package-generator` (QAIRT bin). Register at finalize: `--op_packages`.
- Our surgery + finalize tooling: `S:\qnn\Qnn.psm1` (`Import-Onnx`/`New-OnnxNode`/`Invoke-QnnFinalize`),
  `S:\qnn\onnxnet\Onnx.dll`.
