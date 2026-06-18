# kokoro-js (vendored dist)

This directory holds the `kokoro-js` browser dist: `kokoro.js` (ESM, exporting `KokoroTTS`) plus the
bundled phonemizer WASM (the espeak-ng G2P sidecar — keep it beside the dist; it loads same-directory
relative). License: Apache-2.0. No-CDN rule: these files must be vendored, never fetched remotely —
until they land, `lib/speech.js` degrades to `speechSynthesis`/silence and records the miss once.
