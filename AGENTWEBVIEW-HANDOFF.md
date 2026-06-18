# AGENTWEBVIEW — Session Handoff (2026-06-15)

Self-contained; reads cold. This session built **`agentwebview.exe`** (the WebView2 agentic browsing
surface, evolved from `askgoogle`/`firefox.ps1`), proved an **on-device Gemma model can autonomously
drive it**, and mapped the accelerator + harness roadmap. Companion records: `AGENT-HANDOFF.md`,
`SSRD-HANDOFF.md`, `ss-log.md`. Auto-memory: `agentwebview`, `drive-perceive-before-opining`.

---

## 0. TL;DR — where we are
- **agentwebview.exe** is real and works: a tiny, translucent, resizable, **persistent** WebView2 widget that
  projects any page (web OR `file:///`) into a text **HUD** (numbered element tree) and executes **bounded
  intents** (`goto/click/type/back/respond/wait`). Driven via a watched file channel; emits per-step thumbnails.
- **PROVEN: the on-device dummy drove it.** Gemma **E2B** (OnePlus, CPU) ran the loop autonomously to a
  correct answer (HN top story, 2 hops). **E4B** drove better (3 self-directed hops: search engine → query →
  click) but its heavier CPU inference **wedged the on-device HTTP server** mid-run (app alive, server seized).
- **Backend is now GPU — VERIFIED LIVE.** Changed `LiteRtRuntime.BringUp` from hardcoded CPU to a **GPU→CPU
  ladder** (+ an NPU/QNN scaffold gated on a QNN model), rebuilt + **deployed** the APK → `/diag` reads
  `modelBackend: GPU`, log `BRINGUP e2b verified on GPU (C API)`. E2B runs on the Adreno now (warmed in ~22s).
  NPU is still a scaffold (needs QNN v73 `.so`s bundled + a v73 model). **Next: benchmark GPU vs CPU tok/s and
  re-run the E4B eval — the GPU offload should stop the host-server wedge.**
- **The big architectural through-line (locked):** the HUD/intent loop is Scott's **"ConPTY for agents"** —
  one engine-agnostic surface; engines (Gemma/LiteRT, llama.cpp, frontier-via-browser, Claude-as-MCP) plug in.
  **Projection lives in WebView; truth + authority + memory live in C#** (invariant #4).

---

## 1. What was built — `agentwebview.exe`
- **Source:** `S:\askgoogle\Program.cs` (single file), `AssemblyName=agentwebview` in `S:\askgoogle\askgoogle.csproj`
  (net11.0-windows, WinForms, `Microsoft.Web.WebView2`). README at `S:\askgoogle\README.md`.
