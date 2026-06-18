# Areas for improvement — observed by driving the system (2026-06-14)

Written from *driving* the live binary + phones + browser this session (not from docs). Newest insight first.
Each item: what I hit · why it matters · the fix.

## Phones / PSRP
1. **The on-device HTTP server only binds from `MainActivity.OnCreate`.** A folded Razr (`CLOSED_HALL`,
   `app_accessible=false`) never starts the activity, so `tcp:8080` never binds and the whole node is
   unreachable — even though the process (VoiceInteractionService) is alive. **Fix:** bind the server from
   an activity-independent surface (the VoiceInteractionService or a started/foreground Service), so a
   headless/folded device is still a "bonafide pwsh server" ([[phone-as-server]]). This is the single
   biggest gap between "app" and "server."
2. **`Connect-SsPhone` launches with `am start --display-id N`, which Android 16 (API 36) rejects**
   (`Unknown option: --display-id`). The real flag is `--display N`, and CLAUDE.md says to launch with
   `monkey`. **Fix:** `Phone.psm1` should `monkey -p <pkg> 1` (blessed) and fall back to `am start
   --display <id>`; drop `--display-id`.
3. **Model presence is a coarse boolean.** The OnePlus carries `gemma-4-E2B-it.litertlm.part` (556 MB,
   an interrupted download); `/diag` reports `modelPresent:false` with no hint it's a *partial*. **Fix:**
   diag should surface `.part`/size/expected so "why won't it answer" is one query, and `Set-AgentModel`
   should resume/verify (hash) rather than silently leave a `.part`.
4. **`Invoke-Agent` with no usable model blocks to the client timeout** (60s+) instead of failing fast
   with a typed "no model loaded". **Fix:** pre-flight the model state and return an `RbFault` immediately.

## Browser
5. **The HUD/intents driver is Firefox-RDP, not WebView2.** Scott wants a WebView2 *agentic* tool (beyond
   askgoogle). The Firefox driver is excellent for perceive→act→re-perceive but is a separate browser
   engine from the in-app WebView. **Fix (in progress):** a WebView2 agentic surface that exposes the same
   `/goto·/<n>·/type·/respond` intents + HUD against the CoreCLR-owned WebView2.
6. **`firefoxagent.ps1 -Once` re-handshakes the RDP connection every call** (full listTabs/watcher setup
   per intent). Warm Firefox helps, but the attach is repeated. **Fix:** a persistent driver session that
   keeps the console actor between intents.

## Gate / analyzers
7. **The mental model "republish the analyzer after editing the catalog" is WRONG for data edits.**
   `SystemCatalog.json` is read **live via `AdditionalFiles`** at gate time (`SystemCatalogFile.TryLoad`),
   so catalog *data* edits (verbs, components, DAG) take effect on the next `ss check` with **no
   republish**. Only analyzer *code* (.cs) changes need a republish. CLAUDE.md + memory conflate the two.
   **Fix:** correct the doc/memory — "catalog = live AdditionalFile; analyzer code = published."
8. **SS013 triage now carries non-verbs** (Is/Should/Granted/Active/Total/Cpu/Local) admitted to clear the
   census. They owe a real rename (predicate→Query, noun→measure). Tracked in `verbs._note`, but the
   triage bucket is becoming a junk drawer — worth a follow-up rename campaign.
9. **Registering a component (SS011) is not free** — it *expands* the analyzed surface (SS013/SS014/SS015
   begin scanning that folder), so it can ADD findings. Burndown order must register-then-fix, not
   register-to-hide.

## Tooling
10. **`Collab.ps1`'s LLM-relay path is dormant** (needs loaded models); the Windows node spawns a fresh
    `ss.exe -Command` per turn (~1s startup each). **Fix:** drive the local head in-proc and the LLM relay
    once a model is present — then two phones can actually *converse*, not just hash-chain.
11. **Shell state doesn't persist between tool calls**, so phone URLs/sessions get recomputed each step.
    `Phone.psm1`/`Collab.ps1` already make this idempotent (deterministic ports, owner-reclaim) — keep
    every phone helper stateless-by-derivation, never relying on an in-memory session.

## Verb consolidation (Scott's ask — "find ways verbs should be consolidated")
The live surface is **93 project cmdlets**, `Get` dominating at **41**. The biggest dead weight is the
**~25 `Get-Android*` device-telemetry leaves** (Battery, Cpu, Memory, Thermal, Volume, Network, Netstat,
Device, Display, Sensor, GeoLocation, ProcessTree, Job, Alarm, Message, Setting, Storage, Screen, Gfx,
Startup, DeviceIdle …). This is the **"orgy of near-duplicate leaves"** [[mount-points-and-interops]]
warns about — one verb per slice of the same device.

Consolidations (mount the interop, don't grow a bespoke leaf):
- **`Get-Android*` telemetry → one mount, queried by facet.** Either `Get-AndroidDevice` returning a
  composite object (Battery/Cpu/Memory/… as properties), or `Get-AndroidSensor -Kind Battery|Cpu|Thermal|…`.
  Collapses ~25 cmdlets → 1–2 and **also clears most of SS012** (the "Android encodes context in the type
  name" findings) since the context moves to the parameter/host, not 25 type names.
- **Streams → one verb per direction.** `{Start,Stop,Get}-AndroidAudioStream` + `…ScreenStream` →
  `{Start,Stop,Get}-AndroidStream -Type Audio|Screen` (6 → 3).
- **Input injection → one gesture verb.** `Invoke-AndroidTap` + `Invoke-AndroidSwipe` →
  `Invoke-AndroidInput -Gesture Tap|Swipe` (and room for Text/Key) — ties to the input-injection spike.
- **`New-Session`/`New-AgentSession`** (SS012 flagged `New…` as context-in-name) → the New is the verb, the
  noun is `Session`/`AgentSession`; these are fine — the SS012 hit there is a false-ish positive worth a
  catalog note rather than a rename.
Net: ~30 cmdlets → ~6, and the SS012 Android-name debt largely dissolves *as a side effect* of the mount.

## WebView2 agentic browser (Scott's ask — "beyond simply asking google")
`askgoogle.exe` (S:\askgoogle) is the WebView2 base and already has the hard primitives: **CDP trusted
input** (`Input.insertText`/`dispatchKeyEvent` — bot-detection-proof, unlike synthetic JS), **offscreen
render** (loads + runs JS with no visible window), and **settle-by-innerText-length**. What it lacks vs the
Firefox driver is the **agentic perceive→act loop**: a numbered interactive-element list and click/type *by
index*. Plan (building now): add a `--drive "<intents>"` mode that runs a sequence (`goto·read·click N·
type·enter`) against ONE live offscreen WebView2, printing the HUD (numbered elements + text +
`@@SETTLED@@`) after each `read` — the same control surface as `ss-firefox`, on the in-app engine (WebView2),
driven by CDP so it survives bot detection. This is the seed of `Invoke-Browse` over the CoreCLR-owned
WebView ([[web-through-coreclr]]).
