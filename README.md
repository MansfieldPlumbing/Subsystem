# Subsystem

**A self-hosting, GC-free compute fabric** — an NT-Object-Manager-shaped runtime where memory, code, threads, and devices are *one namespace of refcounted handles* (the VOM), and **one transport (DirectPort)** pushes fenced, zero-copy regions across every boundary: process · GPU↔NPU · language · machine. It builds itself, owns its own memory, and reclaims deterministically (free-on-zero / cascade-kill).

> *First proof, below: PowerShell 7 hosted in-process inside a native Android app — no Linux userland, no VM, no proot, no root.*

Subsystem runs the `Microsoft.PowerShell.SDK` runspace directly inside a .NET 11 (`net11.0-android`) process on CoreCLR, on the device's ARM64 CPU. It is **not** a terminal emulator, an SSH client, a Termux/chroot environment, or a Linux VM. The runspace lives in the app's own process, defines cmdlets at runtime, and is driven by an on-device LLM.

Prior approaches run PowerShell *inside a Linux environment* on Android (Termux + proot; the Android 15 Linux-terminal VM). Subsystem hosts the CoreCLR runspace in-process in an ordinary Android app — a path generally reported as not working in the PowerShell SDK / Android discussions. This repository is a working implementation of it.

## The telos — the *why* (load-bearing; locked)

Subsystem is **fixing the original sin of NT while writing a love letter to it.**

NT's genius was the object model — one namespace, the handle as authority, mechanism over policy. Its sin was the *floor*: a closed ring-0 type taxonomy you cannot extend from outside the kernel, a registry bolted on as a **second store** beside the Object Manager, and a kernel that cannot host or rebuild itself. Subsystem keeps Cutler's discipline whole and severs the sin with one move — **recursion**: one self-similar node type (a Sub-VOM *is* a VOM), `Cm` **projects** the namespace instead of being a second store, and the image builds itself from itself. The love letter is the homage; the fix is the recursion.

Shown the architecture cold, a frontier model placed it in the canon unprompted — and the code bears it out, line for line:

- **Plan 9** — one namespace; a thread, a Sub-VOM, a managed object, and native memory all become the *same* path'd handle in one owner table, and the foreign world is **mounted** as structured objects, never streamed. (`src/runspace/Vom/Vom.cs` — `Register`)
- **Erlang — let it crash** — terminating an owner cancels its token, cascades depth-first to its children, and revokes every handle; a wedged thread is left *resourceless* and quarantined while the whole keeps running. (`Vom.cs` — `Terminate`)
- **The Lisp/Smalltalk machine** — a living image that **builds its own build with its own build**: the running binary compiles its own carried source in-process with Roslyn — no external toolchain — regenerates its own source dump, and self-reproduces generation to generation. (`src/runspace/windows/SelfBuild.cs` — `CSharpCompilation.Create`)
- **Memory** — NT handle semantics, deterministic and owner-scoped: refcount, free-at-zero reclaim, per-owner quota; the native-engine path reclaims gigabytes through `SafeHandle` before the GC ever sees the wrapper. Determinism where every comparable runtime has a garbage collector.

As far as the record shows, this is the **first native in-process CoreCLR + PowerShell runspace on Android**, and it goes harder on NT, objects, and memory than anything else in the on-device space — which is otherwise inference plumbing and apps, not an operating substrate.

> **This telos does not change without the author's explicit approval.** Everything below is *how* it is built and *where* it is going; this is *why*, and it is fixed.

## What it is

