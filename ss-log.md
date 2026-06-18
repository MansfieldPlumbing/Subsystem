# ss.exe Work + Friction Log

Shared log between Claude (canonical `S:\subsystem`) and Antigravity (`S:\antigravity`, kept aligned). Newest entry last.

## Friction 1 — trailing backslash before a closing quote (Antigravity)
- **Symptom**: a command ending in `S:\msvc\"` fails with `ss: The string is missing the terminator: ".`
- **Cause**: the trailing `\` escapes the closing `"`.
- **Workaround**: omit the trailing backslash or double it (`S:\msvc` or `S:\msvc\\`).

## 2026-06-13 — LiteRT-LM Windows binding rolled into canonical (Claude)
**WHAT**: folded Antigravity's in-process LiteRT-LM Windows chat binding from `S:\antigravity` into `S:\subsystem` and made it self-hostable.
- Files landed in `src/host-windows/`: `HeuristicBrokerTypes.cs` (the portable `IChatBackend`/`AgentDelta`/`HbFault` contract), `LiteRtChatClientWindows.cs` (the P/Invoke client), `Chat.cs` (`ss chat`). `Program.cs` gained the `chat` dispatch arm; `Help.cs` the help line.
- Renamed the two lingering `sswin` → `ss` references (csproj comments).
- `S:\antigravity` mirrored to canonical (src trees byte-identical; its `CLAUDE.md` bumped to the current "docs are stale" doctrine so Antigravity stops re-deriving from the dead `docs/` tree).

**Friction 2 — `[LibraryImport]` breaks the ouroboros.**
- The binding used `[LibraryImport]` (a SOURCE GENERATOR). The dotnet-free `ss build self` compiles via `CSharpCompilation.Create(...).Emit(...)` with NO source generators wired in, so the marshalling stubs are never generated → the partial methods are unimplemented → compile fails. A `[LibraryImport]` binding builds under dotnet but the system cannot reproduce itself with it inside (violates prime-directive **rule 0**: anything we add must be buildable BY the system).
- **FIX**: converted `NativeMethods` from `[LibraryImport]` to classic `[DllImport]`. On win-x64 the x64 ABI unifies calling conventions, so it is behavior-equivalent. `[DllImport]` has no UTF-8 string marshalling, so the C API's `char*` args now cross as null-terminated UTF-8 `byte[]` (a `Utf8()` helper at the call sites). The function-pointer param (`delegate* unmanaged[Cdecl]<...>`) compiles fine under `[DllImport]`.

**VERIFIED** (self-built exe `S:\tmp\ss-self\ss.exe`, produced by `ss build self --path <temp stage>`, NO dotnet):
- `ss build self` → GREEN, 129 MB, in-proc Roslyn.
- `ss diag` → 9/9 green (ouroboros fundamentals hold with the binding inside).
- `ss chat "What is 17 + 26?"` → `17 + 26 equals 43.` exit 0 — real Gemma 4 E2B CPU inference via `S:\litert\litert-lm.dll` + `S:\models\gemma-4-E2B-it.litertlm`. The UTF-8 `byte[]` marshalling is runtime-correct.

**KNOWN FOLLOW-UPS (not done):**
- **Native log spam**: litert-lm writes pages of INFO/WARNING to stderr every call. Suppress via `litert_lm_set_min_log_level` (add the P/Invoke + call it in `BringUp`).
- **Self-carried source is stale**: `ss build self` reuses the running exe's embedded ♥/♣ source dump, so the self-built exe carries the PRE-litert dump (a blind extract→rebuild would miss the binding). Real fix = a dotnet-free dump-regen step in `SelfBuild` (the `ss build` dotnet path already regenerates it).
- **Portability**: litert-lm.dll + companions (~65 MB) resolve at runtime from `S:\litert` (dev). For a dropped, portable ss.exe, bundle them as `NativeBinary` in the `SelfBundle` so the ouroboros carries them.
- **Binary not promoted**: working binary `S:\tmp\ss-run\ss.exe` and canonical `S:\ss.exe` still lack `chat` (stale). Promote by file-copy once the embedded-dump refresh lands (else a blind `ss build self` from the promoted binary regresses).
- **host-windows still ungated** (compile-only): the new files aren't analyzer-checked. Closing the host-windows gate gap is queued.

## 2026-06-14 — stale-chat VERIFIED + the real root-cause fix shipped (Claude)
**Verified the prior entry's claims against the live binaries — two were WRONG:**
- ❌ "canonical `S:\ss.exe` still lacks chat (stale)" — FALSE. Canonical *runs* chat (exit 0) AND carries all 3 chat files in its 244-file embedded dump. It reproduces. The genuinely stale 240-file dump existed only in the disposable `S:\tmp\ss-self` (built last session from the pre-chat tool `S:\tmp\ss-run`).
- Proof: `ss extract` from canonical + gen2 (a zero-dotnet `ss build self` *from* canonical) → both write 244 files incl. `Chat.cs` / `HeuristicBrokerTypes.cs` / `LiteRtChatClientWindows.cs`; both run chat at exit 0.

