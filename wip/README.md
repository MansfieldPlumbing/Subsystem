# wip/ — the workshop (clone-and-rebuild sovereignty)

Parked scattered projects so **the whole system lives in one repo**: a fresh `git clone` plus the
re-derivable data (below) is enough to rebuild everything — no dependence on the workstation's loose
`S:\` siblings. These are **not yet integrated** into the `ss` build; they're staged here until each is
folded into `src/` proper (the dp-onnx pattern: vendor → mount as a Runtime/Device → absorb).

**Only source is vendored.** Heavy, re-derivable artifacts (models, runtimes, build outputs, the WebView2
engine) are gitignored — see each entry for where to re-fetch them.

## Parked

### `vom-gpu-compiler/` — the GPU PE-factory / pure-C# D3D12 spine (validated)
A compute shader that **assembles a native Win64 PE32+ executable on the GPU**, driven by **pure-C# D3D12**
(raw COM-vtable dispatch, only OS exports P/Invoked — no home-built DLLs), with the **VOM brokering every
D3D12 resource as a handle** (cascade-reclaimed). `compile.ps1` is self-contained (`Add-Type` on Windows
PowerShell 5.1). **State: produces a structurally-valid, OS-loadable PE** (verified: `Start-Process`
accepts it, PEReader parses PE32+/WindowsCui, `.text` holds real GPU-emitted x64). **Frontier ("no
further"):** a *functional* exe — the code body has a stack-imbalanced epilogue and the import table is
built in `.rdata` but not wired into the data directory. This is the physical proof of the VOM=DirectPort
spine and is **directly reusable as the GPU inference harness** (swap the PE shader for a GEMM/attention
shader; weights become parked VOM handles). See the boundary taxonomy: `DpBoundary` / `VomBoundary`.

### `agent-browser/` — the warm WebView2 browse agent (`ss browse` / `Rd.Consult`)
Source only (single `Program.cs` + csproj + config + `fetch-runtime.ps1`). An MCP-stdio-driven persistent
WebView2 session (`browse_goto/click/type/hud/do`); the consult/"phone-a-friend" is the `browse_do "ask"`
intent. **Not vendored:** the ~100 MB WebView2 Fixed-Version engine (`WebView2Runtime/webview2-fixed.zip`)
— re-fetch with `fetch-runtime.ps1`; falls back to the system Evergreen runtime if absent. **Integration:**
mount as an MCP-child driving the stub `Invoke-RdConsult` (`src/runspace/Pwsh/Cmdlets/RdCmdlets.cs`). CRQ120.

### `gemma4/` — Gemma-4 E2B on dp-onnx (the GraphRuntime LLM rung)
The decisions + state docs (`INTEGRATION-PROMPT.md`, `PHASE1-STATE.md`, `SYNTHESIS.md`, `DEMUCS-BACKFILL.md`)
and the one-time export `export_gemma4_e2b_decomposed.py` (python, export only — no python at runtime).
**Not vendored:** the exported graph + weights (`W:\gemma4\onnx\e2b_step.onnx` + the 4.48 GB PLE etc.) —
re-export from HF (gated Gemma-4) via the script. dp-onnx gap #1 (bf16 load) is already in
`src/native/dp-onnx`. CRQ144.

### `d3d12-kernels/` — the HLSL compute kernel library (the inferencing kernels)
~55 D3D12 HLSL kernels (`mul_mat_split_k_reduce`, `flash_attn_split_k_reduce`, the `dequant_q*`,
`geglu/swiglu/gelu`, `group_norm`, `im2col_3d`, …). These are the matmul/attention/quant kernels that plug
into the `vom-gpu-compiler` D3D12 harness for the GPU off-ramp / GPU inference. Compiled `.dxil`/`.cso` are
gitignored (re-derive from `.hlsl`). CRQ117/off-ramp.

## Still scattered (to vendor — sweep CRQ)
Remaining `S:\` siblings not yet parked here: `directport-project`, `onnxsurgeon-project` (retire-as-dup
candidate vs dp-onnx), `coreclr-hle-project` (PE/SEH/DLL-resolution research), `mansfield.dev-project`,
`terminal-project` / `android-terminal-project`, `razrcover-project`, `flickpaint*`, `virtuacam-project`
(native artifacts already in `src/native/virtuacam`), `qnn-project` (dp-onnx already vendored from it).
Each gets source-vendored here with heavy data gitignored, then folded into `src/`.
