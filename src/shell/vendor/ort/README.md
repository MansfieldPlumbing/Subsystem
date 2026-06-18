# onnxruntime-web (vendored dist)

This directory holds the `onnxruntime-web` browser dist: `ort.min.js` plus its `.wasm` sidecars
(`ort-wasm-simd-threaded.wasm` and the `.jsep` variant — `lib/speech.js` points `ort.env.wasm.wasmPaths`
here, so the sidecars must sit beside the dist). License: MIT (Microsoft, onnxruntime).
No-CDN rule: these files must be vendored, never fetched remotely — until they land, the Speech seam
degrades to `speechSynthesis` and `Speech.available()` reports false.