**Root cause (now FIXED): `ss build self` forwarded the build-TOOL's dump instead of regenerating it.**
- `SelfBuild.Compile()` sourced `ss-source.dump` via `SelfResource(name) ?? DiskResource(...)`; `SelfResource` reads the *running tool's own* embedded dump and always wins → new/edited source compiled into the offspring but never carried in its dump. Chained self-builds from a stale tool silently drop files.
- **FIX** (`SelfBuild.cs`): added `GenerateSourceDump(sourceRoot)` — a pure-C# port of `Get-CodeContext` (same blocked-dirs / whitelist / 500 KB cap, same `♦/♠` wire format `SelfSource.Restore` reads). `Compile()` now REGENERATES the dump from the tree it compiles; catalog + icon unchanged. No dotnet, no runspace — fits the zero-dep self-build path.
- **A/B proof**: canonical (old logic) → **gen_A** carries STALE `SelfBuild.cs` (no `GenerateSourceDump`); gen_A (new logic) → **gen_B** CARRIES the fix in its own embedded source. Offspring now embeds the exact tree it compiled.
- **gen_B verified**: diag 9/9 GREEN · 244 fileBlocks · runs chat (exit 0) · carries all 3 chat files. **Promoted gen_B → `S:\ss.exe`** (backup `S:\ss.exe.old`, 135 MB). Canonical re-verified: diag GREEN, chat exit 0.

**Follow-ups status after this session:**
- ✅ RESOLVED: "self-carried source is stale" (dump now regenerated from sourceRoot).
- ✅ MOOT: "binary not promoted" (canonical already had chat; now also has the dump-regen fix).
- ⏳ OPEN: native log-spam suppression (`litert_lm_set_min_log_level` — pending ABI confirmation), host-windows ungated, litert-dll portability bundling.

