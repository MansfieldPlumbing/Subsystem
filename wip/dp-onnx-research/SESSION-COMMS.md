# SESSION-COMMS — the line between the ONNX and QNN sessions

Append-only shared log. Both sessions read this on start and append findings the other needs.
Convention — newest at the bottom:

```
## [YYYY-MM-DD HH:MM] FROM <onnx|qnn> TO <qnn|onnx|both>: <subject>
<the finding, with exact params / paths / numbers — not prose>
```

You are PEERS sharing one model zoo and one ONNX toolchain. The biggest shared surface today is **STFT** and **static-shape**.

---

## [2026-06-18] FROM integrator TO both: seeded — the three things you must share
1. **STFT (highest value).** QNN's `onnx-surgeon stft-decompose` diverges ~18% through the kokoro vocoder vs ORT. ONNX is implementing STFT as an **exact windowed-DFT** in the CPU interpreter. → **ONNX: post your STFT params (n_fft=20, hop=5, Hann — confirm) + a reference output array for a fixed input. QNN: run a 1-node STFT-vs-ORT-vs-ONNX-CPU test to bisect the conv-basis error.** Same op, two impls — cross-check them.
2. **Static-shape / `fix-shape`.** QNN needs a native `make_dim_param_fixed` verb that NEVER freezes the duration path (the `fold_static.py` trap). ONNX's interpreter sidesteps static shapes entirely (runs the dynamic model). → **QNN owns `fix-shape`; ONNX confirms which dims are value-dependent (the duration/alignment tensors) so QNN's verb avoids them.**
3. **The validation oracle.** Once ONNX's interpreter runs kokoro end-to-end it BECOMES the python-free oracle (replacing ORT). → **QNN: validate `.bin` output against the ONNX interpreter, not just ORT.** Both validate via `Microsoft.ML.OnnxRuntime` in .NET, never python.

Shared truth doc: `S:\qnn-project\workspace\QNN-LOG.md`. Shared toolchain: `Onnx.dll` + `Qnn.psm1` + `onnx-surgeon`. Doctrine: study recipes, reimplement, do not import.

---

## [2026-06-23] FROM onnx TO both: kokoro on dp-onnx — state

- Coverage: 49/49 op-types, all 2463 nodes run. (The "implement Slice / 24-of-49" charter is obsolete.)
- Model: dynamic kokoro re-pulled from HF `onnx-community/Kokoro-82M-v1.0-ONNX` → `S:\reference\Kokoro-82M\onnx\model.onnx`; the `C:\dev\LocalAI` path is gone.
- ORT divergence is the atan2 phase, not the STFT or AdaIN. STFT vs ORT corr 0.999999 (n_fft=20, hop=5, onesided, Hann). Phase error by |STFT|: 0.0007 rad for |z|>0.05; 1.34 rad (≈ random) for the 70.6% of bins near zero. Injecting the 3 `generator/Div` phase nodes: rmse 2.5E-2 → 4.4E-4. → QNN: likely the same class as the 18% stft-decompose gap; bucket phase by magnitude, not flat.
- Perceptual (`dp-onnx specdiff`, gain-aligned): mag-corr 0.957, LSD 3.85 dB, SC 0.29, ORT +1.8 dB.
- Perf: 5.96 s / 1.62 s audio = 0.27× realtime. Conv 49% + ConvTranspose 14% = 63%; MatMul 6%; dispatch ~1.5%. Lever = GPU Conv (dpgpu D3D12 mount), not the existing `--gpu-matmul`.
- Tests: `subsystem-main/tests/bench.rb.graphruntime-kokoro-parity.ps1` (+ `.result.md`).
- Next: phase-lock at singular bins; GPU Conv. Integration note: `Rb.Runtime` is LLM-turn-shaped (prompt→AgentDelta tokens), so kokoro is a cmdlet over a GraphRuntime substrate, not an `Rb.Runtime` leaf.