A **self-hosting object machine** (the whitepaper's own subtitle) — an NT-Object-Manager-shaped object substrate. The core is the **VOM (Virtual Object Manager)**: a microkernel-*shaped* object kernel modeled on the Windows NT Object Manager — refcounted named handles as authority, per-owner quotas, and a deterministic cascade-kill. It is microkernel-*shaped*, not yet a microkernel in the strict sense: today its owners share one address space and isolate cooperatively, so the hardware-enforced inter-owner isolation that earns the unqualified word is tracked, not claimed. NT/Windows priors transfer directly, because the abstractions are real analogs:

| NT / Windows | Subsystem |
|---|---|
| Object Manager (`\Device\…`, handles, refcount) | **VOM** — `\Capability\…`, `\Shell\…`, `DropPrefix` |
| Configuration Manager / registry | **Cm** — volatile + SQLite, HKEY-style paths |
| Handle = authority (access masks) | capability-backed security (possession, not identity) |
| Access tokens + integrity levels | the consent gate + integrity lattice (in progress) |
| The shell (Explorer / taskbar / widgets) | the Shell / Taskbar / Menu presenter objects |

It is NT-*shaped* — CoreCLR + PowerShell + web — not Win32-compatible. The kernel discipline is copied; the `ntdll` ABI is not.

## The discipline

One rule, applied without exception: **everything is an object in one typed namespace, and nothing holds its own truth.**

- **The registry is canonical.** Capabilities, presenters, themes, agent tools, sessions — all are `Cm` records. There is no second store. Adding a capability is a registry row; it then appears everywhere it is relevant by construction, with no new code path to keep in sync.
- **The handle is the authority.** Access is possession of a handle, not identity. A capability fires only with the handle that grants it; an ungranted verb is structurally unreachable, not merely hidden.
- **The UI is a presenter.** It renders objects and holds nothing. The shell reads the registry's orders and assembles chrome from them; a presenter contributes verbs at runtime instead of owning a menu. State lives in the namespace; the DOM is a projection of it.
- **Behaviors are verbs on objects**, not inline functions — registrable, token-gated, enumerable.

The codebase is held to this mechanically: a suite of Roslyn analyzers flag truth held outside the namespace — PowerShell baked into C# strings, static dictionaries as parallel stores, fabricated namespace-path literals, raw memory crossing a cmdlet boundary — and the build gates on them. The driver and UI layers are still being brought fully into line; the analyzers are how that work is measured rather than asserted.

## Security posture

The system is built to be a **good citizen of a device its owner controls.**

- **The owner decides; the system informs and obeys.** A capability that reaches into something consequential — the camera, the microphone, the torch, screen capture, off-device exposure — does so only after an explicit, informed, revocable opt-in. Nothing fires by default, nothing fires silently, and the system bans nothing the owner has knowingly allowed. The owner's hardware does not move without the owner's intent.
- **The WebView is an air-gapped renderer, not a browser.** It has no external origin and no network reach: it loads only loopback and registry-served content, and refuses every other scheme. It holds no truth — registry to projection to DOM for rendering, DOM event to intent to registry for truth. The browser threat surface (cookies, CORS, remote content) is designed out, not defended against.
- **Loopback-only by construction.** The in-process HTTP host binds only to loopback; a non-loopback bind is refused until HTTPS and an authentication gate exist. The home-rolled adb stack reaches the device shell over mutual-TLS on loopback, with no native adb binary and no root.
- **Failures degrade, they never vanish.** A faulted component returns a typed degraded result and records it to the one diagnostic surface; the whole keeps running. An empty result and a failed result never look the same, and an empty `catch {}` is a build-time analyzer finding.

Full token/integrity enforcement across every path is in progress; the principle above is the design the enforcement is being built to.

## The agent

An on-device LLM (Gemma via LiteRT-LM) drives the OS through the same object model. Its entire tool surface is **projected from the registry**: any capability whose manifest declares an `agentTool` block becomes a callable tool by construction — a manifest is simultaneously the tool schema, the widget type, and the permission surface (one JSON, three consumers). No tools are hardcoded. A tool that drives hardware is consent-gated on the same possession principle as any other verb. The model chooses; the deterministic harness does the work — the intelligence the system depends on lives in the harness, not the weights.

## Verified on physical hardware (Galaxy S23, Motorola Razr+)

- **VOM kernel** — generational handles, per-owner quotas, fences, and a deterministic kill switch (`DropPrefix` / `Terminate`). Threads are handles; spawning cascades, and terminating an owner reclaims the whole subtree.
- **Kill-switch blast radius** — a deliberately-leaked zombie grandchild thread, after its owner handle was revoked, was quarantined rather than crashing the app.
- **Home-rolled managed adb** — Curve25519 (SPAKE2) pairing + StartTLS mutual-cert connect, reaching the device shell. No native adb binary, no BoringSSL, no root, no Shizuku.
- **Object-oriented device control** — adb operations return pipeable PowerShell objects (`Get-AdbProcess`, `Get-AndroidProcessTree`, `Stop-AndroidProcess`, …), not scraped strings.
- **Cm registry** — capabilities persist to SQLite (WAL) and rehydrate across a cold restart.
- **On-device LLM** — Gemma via LiteRT-LM, streaming, with a projection UI and a served PowerShell CLI over loopback.
- **Registry-driven shell** — a bootstrap assembles a taskbar, a cascading namespace menu, themes, and a themeable surface, all projected from the registry. Deployed and running on device.

## HTML applets

[`content/html-applets/`](content/html-applets) holds complete programs that are each **one HTML file**. This is the casual tier — distinct from the shell's own presenters, which are full registry citizens projecting the namespace. An html-applet is a guest the OS hosts: it ships loose inside the APK, the boot registrar seeds it into the registry as a launchable object, the shell launches it, the theme system skins it, and `/shell/presenter.js` gives it menus and verbs. No build step, no framework, no bundler — drop a `.html` in the folder and it is in the Start menu. [`minesweeper.html`](content/html-applets/minesweeper.html) is a faithful Win95 build whose sound effects are synthesized in-page with Web Audio oscillators (no shipped audio); [`roku.html`](content/html-applets/roku.html) is a working Roku remote.

## In progress and roadmap

The list above is limited to what the device has demonstrated. Active and planned work, in honest order:

- **UI hardening** — the presenter layer is functional and being brought to a polished, non-hostile state across the shell, terminal, files, and editor.
- **Native LLM function-calling** into device cmdlets, and the consent/integrity gate across every tool path.
- **OS integration** — home-screen widgets (an `AppWidgetProvider` projecting registry card records), dynamic shortcuts, and a live-wallpaper provider; the "true form" of the app is a widget surface, not a desktop in a window.
- **The optical link** — a torch-and-light-sensor handshake protocol (ITU Morse for negotiation), as a peer-to-peer channel between devices.
- **Remote access** — a gated HTTPS mount (Kestrel) carrying off-device PowerShell remoting (PSRP) and screen delivery, behind a mandatory tunnel; and a zero-copy GPU path to a Windows consumer.
- **A Windows head** — the same VOM with swapped drivers, to develop the system from within itself.

Formal specifications are being written for publication and land in `docs/` as they are finished.

## Building

An Android (.NET) app: .NET 11 preview SDK + **JDK 21** (the LiteRT-LM AAR is Java-21 bytecode), targeting `net11.0-android`. Build on physical hardware — emulators are a dead end for the CoreCLR + PowerShell path. The native shims (`libpsl-*.so`) are built with `src/runspace/native/build-native.ps1` (set `SS_NDK` or `ANDROID_NDK_HOME`).

## Acknowledgments

This is a solo project, but it was not built alone. The architecture, the doctrine, every design decision, and all hardware verification are mine — the force multiplier was AI pair-engineering: **Claude** (Anthropic) as the primary engineering collaborator across the kernel, the analyzer suite, and the specs, and **Antigravity** (Google) running code-generation work orders for the cmdlet surface. Nothing landed without being reviewed, corrected, and proven on a physical device. Something like this does not will itself into existence; it also does not direct itself.

## License

MIT — see [LICENSE](LICENSE).
