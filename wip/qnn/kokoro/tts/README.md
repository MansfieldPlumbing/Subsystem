# kokoro-tts — python-free Kokoro TTS (.NET / ONNX Runtime)

Speaks. End-to-end, no python in the runtime path:

```
IPA phonemes --vocab--> int64 ids ┐
voice .pt   --row[n]--> style[256] ┼--> ONNX (input_ids, style, speed) --ORT--> waveform --> 24kHz WAV
speed                 --> float[1] ┘
```

Built from the TRT-repo runner pattern — `chatterbox_trt.cpp` is the 1:1 native twin
(`Init`/`Process`/`Destroy`, int64 tokens → float waveform). Backend here is ORT/CPU;
a TRT C-ABI bridge or a QNN `.bin` runner slots behind the same seam.

## Usage
```
kokoro-tts --model <kokoro.onnx> --phonemes-file <ipa.txt> [--voice af_heart] [--speed 1.0] [--out out.wav]
           [--validate <other.onnx>]    # runs a 2nd graph, reports max|diff|/rmse (decomposition gate)
```
- vocab is read live from `Kokoro-82M/config.json` (178 IPA tokens) — no embedded copy.
- voices are the `.pt` packs (torch-zip; `data/0` = raw float32 `[510,256]`, row-indexed by phoneme count).
- the runner auto-detects a static vs dynamic `input_ids` axis.

## ⚠️ Which model to feed (hard-won, 2026-06-18)
The pipeline is correct; the **graph artifact** is what bites.

| graph | shape | result | use for |
|---|---|---|---|
| `…Kokoro-82M-v1.0-ONNX/onnx/model.onnx` (dynamic, **mask-based**) | `[1,seq]` | ✅ 1.6s clean "hello world" | **playback** |
| `kokoro_lf_sim.onnx` (onnxsim → static `[1,256]`) | static | ⚠️ 10.5s, vocodes padding (mask was baked away) | — |
| `kokoro_folded_sim.onnx` (+ `fold_static.py`) | static | ❌ 218s, 1e16 explosion | **do not use** |

**`fold_static.py` is broken**: it froze a *value-dependent* duration/alignment tensor as a
constant (its header only allows freezing *shape*-derived tensors). That baked a wrong fixed
alignment → 218s output + iSTFT divide-by-near-zero blowup. A correct static-ifier must never
freeze the duration path.

## Status / next
- ✅ **Playback works** via the dynamic model (`af_heart`, 24kHz, ~1.6s for "hello world").
- ▢ **G2P**: takes IPA phonemes directly. Arbitrary *text* needs espeak-ng (a C lib, P/Invoke-able, python-free).
- ▢ **QNN path** (needs static shapes): a correct static-ifier (replace `fold_static.py`) + the
  `onnx-surgeon stft-decompose` verb. The decomposition currently shows a ~18% systematic
  amplitude error vs ORT's STFT op on the full graph — to be isolated with a unit test against
  a 1-node STFT model (relevant only to the QNN path, not to playback).