## 2026-06-14 — LiteRT C-API binding + the native blocker SOLVED-by-download (Claude)
**WHAT**: home-spun the .NET P/Invoke binding to the LiteRT-LM C API and unblocked the Android C-API runtime — WITHOUT a Bazel build.
- `src/runspace/RuntimeBroker/LiteRtNative.cs` — full `engine.h` surface (settings/session/conversation incl. `set_tools`, optional_args, streaming, benchmark, sampler). Classic `[DllImport]`, SafeHandles. Verified compiled into the APK. Committed `25f2047` (+ SS013 extern-exempt so foreign-ABI names don't trip the verb grammar).
- **DECISION (Scott)**: additive — keep the working JNI `LiteRtChatClient`; ADD `LiteRtRuntime : Runtime` (C-API) beside it. No AAR/JDK excision yet.
- **Native `.so` blocker SOLVED by reflexive research**: flutter_gemma (DenisovAV) publishes the monolithic `libLiteRtLm.so` + the `patchelf`'d OpenCL sampler (`DT_NEEDED libLiteRtLm.so`) + the QNN V73 NPU stack. **Downloaded + SHA256-verified** → `S:\reference\litertlm-prebuilt\android_arm64\`. No Bazel needed; `build_android.sh` banked for a sovereign rebuild.
- **GPU "loss" diagnosed**: matmuls run on GPU but the *sampler* fell to CPU (missing TopK sampler `.so` → per-token GPU→CPU readback, ≈7 tok/s). The matched monolithic `.so` + patched sampler fixes it (issue #2211; OnePlus=Adreno, the good case; MTP regression #2227 is PowerVR-only).
- **Naming fence (Scott's concern)**: `.so` filename `libLiteRtLm.so` is locked by the samplers' `DT_NEEDED`, but it's a foreign native-asset filename — never in the object namespace. All foreign names fenced to `LiteRtNative.cs`; `DllName`→`"LiteRtLm"`.

**Friction logged**: APK incremental no-op (stage shares `S:\build` output → ship stale bits; clean the `net11.0-android` obj/bin); `$env:SS_LIBS=S:\libs` required; stage APK lands at `S:\tmp\build\...`. `Phone.psm1` `am start --display-id` rejected on Android 16 (use `monkey`). `askgoogle` `--drive` left non-compiling (RunDrive unwritten — finish or revert).
- ⏳ OPEN: scaffold `LiteRtRuntime` (additive); dynamic-fetch the SoC-matched `.so`s from a GitHub release via pwsh (set `LD_LIBRARY_PATH`); the granular MCP tool loop; the QNN/NPU AOT-model spike. See `latest-session-handoff`.

## 2026-06-14 — Kestrel infra track: Windows-head mount PROVEN, serving green (Claude / Track B)
Parallel to the RuntimeBroker/LiteRtRuntime track. Coordination contract AGREED: this track owns
`Subsystem.csproj` + `SystemCatalog.json` edits; the RuntimeBroker track owns `src/runspace/RuntimeBroker/`
and touches neither; this track builds from a disjoint stage (`S:\tmp\ss-stage-kestrel` -> `S:\tmp\build`)
so canonical `S:\build` stays clear; no simultaneous APK builds.

DONE:
- Proved `FrameworkReference Microsoft.AspNetCore.App` restores + compiles clean for `net11.0-android`
  (probe S:\tmp\kestrel-probe, API 37 via S:\android-sdk). Kestrel is pure-managed (no .so) -> embeds like any DLL.
- Windows head: added the FrameworkReference to `src/host-windows/SubsystemWin.csproj`; new
  `src/host-windows/KestrelHost.cs` (loopback Kestrel mount); `ss serve` mode in `Program.cs`. Built green;
  `ss serve` curled `GET /health` 200 + `GET /diag` 200 IN-PROCESS on the dev box (no device). Run with
  `DOTNET_ROOT=S:\dotnet` (apphost otherwise resolves the machine-global preview.4 runtime and fails).

NEXT (Android — the deliverable):
- One line into `src/runspace/Subsystem.csproj` (this track owns it).
- New `src/runspace/Host/KestrelHost.cs` — additive loopback mount BESIDE ProjectionServer, does not touch it.
  Open analyzer item: a NEW `Host/` file with root-only `namespace Subsystem;` trips a fresh SS011 -> resolve
  by namespacing under a registered component OR adding `src/runspace/Host/` to hostPaths (catalog edit, owned here).
- Auto-launch under the `DEV` constant in MainActivity boot, beside ProjectionServer + the RuntimeBroker boot hook.
Goal chain: Kestrel(HTTPS) -> off-device PSRP -> SSH -> screen broadcast (inverted-scrcpy / Windows-App RDP).

## 2026-06-14 — KESTREL IS IN THE ANDROID BUILD — APK packs green (Claude / Track B)
THE FRONTIER COMPILED. `dotnet build Subsystem.csproj -c Release -f net11.0-android` (from the isolated
copy S:\subsystem-kestrel, output SS_BUILD=S:\tmp\build-kestrel) -> exit 0, 0 errors, signed APK 89.7 MB at
S:\tmp\build-kestrel\Subsystem\bin\Release\net11.0-android\dev.mansfieldplumbing.subsystem-Signed.apk.
86 Microsoft.AspNetCore.*.dll embedded (Kestrel.Core.dll confirmed in obj\...\android\assets\arm64-v8a).

THE BESPOKE FIX (nobody ships Kestrel on Android): AspNetCore has NO android-arm64 runtime pack
(error NETSDK1082), so FrameworkReference cannot embed it. But the framework is 134 pure-managed-IL DLLs
+ exactly 1 native (aspnetcorev2_inprocess = Windows IIS in-proc, irrelevant to Kestrel). So: DROP the
FrameworkReference; embed the managed assemblies straight from $(NetCoreRoot)\shared\Microsoft.AspNetCore.App
as plain <Reference Private=true>, excluding only the native dll. No runtime pack, no .so to stub
(managed IL is RID-agnostic). See Subsystem.csproj.

WIRING: src/runspace/Host/KestrelHost.cs = loopback mount (app.Start() non-blocking, /health + /diag, port
8090 distinct from projection 8080/8081). Auto-launches under #if DEV in MainActivity.EnsureWebServer beside
ProjectionServer. Added to SystemCatalog.json hostPaths as a single-file host-seam exemption (zero baseline churn).

GATE: my Kestrel change added ZERO findings (the hostPath exemption worked). Gate is RED only on TWO of the
RuntimeBroker track's in-flight files that rode along in my 15:48 snapshot:
  SS011  RuntimeBroker/Runtime.cs + LiteRtRuntime.cs  -- namespace 'Subsystem.RuntimeBroker' uses the full
  name; SS011 checks the COMPONENT CODE and the registered code is 'Rb', not 'RuntimeBroker'. FIX in the
  agent copy: namespace them 'Subsystem.Rb', OR add to hostPaths, OR baseline. (Canonical was likely already
  red on these at copy time.)

NEXT: device smoke-test -- install the APK, `ss adb forward tcp:8090 tcp:8090`, curl /diag on the phone =
Kestrel serving in-process on Android.

## 2026-06-14 — SSRD: DirectPort producer + native Win32 viewer + the scrcpy-without-scrcpy proof (Claude / Track B)
The whole DirectPort/Kestrel/RDP arc moved from "settled architecture" to "working code, rolled into
canonical, gate-green." Started M0 (ss.exe as a DirectPort PRODUCER VirtuaCam consumes); ended with the
phone's screen rendering live in a pure-C# native Win32 window — and ALL of it integrated into S:\subsystem
without errors (both heads compile, gate 350/350/new 0).

S: C++ TOOLCHAIN (new, wired into scripts/env.ps1): SS_MSVC=bin\vs\VC\Tools\MSVC\14.51.36223,
SS_WINSDK=bin\Windows Kits\10, SS_WINSDK_VER=10.0.26100.0. No vcvars dependency — src/native/directport/
build.ps1 sets INCLUDE/LIB/PATH from those vars and calls cl directly. (S:\bin\toolchain was incomplete;
W:\bin\vs had MSVC but no SDK — copied the SDK to S:\bin\Windows Kits.)

directport.dll (NEW: src/native/directport, vendored from S:\virtuacam-claude\directport-sdk + 4 added
exports): there is NO prebuilt directport.dll — the dp12_* C API is statically linked INTO VirtuaCam's
producer DLLs, so we vendor + build our own. Added: dp12_get_adapter_luid (broker filters by LUID; C API
otherwise hides the device), dp12_upload_bgra (CPU pixels -> UPLOAD buffer -> CopyTextureRegion -> shared
VRAM texture -> signal shared fence), dp12_is_uma + dp12_last_hresult (diagnostics).

HARD-WON D3D12 FINDINGS (the kind that cost an hour each):
- is_system_ram=true (CPU-mappable texture) is UMA-ONLY. On a discrete GPU CreateCommittedResource returns
  E_INVALIDARG (0x80070057) -- a CPU-visible/L0 TEXTURE is invalid on non-UMA. Real producer path =
  is_system_ram=false (DEFAULT/VRAM shared) + a GPU upload (dp12_upload_bgra). Gemini's "CPU map" assumption
  is wrong on discrete.
- The NO-SIGNAL bug (broker discovers the producer but renders black): the D3D11 broker opens the producer
  texture via OpenSharedResource1/OpenSharedFence. TWO killers fixed in the vendored DLL: (1) BGRA8 +
  ALLOW_UNORDERED_ACCESS is not D3D11-openable -> ALLOW_RENDER_TARGET only; (2) a SHARED_CROSS_ADAPTER fence
  is rejected by D3D11 OpenSharedFence for a same-adapter share -> plain D3D12_FENCE_FLAG_SHARED.
- VERIFIED BroadcastManifest (Gemini's D1 was WRONG -- command @8 + bogus pack(4)). Canonical layout
  (default pack(8), 1056 bytes): FrameValue@0 Width@8 Height@12 Format@16 LUID@20 (model as nested 2x4B
  struct so it 4-aligns; a bare `long` 8-aligns to 24 and corrupts everything) TextureName[256]@28
  FenceName[256]@540 Command@1052. Manifest name DirectPort_Producer_Manifest_<PID> is SESSION-LOCAL (no
  Global\); texture/fence are Global\. Surface.cs runtime-asserts sizeof/offsets.
- The 49kb DirectPortConsumerD3D11.exe (C:\dev\DirectPort-main\Examples) uses the IDENTICAL manifest
  contract ss surface produces -- it, VirtuaCam, and the multiplexer are interchangeable consumers. The
  PC-side viewer is already built and zero-copy (Wait fence -> CopyResource -> sample).

TOKEN ENFORCEMENT (built in from the start, per Scott's directive -- not patched): src/runspace/Cm/
Security.cs = IntegrityLevel lattice (Untrusted<User<Admin<System) + `Caller` (the access-token/subject
analog -- bare `Token` is NT-reserved, gate-flagged) + fail-closed AccessCheck.Resolve (record exists +
Enabled + caller dominates integrity + DependsOn consents granted). Verb names obey the catalog
(Resolve/Grant, not Demand/Deny). CapabilityGate.cs is the shared default-deny door for ss surface/view.
Vom.RegisterNative (src/runspace/Vom/Vom.Native.cs) = a foreign native ptr as a possession-gated handle
(Reclaim = dp12_close), the \Surfaces\<name> mount.

ss view (src/host-windows/View.cs) = a NATIVE Win32 window in pure C# (user32/gdi32 P/Invoke -- real HWND +
message loop + WndProc + StretchDIBits, no UI framework). Modes: `ss view <host>` (phone /screen JPEG over
a WebSocket, no adb) and `ss view --screencap <serial>` (adb screencap PNG poll). DEMO LANDED: the OnePlus
screen (1080x2412) rendered live in our window via screencap -- shell capture, no MediaProjection tap, zero
scrcpy. ~2-3 fps (full PNG/frame) -- the floor; smooth path = loopback-shell MediaCodec H.264.

CAPTURE-PERMISSION RESEARCH (MEASURED on the OnePlus, Android 16 -- not asserted): scrcpy captures by
creating a mirror VirtualDisplay via hidden DisplayManager/SurfaceControl APIs, which need SHELL/SYSTEM uid
(it runs as uid 2000 via `adb shell app_process`). The wall is the server-side UID check in system_server,
NOT reflection (native/CoreCLR JNI bypasses ART's non-SDK guard but NOT the uid check). pm-grant probe
results: CAPTURE_VIDEO_OUTPUT + ACCESS_SURFACE_FLINGER = "not a changeable permission type" (pure signature,
un-grantable); CREATE_VIRTUAL_DEVICE + ADD_TRUSTED_DISPLAY = "managed by role" (VirtualDeviceManager path --
role-managed, not pm-grantable; `cmd role add-role-holder` for COMPANION_DEVICE_{APP_STREAMING,COMPUTER,
NEARBY_DEVICE_STREAMING} FAILED, and the REQUEST_COMPANION_PROFILE_* perms are signature-locked -- needs a
real CDM association the user approves, which third-party apps can't request for streaming profiles).
CONCLUSION: no-adb + no-tap + smooth full-device capture does NOT exist on stock non-root Android. The
unlock is the loopback-shell (on-device wireless-debugging -> shell uid, no PC/cable) -- the CLIENT (PC)
needs zero adb. Tiers: loopback-shell (scrcpy-exact, no tap) | MediaProjection (one tap/boot) | A11y
takeScreenshot (zero tap, ~1fps).

USB measured: 907 MB/s real adb push throughput (USB 3.1 Gen2). Raw 1080p RGBA @60fps = 497 MB/s -> FITS
with headroom (no codec, deletes encode/decode latency = the lever to beat scrcpy at 1080p). 1440p60 = 885
MB/s (just fits); 4K = ~27fps max.

ROLLED INTO CANONICAL: baseline-built canonical first (GREEN, so any error is mine), copied 9 new files,
re-applied 2 additive edits (Program.cs surface/view cases; csproj directport.dll copy item) WITHOUT
touching canonical's 21 in-flight dirty files. Gate caught my lexical drift (Token type + Demand/Deny/
ThrowIfDenied verbs) -> renamed to Caller/Resolve, inlined Deny, dropped the throw helper. Final: Windows
head GREEN, gate 350 findings / baseline 350 / new 0. Left uncommitted for Scott (mixed with his dirty
work). kestrel work copy is now SUPERSEDED by canonical (its copies have the stale Token naming).

NEXT (to a performant scrcpy alternative): see SSRD-HANDOFF.md. Short version: (1) loopback-shell
SurfaceControl/MediaCodec H.264 capture (JNI to the hidden APIs) = the smooth path; (2) MF H.264 decode on
Windows -> DirectPort texture (the DirectPort->MediaFoundation adapter); (3) transport: raw USB for
low-latency, Kestrel+SPAKE2 for WiFi/mesh; (4) A11y input injection -> full RDP; (5) dirty-rects to beat
scrcpy on desktop content; (6) relocate SSRD out of host-windows; (7) the single-csproj / core-portability
question.

## 2026-06-14 — AGENT TRACK: C-API LiteRT proof-of-life, KV knobs, scheduler spine, SS021 (Claude / Track A)
The RuntimeBroker/agent track moved from "compile-green, not runtime-proven" to "the on-device model
answers live, with KV knobs turned and the security hole closed." Canonical `S:\subsystem` is now the
working tree (the isolated `subsystem-agent` copy is fully merged + redundant). Gate ends 355/355/new 0.

THE SIGSEGV — KILLED. `litert_lm_conversation_config_create` was bound as upstream's **0-arg** form, but the
shipped flutter_gemma `native-v0.12.0` `.so` exports the **6-arg monolithic** form (verified against
flutter_gemma's own Dart FFI binding — the recipe, our binding). A 0-arg call left the 6 ARM64 arg-registers
as garbage → the `.so` dereferenced them. FIX: 6-arg signature in `LiteRtNative.cs`; `BringUp` folds system
message + tools into the monolithic create, drops the separate setters. PROOF OF LIFE on OnePlus `106d1839`:
Gemma 4 E2B answered "4" and "Earth" and "I am running" — `[CPU]`, fast (UFS 4.0).

KV KNOBS (the "model falls off a cliff" bug). Root cause = LiteRT-LM **issue #1878**: the engine ships the
primitives (`DeleteTokensFromKvCache`, `SaveCheckpoint`/`RewindToCheckpoint`, `GetCurrentStep`,
`ClearKVCache`) but NO coordinating context manager, so you hit `max_num_tokens` and degrade. (Updates our
old note — `DeleteTokensFromKvCache` EXISTS now, so a rolling-FIFO KV window IS buildable.) Wired two knobs
in `LiteRtRuntime.BringUp` (Android head; Windows mirror is the parity follow-up): `set_max_num_tokens`
(explicit 4096 window, ctor param, was riding the small default) + `set_filter_channel_content_from_kv_cache`
(keeps Gemma's verbose thinking OUT of persistent KV — the fastest cliff-filler, zero OOM risk). Deployed +
bring-up proven with both live. The cliff is RAM-bound on low-mem devices (8 GB S23) — eviction matters MORE
there, not less; per-device limits should be measured + stored in the registry.

SCHEDULER SPINE. NEW `RuntimeBroker/ScheduledTaskTable.cs` — the durable scheduled-task plane (the temporal
agent's foundation): a task is a Cm object at `\Agent\Task\<id>` (no second store, modeled on
`AgentSessionTable`). TWO MODES (`owner` | `agent` — who scheduled it), possession-gated via a `gate`
capability path (default-deny unattended-inference). `Create`/`Query` (due set)/`Open`+`Close`
(run-lifecycle)/`Cancel`/recurrence. Still to build: the brutally-synchronous ticker (Fence `WaitAny` on a
timeout, NOT `Task.Delay`), the unattended-inference capability, the Broker fire-hook, the schedule cmdlet/tool.

SECURITY — AGENT TOOL INJECTION CLOSED. `ToolCatalog.Execute` + `AgentTools` spliced the model's `argsJson`
into a PowerShell here-string (`@'…'@`) → a hostile/hallucinated arg with a column-0 `'@` could break out and
run arbitrary pwsh at the runspace's (dev: ADB-elevated) privilege. FIX: args now cross as **base64** (alphabet
has no quote/newline/`'@`) into a single-quoted literal, decoded device-side — the injection class dies at the
boundary, the `Rs.cs` discipline. Behavior identical, transport inert. (Gemini found this; confirmed real.)

NEW ANALYZER — **SS021 AmbientHostPath**. Bans `Environment.GetFolderPath` / `Path.GetTempPath()` in
component-folder core (src/runspace), host seam exempt (hostPaths) — the core resolves dirs from Cm, never the
ambient OS (the build-level "three heads, one core"). Caught 5 real bleeds (`Cm.cs` ×2, `PackageInfoDb.cs`,
`Rs.cs` ×2), baselined (fix = route through the Host seam → Cm `\System\Config\*`, a follow-up). Checker
republished.

VERB CATALOG — `pwsh` BUCKET. Split the PowerShell cmdlet verbs (`Get`/`Invoke`/`Start`/`Stop`/`Send`/…) out
of `triage` into their own documented `pwsh` bucket in `SystemCatalog.json` (+ `_doc` strings explaining all
three buckets — JSON has no comments, the parser ignores `_doc` keys). `SystemCatalogFile.cs` + SS013 now read
`approved ∪ pwsh ∪ triage` (gate-neutral — same allowed set). Republished. (Owner was confused by the
two-bucket layout; this self-documents it.)

TOOLING (dogfood → improve ss): `ss-build`/`-sign`/`-deploy` gained `-Root`/`-Build` (target a parallel copy
without disturbing canonical — how the agent copy built/deployed); `ss-psrp` now returns LIVE objects (was an
XmlDocument — `Invoke-WebRequest .Content` + `PSSerializer.Deserialize`) + a `-TimeoutSec` knob.

DESIGN WORKSHOPPED (not yet built — see AGENT-HANDOFF.md): the context manager as an evictable KV cache over
the auditable session trail (FIFO default + buckets-as-Conversations + idle compaction, all auditable); the
`\Device\Telemetry\*` push-based no-thrash telemetry mount (one producer per signal, OS broadcasts, fence
doorbell + threshold subs, demand-gated sampler); named pipes/UDS for the local control plane; the QNN model
zoo as NPU perception tools + a Genie LLM as a parallel sub-agent.

NEXT: see AGENT-HANDOFF.md. Short: scheduler ticker → telemetry mount → KV FIFO evictor → the core-portability
pass (39 Android-coupled files in src/runspace) → unify Windows+Android into one multi-target csproj.

## 2026-06-15 · head-alignment decisions + on-device security lock

ALIGNMENT (the arc — see memory `head-topology-shared-runspace`): heads are PEERS, not host-vs-eyes.
`runspace` IS the CoreCLR runspace and belongs to BOTH heads. Target tree: `src/runspace/` = SHARED core
compiled into both, + `runspace/android` + `runspace/windows` seams (host-windows MOVES to runspace/windows).
FOLDERS = NAMESPACES = the component hierarchy (Scott: catch it now, not later). Both csprojs symmetric.
Collapse dual-truth dups: Dg↔WinDg, Runtime↔RuntimeTypes, LiteRtRuntime×2 → one each behind the
MainActivity/WinMainActivity seam. REMOVE the JNI LiteRT backend (drop litertlm-android AAR + GoogleGson/
Kotlin deps + LiteRtChatClient); C-API LiteRtRuntime (P/Invoke libLiteRtLm.so) is the one runtime — portable.
SelfBuild/SelfSource + ApkFactory on BOTH heads ("on both": exe-makes-apk AND apk-makes-apk); SelfSource +
Roslyn compile shared, both packagers shared, head picks the target; SelfBuild.roots must follow the topology.

TRANSPORT: KESTREL PARKED. RDP / screenshare→VirtuaCam / virtual cam+display are binary protocols over
VOM-owned TLS sockets + GPU shared NT handles — NOT HTTP. ProjectionServer (HttpListener) stays the loopback
control/UI plane. Kestrel returns only as raw KestrelServer (never the WebApplication app-model) IF HTTP/2-3/
gRPC fan-out or a 443 RD-Gateway tunnel becomes real; aspnetcore-arm64 build knowledge banked. VirtuaCam
(S:\virtuacam-claude) = the Windows-local zero-copy GPU broker; BroadcastManifest = DirectPort = the VOM at
GPU scope; Surface.cs/DirectPortNative.cs already publish a producer → can feed VirtuaCam today (cam+display
> scrcpy). IddCx virtual MONITOR needs EV+attestation to DISTRIBUTE (confirmed current 2026-06-15, tightening;
Azure Trusted Signing does NOT cover drivers); test-signing for self; camera+capture path is sovereign.
MESH transport = WireGuard/Noise over UDP, NOT HTTP: no cipher agility (= closed-vocabulary discipline),
Curve25519 (= existing SPAKE2 stack), silent-to-scanners (= default-deny), ~4k lines (= home-rolled-minimal),
DoS-resistant. Userspace C# impl (no root/kernel on Android). SPAKE2 pairs WHO; WireGuard secures the PIPE;
scoped capability token (OAuth-shaped bearer) says WHAT. OAuth: take the shape (you already have it as the
capability model), leave the protocol/IdP; real OAuth only as a CLIENT for external services.

SECURITY — ON-DEVICE LOCK (urgent; Scott: "I don't feel safe using my app on my own phone" — correct).
HOLE: loopback is NOT private on Android (any app w/ INTERNET reaches 127.0.0.1:8080) and /api/exec is
UNGATED arbitrary-command execution (ADB-elevated in dev) = local RCE/privesc from any app on the device.
FIX (this session): per-boot capability token required on the command routes; injected into the WebView
in-process (never on the wire); other apps can't read/guess it → refused. The possession-not-identity model
applied to the local surface that was bypassing it. Next layer: private-dir UDS + SO_PEERCRED (own-uid only),
gate the WS + read-only projections, finish token/integrity enforcement.
NEXT: see HANDOFF-2026-06-15-alignment.md. [security build/deploy status appended below after the lock lands]

## 2026-06-15 (cont.) · loopback lock LANDED + gate-GREEN (dogfooded)
The /api/exec local-RCE hole is closed in CANONICAL src, verified by `ss check --gate --path S:\subsystem`
= 355/355, new 0. (Dogfooding corrected me: `Authorized` does NOT trip SS013 — only empty catches tripped
SS007; fixed via a single recording `CloseUnauthorized` helper.) Changes:
- ProjectionServer.cs: per-boot `CapToken` (Guid); `Authorized()` + `CloseUnauthorized()` gate the COMMAND
  routes — /api/exec (X-Subsystem-Cap header), /clixml, /psrp, and the input WebSockets (?cap= query, since
  browser WS can't set headers). Read-only GET projections left open (next layer). Token also written to
  filesDir/.cap (own-uid / adb run-as only) for dev tooling.
- MainActivity PwshBridge: `[Export("getCap")]` — the in-process WebView reads the token; it never crosses
  the wire, so a foreign app on the device can neither read nor guess it.
- shell: lib/api.js (`capToken()` + header + `?cap=` on WebSocketClient) + the raw /api/exec callers
  (surface/screen/settings/shader-bg) + screen.obp's raw /screen WS.
ACTIVATION: ships in the APK → `ss build apk` (kicked off this session) + deploy. NOT yet runtime-proven on device.
DEV-TOOLING FOLLOW-UP (don't slog dev): dev.psm1 (ss-psrp / the /cli client) must read filesDir/.cap via
`adb shell run-as <pkg> cat files/.cap` and send X-Subsystem-Cap, else PSRP/CLI over adb-forward 401s.
NEXT LAYER (see handoff): private-dir UDS + SO_PEERCRED (own-uid), gate the read-only projections,
BiometricPrompt at GRANT moments only (posture-gated: enforced release / relaxed dev), finish token+integrity.

## 2026-06-15 (cont.) · loopback lock RUNTIME-PROVEN on-device (Razr ZY22KN3TSZ)
Built (env.ps1 sourced → SS_LIBS=S:\libs) → signed → gate GREEN → installed → verified end-to-end:
  UN-tokened POST /api/exec 'Get-Date' -> {"error":"unauthorized"}  (the command did NOT run — door locked)
  tokened    POST /api/exec 'Get-Date' -> "2026-06-15T10:00:30..."  (legit in-process WebView path works)
Device filesDir/.cap = 32-char per-boot Guid, read via `adb shell run-as <pkg> cat files/.cap`. The local-RCE
hole (any app on the device driving arbitrary commands over loopback) is CLOSED. The personal OnePlus
(106d1839) takes the SAME signed APK when plugged in. (All three earlier build failures were env/contention —
stale obj, Antigravity file-lock, SS_LIBS unset — never the lock code.)
PENDING: dev.psm1 (ss-psrp / the /cli client) must read .cap + send X-Subsystem-Cap or PSRP/CLI over
adb-forward will 401; next security layer per handoff (UDS+SO_PEERCRED, gate WS+read-only projections,
BiometricPrompt at GRANT moments, finish token/integrity). Build.cs hardening offered (self-source env /
libs safety-check) so a bare-shell `ss build apk` can't repeat the SS_LIBS fallback.

## 2026-06-15 (cont.) · dev.cap wiring · WebView DirectPort adapter · Kestrel+JNI cuts · lexical bans (Claude)
The transport got rethought with Scott and two big bloat sources came out. Canonical `S:\subsystem` is the
single source of truth (the throwaway reorg-copy was abandoned). Dogfooded `ss.exe` throughout.

- **dev.psm1 `.cap`** — `ss-probe`/`ss-psrp`/`/agent` WS now read the per-boot token via `adb run-as cat
  files/.cap` (cached, auto-refresh on 401) so the loopback lock no longer 401s dev tooling. Verified live
  (Razr): capped calls pass, un-capped refused. (NOTE: the Razr's *running* APK at the time wasn't even
  enforcing the lock — a STALE build; the new 81.3 MB APK below carries it.)

- **ARCHITECTURE PIVOT (Scott-driven, locked):** the local WebView↔dotnet channel should NOT be a loopback
  HTTP socket (in-process IPC over TCP = waste + an always-on listener/wakelock = battery). The channel is a
  **DirectPort producer/consumer** of a **Float32 256-aligned row-major region + a fence** (the kernel
  already IS this: `Vom.cs` Alignment=256, VomFormat.Float32). The WebView is a **peer, not a Sub-VOM**
  (sandboxed, unowned) — and specifically the **BUFFER endpoint** (can't open a D3D shared NT-handle like a
  native consumer), so it takes the CPU-side region and uploads to its own canvas/WebGL. The JS side is a
  **CONSUMER endpoint, not a VOM**. Loopback `HttpListener` is DEMOTED to an opt-in
  `\Capability\Remoting\Projection` mount whose only job is remote display to a PC (X11/RDP/scrcpy-style) —
  cap-lock/BindGuard/firewall live there ONLY. Into-the-WebView is ALWAYS a hop (WebGL/WebGPU can't import
  external GPU memory); GPU→GPU zero-copy is the NATIVE-consumer path (VirtuaCam/Win32 viewer). Ground truth:
  `C:\dev\DirectPort-main\Examples` (`DirectPort.h` BroadcastManifest + discover + signal_frame/wait_for_frame).

- **WebView DirectPort adapter (Windows head, FIRST SLICE PROVEN):** `WebViewDirectPortAdapter.cs` (region +
  fence via WebView2 `CreateSharedBuffer`/`PostSharedBufferToScript` — zero-copy CPU buffer on Windows; the JS
  consumer reads the SAME bytes as a `Float32Array`, renders on each `frameValue` tick) + `WinShellUi.cs`
  (WinForms window, portrait 420×860, F11 = true fullscreen via ProcessCmdKey) + `ss ui` mode + **double-click
  → UI** (`Interactive.IsDoubleClick`/`HideOwnConsole`). Screenshot-confirmed: an animated wave at frame 778,
  ZERO socket. Ouroboros intact (`ss build self` GREEN, offspring `diag` 9/9 — WebView2/WinForms compiled
  under in-proc Roslyn because WinShellUi sets visual styles by hand, no ApplicationConfiguration generator).
  Also landed earlier this session: the double-click WebView2 window rendering the FULL shell over loopback
  (screenshot-confirmed) — SUPERSEDED by the no-loopback adapter direction.

- **Kestrel REMOVED (Windows head):** the decision parked it but the code never left — `FrameworkReference
  Microsoft.AspNetCore.App` + `KestrelHost.cs` + `ss serve` gone. **ss.exe 192 MB → 166 MB.**

- **JNI LiteRT REMOVED (Android head):** dropped the `litertlm-android` AAR + GoogleGson + Xamarin.Kotlin.Reflect
  + KotlinX.Coroutines; deleted `LiteRtChatClient.cs` + the dead JNI `AgentTools` cluster (DeviceTool/Build/
  RegistryTools — a duplicate of the runtime-agnostic `ToolCatalog`, which is the live surface; the tools
  themselves are registry DATA, untouched). `Broker` is C-API-only; `Benchmark` relocated onto the `Runtime`
  contract + `LiteRtRuntime` now SURFACES tok/s (the JNI-only path denied it). **APK 89.7 MB → 81.3 MB**, gate
  baseline shrunk 355→343 (JNI binding findings retired), signed, GREEN. The `Java type '$'` gate noise is gone.

- **Lexical hygiene:** bleached "dumb" (9 comments → leaf / no-authority framing); banned `dumb`, `blessed`,
  `sovereign`, `hack`, `heist`, `winning`, `litertlm-android`, and the JNI namespace in `alwaysFlag`. The gate
  reads `SystemCatalog.json` as an AdditionalFile (working-tree), so bans are LIVE — no checker republish needed
  (that's only for analyzer CODE changes). Removed a Streisand-effect comment in favour of the ban.

NEXT (discussed-but-not-built — handoff): see HANDOFF-2026-06-15-adapter.md.
