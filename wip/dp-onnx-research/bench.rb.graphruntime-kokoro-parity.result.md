# bench.rb.graphruntime-kokoro-parity — result

**Run:** 2026-06-23 · `dp-onnx` rebuilt today from `Program.cs` (`S:\bin\dotnet` 11.0.100-preview.5) ·
model = HF `onnx-community/Kokoro-82M-v1.0-ONNX` `onnx/model.onnx` (dynamic fp32, 310 MB) →
`S:\reference\Kokoro-82M\onnx\model.onnx` · fixtures = `onnx-interp\io` (+ `io\all` per-node ORT dump).

## Receipt
| field | value |
|---|---|
| op-types | **49/49** implemented |
| nodes | **RAN ALL 2463** (≈6 s) |
| waveform rmse vs ORT | 2.504E-2 (strict MISMATCH; gate ≤1e-3) |
| inject ceiling (fix phase) | **4.418E-4** (≈ transparent) |
| perceptual (`specdiff`, gain-aligned) | mag-corr 0.957 · log-spec-dist 3.85 dB · SC 0.29 · ORT +1.8 dB |
| perf | 5.96 s / 1.62 s audio = **0.27× realtime** |
| verdict | coverage DONE; quality + perf OPEN, both root-caused |

## What this overturns
The 2026-06-18 charter ("24/49 ops, stops at `Slice`, implement Slice") is **stale** — every op-type is
implemented and the full graph runs. The frontier is parity + speed, not coverage.

## Root cause — the phase singularity (proven by `--inject` elimination)
Chain: `STFT → Transpose_3 → Gather(real/imag) → Div(=imag/real) → Atan → Where(quadrant)` = an atan2 phase.
- inject STFT **input** (`Reshape_3`): NOT fixed → bug is at/after STFT.
- our STFT **output** vs ORT: corr **0.999999**, ratio 1.0000 → the windowed-DFT kernel is exact (n_fft=20, hop=5, onesided, Hann).
- inject Gather-inputs / `Div` / `noise_res` / `istft`: ALL collapse to the identical **4.418E-4** floor → one upstream cause.
- phase error bucketed by |STFT|: high-mag (24.5%) **0.0007 rad** · mid (4.9%) 0.0103 rad · **low-mag (70.6%) 1.34 rad ≈ random.**

∴ STFT exact **and** atan2 correct, but the *composition* is ill-conditioned where 70.6% of bins are ~zero-magnitude.
ORT's low-mag phase is its own float-FFT noise — not cheaply bit-matchable. Fix = resolve the singular phase
deterministically (magnitude-gate / phase-lock); validate by ear + `specdiff`, not strict waveform rmse.

## Perf profile (precise; corrects "CPU bound")
Conv **49.4%** · ConvTranspose **14.4%** (= **63% convolution**) · Pow 9.3% · Sin 6.6% · MatMul **6.2%** · LSTM 5.1% · rest <2%.
Dispatch/interpretation overhead ≈ **1.5%** (compile-for-speed is moot). Lever = **GPU Conv** (the dpgpu D3D12 mount),
not GPU MatMul (the existing `--gpu-matmul` targets the wrong 6%).

## Reproduce
```
ss run tests/bench.rb.graphruntime-kokoro-parity.ps1
dp-onnx run <model> --inputs io --compare io\all                          # divergence map
dp-onnx run <model> --inputs io --compare io\all --inject generator/Div   # causal test (→ 4.418E-4)
dp-onnx run <model> --inputs io --prof                                    # per-op wall-time profile
dp-onnx specdiff _dp_timing.wav _oracle.wav                              # perceptual (magnitude-spectrogram)
```