- **Build:** `$env:DOTNET_ROOT="S:\dotnet"; & S:\dotnet\dotnet.exe build S:\askgoogle\askgoogle.csproj -c Release`
  → `S:\askgoogle\bin\Release\net11.0-windows\agentwebview.exe` (framework-dependent; run with `DOTNET_ROOT=S:\dotnet`).
  **NOTE: `S:\askgoogle.exe` is the STALE 6/8 build** (the publish-to-S:\ was rejected mid-session). The current
  binary is only in `bin\`. **TODO: publish agentwebview.exe to `S:\agentwebview.exe`** (self-contained single-file).
- **Modes (argv):**
  - `--ask "<q>"` — one-shot google.com/ai, prints the answer. (offscreen)
  - `--fetch <url>` — load a URL, settle, print page text. (offscreen)
  - `--drive "i1 | i2 | ..."` — run a `|`-separated intent sequence in ONE live (cute, visible) WebView, one shot.
  - `--serve` — **persistent** tiny cute widget; watches `S:\tmp\drive\cmd.txt`, executes, writes
    `S:\tmp\drive\hud.txt` + `S:\tmp\drive\step-NN.png`. Stays up between commands (session/cookies persist).
  - `--agent "<objective>" [--brain <url>] [--max N]` — autonomous loop vs an **OpenAI-compatible** brain
    (`/v1/chat/completions`). Has the working-memory loop-breaker (tracks tried/visited, injects both each hop).
- **Intents:** `goto <url>` · `click <N>` · `type <N> "text"` · `back` · `respond <text>` · `wait`.
- **The tree (`Hud`):** tags every visible interactive node `data-ag-id=N`; HUD = `[SITREP]`/`[VIEWPORT]`/
  `[PAGE TEXT]`/`[ELEMENTS]`/`[INTENTS]`. "It's the Android accessibility tree, but for WebView."
- **Thumbnail:** `CoreWebView2.CapturePreviewAsync` (works offscreen; small window = thumbnail-sized PNG).
- **Widget doctrine (Scott, locked):** borderless, translucent (Opacity 0.92), always-on-top, **resizable**
  (WM_NCHITTEST rim), **display-only** (`wv.Enabled=false` — can move/resize/close, not click the page),
  scrollbars CSS-hidden, cute ✕, smallest-possible (unreadable text is FINE — the agent reads the text HUD,
  the human just glances). HITL visibility is OPTIONAL (headless default; widget = the operator's window in).
- **NOT yet built (Scott's asks):** top-left **message button → `agent.obp`-style chat panel**; new-tabs
  **anchor as a bottom tab-strip**; a **bottom resize nub** (vs free-rim); the **MCP adapter** (so Claude
  drives it with no `Start-Sleep` polling — MCP returns when the server says done).

## 2. What was PROVEN (driven live, not asserted)
- **E2B autonomous (phone CPU):** objective "HN top story" → HOP1 `/goto news.ycombinator.com` → HOP2
  `/respond "Your ePub Is fine"` (correct). 2 hops.
- **E4B autonomous (phone CPU):** objective "AndroidWorld SOTA" → HOP1 `/goto duckduckgo.com` → HOP2
  `/type 1 "AndroidWorld benchmark success rate"` → HOP3 `/click 1` → HOP4 the device HTTP server had seized.
  E4B's decisions were *good*; the runtime buckled (see §5).
- **Multi-turn frontier consult:** drove `--serve` on google.com/ai through a real back-and-forth (AndroidWorld
  Q → follow-up), proving "an edge model can ask an adult." Also used it to research its OWN competition (Layla).
- **Local browsing:** `goto file:///S:/` rendered the filesystem as a numbered tree — same loop drives local + web.
- **Findings:** (1) the element tree includes page **chrome** → E4B's `/click 1` hit a nav link not the first
  result (needs result-scoping). (2) the settle heuristic (text-length-stable) **fires mid-stream** on chat
  answers (use `wait`, or port firefox's network-settle + Windows telemetry).

## 3. Architecture decisions (locked this session)
- **"ConPTY for agents":** the HUD/intent loop is the universal surface; engines are swappable backends.
  Three adapters of ONE surface: frontier→**MCP** (Claude), Gemma→**the on-device tool loop**, human→**/slash REPL**
  (man-in-the-chair falls out free). Same "one JSON, N consumers" doctrine, applied to *who drives*.
- **Projection in WebView, authority in C#:** the page may *suggest* intents (JS/iframe, per-page, dynamic,
  CDN-rich — never touches the C# framework); the C# kernel *validates + executes* them gated. Never let the
  untrusted page self-authorize. (= invariant #4.)
- **E2B-worker / E4B-main:** E2B (fast, stateless) = HUD-builder/summarizer/pruner; the main model (E4B) holds
  the persistent memory/KV and decides. The fast dummy serves the slow one. (= the two-pass idea.)
- **Consult = fence-blocked one-off:** `askgoogle --ask` demoted to a `Consult` verb the agent fires and waits
  on (killable `:worker` + `Fence.WaitAll`); `Invoke-RdConsult` already exists on-device.
- **Dynamic prompt assembly + history-pruning** are the two levers that let a dummy research: assemble the
  prompt per-state from {context + ONLY this viewport's scoped intents + objective + tried-so-far}; track
  visited/tried so it can't loop. (StateAgent's PromptFormatter + working memory.)
- **HONEST take on "can E2B/E4B research?"** Lookup: yes. Multi-source w/ scaffold: E4B yes, E2B shaky.
  Open-ended deep research alone: no. The dummy is the *driver*; the harness is the *researcher*. With
  scaffold + history-pruning + consult-on-hard-steps, E4B does useful private on-device research. Don't claim
  cloud-Deep-Research *judgment* parity — that gap is what the consult fills. Report **two numbers**
  (local-only vs local+consult); the shrinking delta (as the policy-cache fills) is the pitch.

## 4. StateAgent / Jeeves = the C# harness blueprint (port target)
- `C:\dev\StateAgent` (Python) — the node pipeline to port: `InputParser → MemoryRecall → PromptFormatter →
  ContextMonitor → LLMCall → WorkingMemoryUpdate → MemoryGatekeeper`. `UserDossier` = persistent working
  memory (`deque(maxlen=20)` = the rolling KV) + persona/ability/engine cards. `MemorySystem.intelligent_recall`
  = entity-filtered vector search ("Two Sarahs" fix via speaker_id+entity_id) + `remember` (enrich→route→embed)
  + enroll/identify by voice-signature embeddings. `MemoryGatekeeper` = enrich→route→store (consolidation).
- `S:\reference\Jeeves` = the evolved version (adds orchestrator, goal/state-tracker, web/android tools, auth) — mine next.
- **Port plan (Scott: "custom build it for this series of models"):** lift the pipeline + dossier + vector
  memory + **card-based dynamic prompts** into `Rb` (C#), **Gemma-4-tailored** — Gemma specifics (the
  `<|tool_call>` DSL, `<|think|>` channel, KV knobs, tight sampler, constrained decoding) live in the engine
  adapter, NEVER in the pipeline. Maps onto existing pieces: dossier→`AgentSessionTable` (`\Agent\Session`),
  vector store→`\Agent\Memory\<id>` (EmbeddingGemma-300M on NPU), cards→`\Capability\Prompt\*`, scheduler→
  `ScheduledTaskTable`. Build in agentwebview first (eval-able now), then port.

## 5. The runtime/accelerator situation (the meat)
- **Backend = CPU** (`/diag.modelBackend: CPU`; log `BRINGUP e2b verified on CPU (C API)`). The deployed
  on-device runtime is the **C-API `LiteRtRuntime`** (`src/runspace/RuntimeBroker/LiteRtRuntime.cs`), driving
  `libLiteRtLm.so` (the flutter_gemma monolithic prebuilt).
- **The server that wedged is `ProjectionServer` (HttpListener), NOT Kestrel.** Proven via `Server:
  Microsoft-NetCore/2.0` header on device:8080; Kestrel (8090) isn't running (only 8080+8081 listen). E4B's
  synchronous CPU inference ran on the HttpListener request path and seized it. **Fix = inference on a
  killable worker (the Doghouse), not the server thread** — true regardless of which server. (Kestrel's
  VOM-MemoryPool/threading would help but isn't the root fix.)
- **GPU/NPU — what I changed this session** in `LiteRtRuntime.BringUp` (was hardcoded `admitted = {"cpu"}`):
  - Now a **ladder**: `qnnModel ? {"npu","gpu","cpu"} : {"gpu","cpu"}` (qnnModel = model path tags
    qualcomm/qcs/qnn). GPU first for normal models, CPU guaranteed fallback.
  - `targetBackend` whitelist extended to allow `"npu"`.
  - For the `npu` rung, sets `litert_lm_engine_settings_set_litert_dispatch_lib_dir` to the model's directory
    (co-locate the QNN `.so`s there). **NPU is a SCAFFOLD** — it will fail+fallback until (a) the QNN v73
    `.so`s (`libQnnHtp.so`, `libQnnHtpV73Skel.so`) are bundled in the APK and (b) a v73 model is on device.
  - **APK rebuild: deploy + measure** GPU speedup and whether the wedge disappears. **Build cmd (GOTCHA: must
    point at the repo SDK or you hit `XA5207: android.jar for API 37 not found` — the default machine SDK
    `C:\Users\Scott\AppData\Local\Android\Sdk` lacks android-37; `S:\android-sdk\platforms\android-37.0` has it):**
    `$env:DOTNET_ROOT=S:\dotnet; $env:SS_LIBS=S:\libs; $env:SS_JDK=S:\jdk; dotnet build
    S:\subsystem\src\runspace\Subsystem.csproj -c Release -f net11.0-android -p:AndroidSdkDirectory=S:\android-sdk`
    (blessed/gated path = `ss-build apk`, which resolves SDK/JDK itself). Deploy: `adb -s 106d1839 install -r <Signed.apk>`.
    First raw attempt failed on XA5207 (wrong SDK dir); retried with `-p:AndroidSdkDirectory=S:\android-sdk`.
    **STATUS: built → deployed → GPU VERIFIED (`modelBackend: GPU`, `BRINGUP e2b verified on GPU`). The signed
    APK is at `S:\build\Subsystem\bin\Release\net11.0-android\dev.mansfieldplumbing.subsystem-Signed.apk`
    (`S:\subsystem\Directory.Build.props` redirects build output to `S:\build\Subsystem\...`, not the repo `bin\`).**
- **NPU / QNN — the two roads (both viable, different runtimes):**
  1. **LiteRT-LM QNN delegate** (current runtime): the on-disk `S:\models\gemma-4-E2B-it_qualcomm_qcs8275.litertlm`
     (3.07GB) is the QNN-accelerated variant. QNN HTP binaries are **arch-keyed (v73)**; if `qcs8275` is a v73
     part it loads on the OnePlus (SM8550 = v73) — "compat" = same-arch, not backward. **TEST = cheapest NPU
     check:** push qcs8275 to the phone, bundle the QNN v73 `.so`s, select it (the BringUp scaffold then tries
     npu). Risk: if qcs8275 is v68/v69 not v73, it errors → that's the answer.
  2. **Qualcomm Genie (native QNN LLM runtime):** you HAVE the full toolchain to **compile your own v73 model**
     — `C:\bin\qnn` has QAIRT **2.46** + `qnn-genai-transformer-composer` + `QnnGenAiTransformerComposerQuantizer.dll`
     + `qnn-context-binary-generator` + the `Genie` runtime (`genie-t2t-run.exe`, `Genie.dll`) + v73 stubs +
     `examples/Genie/configs/htp_backend_ext_config.json` + a `test-harness-s23-v73` (S23 = v73 like the OnePlus).
     This produces a Genie/QNN context binary, NOT a `.litertlm` → it's a **NEW Runtime adapter** beside LiteRT
     (the deep-dive's "Genie LLM as parallel sub-agent"). The "how to make a v73 model" answer: you don't need
     to find one online — compile it here. Heavy but fully owned. (Also `S:\qairt` 2.42 + `S:\qnnscripts`.)

## 6. Phone plumbing cookbook (exact — so you can drive it cold)
- **Devices:** OnePlus `106d1839` (CPH2451, 16GB, SM8550/v73 — the dev phone, `SS_SERIAL`); Razr `ZY22KN3TSZ`
  (clamshell). adb = `C:\bin\SCRCPY\adb.exe` (the running server, pid was 15632).
- **Forward to the phone's ProjectionServer:** `adb -s 106d1839 forward tcp:18080 tcp:8080` → `http://127.0.0.1:18080`.
  Endpoints: `/diag` (JSON), `/apps`, `/health` (HTML shell), **`/api/exec`** (POST a RAW PowerShell command
  body → runs in the on-device runspace, returns output).
- **Run a phone command:** `Invoke-RestMethod http://127.0.0.1:18080/api/exec -Method Post -Body 'Get-Date' -TimeoutSec 20`.
  **Complex scripts mangle in transit** → base64-wrap: body = `Invoke-Expression([Text.Encoding]::UTF8.GetString(
  [Convert]::FromBase64String("<b64>")))`.
- **Drive the on-device model:** `Invoke-Agent "<prompt>" -AsText` (params: Prompt, AsText only — NO -System).
  Response = a stream of `{"role":"assistant","channels":{"thought":"..."}}` JSON deltas, then the **final
  answer as plain text AFTER the last `}}`**. Cold-load: E2B ~20s, E4B ~34s. Pass the prompt base64-wrapped.
- **Model cmdlets:** `Get-AgentModel` (Id/Name/Present/Active), `Set-AgentModel -Id e2b|e4b`, `Set-AgentContext`
  (ChatContext/SystemContext — use to give a clean web-agent persona, kills the device-broker bleed),
  `Invoke-RdConsult` (the consult), `Get-AgentSettings`, `*-AgentSession`.
- **Phone model dir (app-private):** `/data/data/dev.mansfieldplumbing.subsystem/files/models/`. Contains
  `gemma-4-E2B-it.litertlm` and (pushed this session) `gemma-4-E4B-it.litertlm` (3.66GB). 12B not present.
- **Pushing a model to app-private (scoped-storage workaround — IMPORTANT):** Android 16 FUSE blocks the app
  from reading its own `/sdcard/Android/data/<pkg>` via raw paths, and adb can't write app-private directly.
  The working method: `adb -s 106d1839 reverse tcp:9347 tcp:9347` + run a loopback file server on the PC +
  on the phone `HttpClient.GetAsync(...).Result` streamed to `[System.IO.File]::Create($dst)` (IWR `-OutFile`
  FAILS on the absolute path via the PS provider — use HttpClient+IO.File). Streamed E4B in at ~38MB/s/103s.
- **Recover a wedged APK:** `adb -s 106d1839 shell am force-stop dev.mansfieldplumbing.subsystem` then
  `adb shell monkey -p dev.mansfieldplumbing.subsystem -c android.intent.category.LAUNCHER 1`, re-add the
  forward, `/diag`. (Sandbox note: `Start-Process pwsh` is EPERM-blocked here; `Start-Process <exe>` and
  `dotnet build` work; long-running PC helpers must run as a tool background task, not Start-Process.)

## 7. Drive the Windows widget (cold)
1. `$env:DOTNET_ROOT="S:\dotnet"; Start-Process S:\askgoogle\bin\Release\net11.0-windows\agentwebview.exe -ArgumentList "--serve"`
2. Wait ~7s; `Get-Content S:\tmp\drive\hud.txt` should say "ready".
3. `Set-Content S:\tmp\drive\cmd.txt 'goto https://news.ycombinator.com/' -NoNewline`; wait; read `hud.txt` + `step-01.png`.
4. To let the **phone** drive it: orchestrate in pwsh — read `hud.txt` → `Invoke-Agent` (base64 prompt) → parse
   intent (regex for `/goto|/click N|/type N "x"|/respond|DONE`) → write `cmd.txt` → loop. (This session's E2B/E4B
   evals used exactly this; the loop-breaker injects an `[ALREADY TRIED]` band.)

## 8. Competitive landscape (researched live via the tool)
- **Layla** = closest direct competitor (native Android, small models, native MCP, on-device multi-step research).
  So you're NOT first to the generic "phone does research" demo.
- The **local-execution cohort** (OpenClaw, Nanobot/HKUDS) all run via **Termux + proot Linux** — the exact
  thing Subsystem rejects. **Your differentiator = native in-process CoreCLR+pwsh, no Linux crutch**, capability
  security, the consult escape hatch, compounding browser-state memory, and assistive-first (blind + speech).
  Position on the SUBSTRATE + the assistive wedge, never the leaderboard.

## 9. Next steps — ordered pick-up list
1. **Check the APK build (`b2l1tbmsx`), deploy it, measure GPU.** `/diag.modelBackend` should read `GPU`; time
   a turn vs CPU; confirm the server no longer wedges under load.
2. **Tree-scoping** (cheap, every-run win): make `[N]` map to real results, not nav chrome (rank/filter, or
   per-page dynamic intents via iframe).
3. **Killable-worker (Doghouse) for inference** — off the HttpListener thread so no pass can wedge the host.
4. **Publish `agentwebview.exe` → `S:\`** + the polish: message-button→`agent.obp` chat, bottom tab-strip, resize nub.
5. **NPU test:** push `qcs8275.litertlm` + bundle QNN v73 `.so`s in the csproj + select it (BringUp scaffold tries npu).
6. **StateAgent → `Rb` C# port** (pipeline + dossier + vector memory + dynamic card-prompts, Gemma-4-tailored).
7. **MCP adapter** for agentwebview (kills Claude's `Start-Sleep` polling) + the **message back-channel** (a
   watched file/queue → Claude's next REPL — same primitive as `cmd.txt`).
8. **Set-AgentContext** web-agent persona (fair evals) + **flow-settle** fix (firefox network-settle + Win telemetry).
9. **Genie self-compile v73 model** (the own-it NPU road) if the LiteRT qcs8275 path doesn't pan out.
10. **STILL OPEN from the session's original ask** (never reached — pivoted to agentwebview): unify Win/Android
    builds + clear the analyzer burndown + docs/code audit. Gate is **355/355 GREEN**. The convergence found:
    **SS021+SS018 + SS012 `Android`-naming + much of SS011 ARE the build-unify thesis as analyzers** — burning
    them down unifies the heads. Docs/ tree is stale (quarantine+derive). `ss contextualize`/`ss check --list`
    are the live-truth tools.

## 10. The one-off MCP answer (Scott asked early)
Yes — build `ss`/agentwebview as an MCP server **for Claude**, but **projected** from `ss contextualize --json`
+ the registry `agentTool` manifests (one JSON, N consumers), never hand-maintained (that'd be a second store /
prime-directive violation). It speeds the *inner loop* (typed tools, no text-parsing, structural `ss-refs` =
"no grep" by construction) and makes dogfooding the path of least resistance — but it's an **ergonomics/discipline
multiplier, not a capability unlock**; it won't move the critical path (seam work, device capture, KV). The
highest-leverage version IS the on-device agent tool loop with a second consumer — build it once, Claude + Gemma
both consume it.
