# dpx — the home-rolled, ORT-free ONNX engine (vendored)

> *"ONNX is protobuf."* A native .NET ONNX interpreter over `Onnx.dll` (protobuf decomposition):
> it walks `graph.Node` in topological order over a `Dictionary<string,Tensor>`, dispatching each
> `OpType` to a hand-rolled kernel (math decomposed from ggml). **No onnxruntime. No python.**

This is the inference engine `ss tts` drives (CRQ121) and the substrate for the gemma LLM rung
(CRQ135). It was a local-only project with **no git** — vendored here for sovereignty (CRQ143 P0):
the engine can never again be lost with the workstation.

## Layout (the buildable closure — ~155 KB of source)
- `onnxnet/onnx.proto` + `onnxnet/Onnx.csproj` — the ONNX schema; Grpc.Tools' protoc compiles it to
  `Onnx.dll` (ONNX `ModelProto` graphs as walkable .NET objects, buildable/editable from PowerShell).
- `onnx-interp/Program.cs` + `onnx-interp/Onnx.Interp.csproj` — the interpreter (`AssemblyName=dpx`).
  Compiles **only** `Program.cs` (`EnableDefaultCompileItems=false`); references `Onnx.dll` via
  `..\onnxnet\bin\...` (layout preserved on vendoring, so the HintPath still resolves).

## Build
```powershell
pwsh -File build.ps1     # 1) onnxnet -> Onnx.dll   2) onnx-interp -> dpx.exe
```
Uses the on-drive dotnet (`S:\bin\dotnet`, or `$env:SS_DOTNET`) — the PATH is intentionally naive.

## Engine CLI (what `ss tts` calls)
`selftest` · `probe <model>` · `run <model> [--inputs <dir>] [--out <wav>]` · `stream <model>
--phonemes-file <ipa.txt> [--voice af_heart] [--out a.wav]` · `fold` · `emit` · `run-compiled` ·
`addoutput`/`addoutput-all`/`nodeinfo` (graph surgery) · `gpu-test`/`gpu-bench`.

## NOT vendored (re-derivable data, kept out of the repo)
- The kokoro `model.onnx` (~310 MB) and `voices/`/`config.json` — public **Kokoro-82M** model data,
  resolved at runtime under `<drive>\reference\Kokoro-82M\` (gitignored `/reference/`).
- `io/kokoro_compiled.cs` — **emit output**, not source (regenerate with `dpx emit`/`fold`).
- `bin/`/`obj/` build artifacts.

## Provenance & known edges (P1 cleanup, CRQ143)
- Vendored 2026-06-25 from `S:\qnn-project\workspace\{onnx-interp,onnxnet}` (the live `ss tts` exe).
  The originals remain the engine `Tts.cs` drives until the intraprocess absorption (CRQ143 P2).
- `Program.cs` has hardcoded `S:\` defaults (the `_gpu\gemm.dxil` GPU-bench scratch paths and the
  `S:\reference\Kokoro-82M` data dir, the latter overridable via `--config`/`--voices`). SS021 smells
  to clean when the engine is lifted under the RuntimeBroker `GraphRuntime` contract (CRQ143 P1).
