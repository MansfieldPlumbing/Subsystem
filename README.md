# Subsystem

**A subsystem** — self-hosting and GC-free: an NT-Object-Manager-shaped runtime where memory, code, threads, and devices are *one namespace of refcounted handles* (the VOM), and **one transport (DirectPort)** pushes fenced, zero-copy regions across every boundary: process · GPU↔NPU · language · machine. It builds itself, owns its own memory, and reclaims deterministically (free-on-zero / cascade-kill).

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

---

## DirectPort Memory & Pipeline Architecture Specification

**Version:** 1.0  
**Status:** Canonical Reference Specification  
**Scope:** Subsystem Runspace, DirectPort IPC, Intra-Process Virtual Handle Sharing, and Compiler Enforcement  

---

### 1. Executive Summary & Design Philosophy

The **DirectPort Architecture** is a deterministic, zero-copy, hardware-synchronized memory and execution pipeline for real-time data streaming (video frames, multi-channel audio, and $N$-dimensional AI/ML tensors).

DirectPort is built on three uncompromising principles:

1. **Total Runspace Memory Accounting:** Every byte of allocated stack, VRAM, and shared buffer space is explicitly owned and tracked. Runtime schedulers, hidden compiler state machines (`async`/`await`), and `ThreadPool` continuations are forbidden within owned runspace components.
2. **Real-Thread Execution:** Every pipeline node owns its dedicated OS thread (`Thread`). Thread ownership never yields to ambient task schedulers.
3. **The Fence IS the Clock:** Temporal ordering and pipeline synchronization are governed by hardware timeline fences (`ID3D12Fence` / `ID3D11Fence` / `VkSemaphore`) and atomic memory futexes (`WaitOnAddress` / `WakeByAddressAll` / `futex`).

---

### 2. Domain Equivalence Scope (Tensors $\equiv$ Frames $\equiv$ Samples)

DirectPort establishes a strict **Domain Equivalence Principle** ("Tensor-as-a-Frame"):

* $N$-dimensional AI/ML model tensors, multi-channel audio PCM sample blocks, and raw video frames are treated **identically** by the storage and pipeline transport layers.
* All data payloads flow through the same $64\text{B}/256\text{B}$ row-major mailbox pipeline model without domain-specific transport wrappers or stream-type branch logic.

---

### 3. The Unified Dual-Role Node Paradigm

In DirectPort, there is no static dichotomy between "Producer" and "Consumer" binaries. Every processing node in a DirectPort pipeline is a **symmetric execution unit** that transitions sequentially through a 4-phase loop on its dedicated thread.

#### Node Resource Anatomy

Every Node $N$ owns and encapsulates six discrete hardware handles initialized upfront:

1. **Dedicated OS Thread (`Thread`):** Drives the loop continuously. Banned from yielding to `async`/`await` state machines or `ThreadPool` schedulers (`SS018`).
2. **Private Scratch Buffer ($B_{\text{scratch}}$):** Private VRAM allocation. Holds working data, intermediate compute state, and UAV/RTV barriers. Completely hidden from other nodes.
3. **Shared Export Buffer ($B_{\text{export}}$):** Cross-process / intra-process shared VRAM (`D3D12_HEAP_FLAG_SHARED` / `MISC_SHARED_NTHANDLE`). Exposes completed output payloads.
4. **Egress Timeline Fence ($F_{\text{egress}}$):** Hardware timeline fence owned by Node $N$, incrementing monotonically ($v_{\text{egress}} = 1, 2, 3, \dots$).
5. **Egress Manifest ($M_{\text{egress}}$):** Shared memory control header containing sequence value $v_{\text{egress}}$, $64\text{B}/256\text{B}$ stride metadata, LUID, and shared handle names.
6. **Ingress Import References:** Handles to Previous Node $(N-1)$'s Shared Export Buffer ($B_{\text{prev\_export}}$) and Egress Fence ($F_{\text{prev\_egress}}$).

#### Comprehensive Loop Execution Sequence (Iteration $K$)

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│                         NODE N: CONTINUOUS LOOP ITERATION K                      │
└──────────────────────────────────────────────────────────────────────────────────┘
                                         │
 ┌───────────────────────────────────────┴───────────────────────────────────────┐
 │ STEP 1: INGRESS BLIT (Upstream Shared Export ──> Private Scratch Buffer)     │
 │ • Read upstream manifest frameValue (v_prev) non-blockingly.                  │
 │ • IF v_prev > v_seen:                                                         │
 │     - Enqueue GPU queue wait on F_prev_egress at value v_prev.                │
 │     - Enqueue GPU CopyResource: B_prev_export  ──>  B_scratch.               │
 │     - Update v_seen = v_prev.                                                 │
 │ • ELSE:                                                                       │
 │     - Reuse existing payload in B_scratch (or proceed with default state).     │
 └───────────────────────────────────────┬───────────────────────────────────────┘
                                         │
 ┌───────────────────────────────────────┴───────────────────────────────────────┐
 │ STEP 2: WORKLOAD EXECUTION (Kernel / Compute Pass)                            │
 │ • Dispatch compute shaders, AI/ML tensor passes, or render pipelines.         │
 │ • All compute reads/writes execute strictly inside B_scratch.                 │
 │ • Intermediate UAV barriers & atomics are 100% isolated inside B_scratch.    │
 └───────────────────────────────────────┬───────────────────────────────────────┘
                                         │
 ┌───────────────────────────────────────┴───────────────────────────────────────┐
 │ STEP 3: EGRESS BLIT (Private Scratch Buffer ──> Shared Export Buffer)        │
 │ • Enqueue GPU CopyResource: B_scratch  ──>  B_export.                         │
 │ • Enforce 64B/256B row-major alignment stride.                                │
 │ • Truncate trailing alignment bytes / padding uint32s at the boundary.       │
 └───────────────────────────────────────┬───────────────────────────────────────┘
                                         │
 ┌───────────────────────────────────────┴───────────────────────────────────────┐
 │ STEP 4: EGRESS SIGNAL & FUTEX NOTIFICATION                                    │
 │ • Enqueue GPU Signal on F_egress with value K.                                │
 │ • Atomic update: M_egress->frameValue = K via InterlockedExchange64.          │
 │ • Call WakeByAddressAll(&M_egress->frameValue) to alert CPU waiters.          │
 │ • FIRE-AND-FORGET: Node N NEVER checks if downstream nodes are listening.     │
 └───────────────────────────────────────┬───────────────────────────────────────┘
                                         │
 ┌───────────────────────────────────────┴───────────────────────────────────────┐
 │ STEP 5: IMMEDIATE RE-ENTRY                                                    │
 │ • Increment iteration counter K = K + 1.                                      │
 │ • Immediately loop back to STEP 1 for the next payload pass.                 │
 └───────────────────────────────────────────────────────────────────────────────┘
```

#### Detailed Step-by-Step Mechanics

1. **Step 1: Ingress Blit (Non-Blocking Ingest):**
   * Node $N$ reads $M_{\text{prev\_egress}}\rightarrow\text{frameValue}$ non-blockingly.
   * If a new upstream sequence value $v_{\text{prev}} > v_{\text{seen}}$ is detected, Node $N$ enqueues a GPU queue wait:
     $$\text{Queue}\rightarrow\text{Wait}(F_{\text{prev\_egress}}, v_{\text{prev}})$$
   * Node $N$ enqueues a GPU-side copy to blit from $B_{\text{prev\_export}}$ directly into its Private Scratch Buffer $B_{\text{scratch}}$.
   * **No Backpressure:** If Node $(N-1)$ is faster than Node $N$, Node $N$ skips unread intermediate sequence numbers ($v_{\text{prev}} = 10, 11, 12 \rightarrow$ ingests $13$). Node $(N-1)$ is never blocked.

2. **Step 2: Workload Execution (Isolated Compute Pass):**
   * Node $N$ executes compute dispatches, AI/ML tensor operations, or processing shaders targeting $B_{\text{scratch}}$.
   * Intermediate UAV barriers, scratch memory writes, and thread group synchronizations remain 100% isolated inside $B_{\text{scratch}}$.

3. **Step 3: Egress Blit (Format & Alignment Staging):**
   * Node $N$ enqueues a GPU-side copy from $B_{\text{scratch}}$ into $B_{\text{export}}$ (`CopyResource` / `CopyBufferRegion`).
   * $64\text{B}/256\text{B}$ row-major stride rules are enforced, and any trailing alignment bytes or tail control `uint32`s are truncated at the boundary.

4. **Step 4: Egress Signal & Futex Notification:**
   * Node $N$ enqueues a GPU signal command on its egress fence:
     $$\text{Queue}\rightarrow\text{Signal}(F_{\text{egress}}, K)$$
   * Node $N$ updates its shared manifest counter:
     $$\text{InterlockedExchange64}(\&M_{\text{egress}}\rightarrow\text{frameValue}, K)$$
   * Node $N$ calls `WakeByAddressAll` to notify optional CPU listeners.
   * **Fire-and-Forget Invariant:** Node $N$ does not check or wait for downstream consumers.

5. **Step 5: Immediate Re-Entry:**
   * Node $N$ increments iteration counter $K = K + 1$ and immediately loops back to Step 1.

#### Architectural Stability Guarantees

1. **Hazard-Free Memory Access:** Downstream nodes reading $B_{\text{export}}$ do so only after Node $N$'s GPU copy completes (fence $K$). Node $N$ works inside $B_{\text{scratch}}$, preventing mid-read corruption.
2. **Zero Lock Contention & Zero Deadlocks:** No node waits for downstream nodes. Data dependencies flow strictly unidirectionally.
3. **Deterministic Threading & Allocation (`SS018` Enforcement):** Memory is allocated once at startup. Dedicated OS threads execute continuously with zero dynamic heap allocations, zero GC pressure, and zero scheduler yields.

---

### 4. Dual Transport Architecture: Inter-Process (IPC) vs. Intra-Process

DirectPort exposes a single, transport-agnostic interface that adapts based on node process boundaries.

```
                             DIRECTPORT INTERFACE
                                      │
            ┌─────────────────────────┴─────────────────────────┐
            ▼                                                   ▼
   INTER-PROCESS MODE (IPC)                           INTRA-PROCESS MODE
┌────────────────────────────────┐                 ┌────────────────────────────────┐
│ • OS Shared File Mapping       │                 │ • Virtual Handle / Raw Pointer │
│   (SharedBufferManifest)       │                 │   Ref Sharing                  │
│ • Named Windows NT Handles     │                 │ • Direct VRAM Pointer Pass     │
│   (CreateSharedHandle)         │                 │ • Zero OS Handle Overhead      │
│ • Cross-Process Fences         │                 │ • Same Fence & Ingress/Egress  │
│   (ID3D12Fence / ID3D11Fence)  │                 │   Semantics                    │
└────────────────────────────────┘                 └────────────────────────────────┘
```

#### 1. Inter-Process Communication Mode (IPC)
* **Control Plane:** Shared memory file mappings (`CreateFileMappingW` / `SharedBufferManifest`) advertise buffer byte size, dimensions, adapter LUID, sequence values, and NT handle names.
* **Data Plane:** Named Windows NT shared handles (`CreateSharedHandle` / `OpenSharedHandleByName`) share GPU memory allocations across process boundaries.
* **Synchronization:** Cross-process shared fences (`D3D12_FENCE_FLAG_SHARED`) synchronize GPU queues across PIDs.

#### 2. Intra-Process Sharing Mode (Virtual Handles)
* **Control Plane:** Manifest headers pass directly via in-memory struct pointers or virtual handle abstractions without OS kernel file mapping overhead.
* **Data Plane:** Resource handles alias directly to VRAM pointer addresses (`ID3D12Resource*` / `ID3D11Buffer*`), bypassing `CreateSharedHandle` calls.
* **Synchronization:** GPU queues synchronize via lightweight local fences or direct GPU-side command ordering.
* **Invariant:** Node business logic, memory isolation rules, and `Ingress -> Workload -> Egress` phases remain identical regardless of transport mode.

---

### 5. Cross-Platform HAL Mapping Matrix (Windows $\leftrightarrow$ Android)

| Abstraction | Windows (D3D11 / D3D12 / Win32) | Android (Vulkan / NDK / POSIX) |
| --- | --- | --- |
| **Shared VRAM Allocation** | `ID3D12Resource` (`D3D12_HEAP_FLAG_SHARED`) / `ID3D11Buffer` | `VkImage` / `VkBuffer` + `AHardwareBuffer` |
| **Cross-Process Handle** | Named NT Handle (`CreateSharedHandle` / `OpenSharedHandleByName`) | File Descriptor (`AHardwareBuffer_sendHandleToUnixSocket` / `opaque_fd`) |
| **Control Manifest** | Windows Shared File Mapping (`CreateFileMappingW` / `MapViewOfFile`) | Anonymous Shared Memory (`ashmem` / `memfd_create` + `mmap`) |
| **Timeline Synchronization** | `ID3D12Fence` / `ID3D11Fence` (`D3D12_FENCE_FLAG_SHARED`) | Vulkan Timeline Semaphore (`VkSemaphore` / `VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO`) |
| **CPU Futex Signaling** | `WaitOnAddress` / `WakeByAddressAll` | POSIX `futex()` (`FUTEX_WAIT_PRIVATE` / `FUTEX_WAKE_PRIVATE`) |

---

### 6. Memory Layout, Alignment & Tail Truncation Standard

All DirectPort buffers follow a strict memory geometry:

```
+-----------------------------------------------------------------------------------+
| Row / Tensor Slice 0: Contiguous Elements                 | 64B / 256B Alignment  |
+-----------------------------------------------------------------------------------+
| Row / Tensor Slice 1: Contiguous Elements                 | 64B / 256B Alignment  |
+-----------------------------------------------------------------------------------+
| ...                                                                               |
+-----------------------------------------------------------------------------------+
| Final Row: Dense Elements [Tail uint32 / Control Padding Stripped at Boundary]    |
+-----------------------------------------------------------------------------------+
```

#### Alignment Constraints
1. **64-Byte Alignment (CPU & SIMD Optimization):** All host-visible buffer offsets, row starts, and structure strides align to 64-byte boundaries (CPU cache line & AVX-512 register width).
2. **256-Byte Alignment (GPU Hardware Stride):** All subresource pitches and row-major texture/tensor strides align to 256-byte multiples (DirectX 11/12 and Vulkan DMA pitch requirements).

#### Row-Major Packing & Tail Truncation
* **Row-Major Layout:** Multi-dimensional arrays (tensors, audio frames, image rows) are densely packed in row-major order ($[N, C, H, W]$ or $[T, C]$).
* **Tail Truncation ("Cutting the Tail `uint32`"):** Trailing control words, status flags, CRC checksums, or alignment padding bytes (e.g., tail `uint32` counters) are stripped at output boundaries. Compute dispatches clamp writes (`if (idx >= valid_count) return;`) or issue exact byte-range copies (`CopyBufferRegion`) so export buffers contain no trailing garbage or unmapped slack.

---

### 7. Synchronization & Decoupled Clock Model

1. **Fire-and-Forget Egress (Best-Effort Push):** Producers never wait for consumers. Once the egress copy finishes, the producer updates the manifest counter, signals its fence, and immediately begins its next iteration.
2. **Non-Blocking Ingress (Latest-Frame Access):** Consumers inspect `frameValue` non-blockingly. If a consumer falls behind, it skips intermediate sequence numbers and ingests the latest frame value without backpressure or pipeline stalls.
3. **GPU Timeline Synchronization (`Wait`):** When a consumer ingests a new sequence value, it enqueues a GPU queue wait (`g_commandQueue->Wait(sharedFence, frameValue)`). The CPU thread is never blocked during GPU queue waits.
4. **Futex Park/Wake:** When a CPU thread must wait for an ingress update, it parks directly on the manifest address via OS futexes (`WaitOnAddress` / `futex`). The producer wakes parked threads via `WakeByAddressAll` / `FUTEX_WAKE_PRIVATE`.

---

### 8. Compiler Enforcement & Governance (Roslyn `SS018`)

To preserve memory accounting and prevent `async`/`await` state machine pollution, the Subsystem compiler gate strictly enforces this architecture via **`SS018FenceClockRunspaceAnalyzer`**:

* **Rule ID:** `SS018`
* **Rule Name:** `SS018FenceClockRunspaceAnalyzer`
* **Enforcement Scope:** Fail-closed across all owned runspace code.
* **Prohibited Constructs in Owned Code:**
  * `async` keyword on methods, local functions, or lambdas.
  * `await` expressions.
  * Sync-over-async invocations (`Task.Wait()`, `Task.Result`, `.GetAwaiter().GetResult()`).
  * `ThreadPool` delegation (`Task.Run`, `Task.Factory.StartNew`, `ThreadPool.QueueUserWorkItem`, `ThreadPool.UnsafeQueueUserWorkItem`).
* **Permitted Exemptions:**
  1. Generated code (`obj/` directories).
  2. Declared host seam boundaries (`(host)` entries in `SystemCatalog.json`).

---

### 9. Canonical Architecture Summary

| Property | Canonical Specification |
| --- | --- |
| **Execution Unit** | Unified Dual-Role Node (`Ingress` $\rightarrow$ `Workload` $\rightarrow$ `Egress`) |
| **Thread Model** | Real OS Thread per Node (Dedicated `Thread`, No `ThreadPool`, No `async`/`await`) |
| **Domain Scope** | Domain Equivalence (Tensors $\equiv$ Frames $\equiv$ Samples) |
| **Memory Isolation** | Private Scratch Buffer (Working Pass) $\rightarrow$ Shared Export Buffer (Blit Pass) |
| **Transport Modes** | Inter-Process (Shared Memory + NT Handles) & Intra-Process (Virtual Handle Aliasing) |
| **Cross-Platform HAL** | Direct3D 11/12 + Win32 (Windows) $\leftrightarrow$ Vulkan + `AHardwareBuffer` + `ashmem` + POSIX futex (Android) |
| **Memory Alignment** | 64-Byte (Cache/SIMD) / 256-Byte (GPU Pitch) Row-Major Packing |
| **Tail Geometry** | Explicit Tail Truncation (Stripping alignment padding / trailing `uint32`) |
| **Clock Source** | Timeline Fence (`ID3D12Fence` / `VkSemaphore`) + Atomic Manifest Futex |
| **Stream Coupling** | Decoupled, Non-Blocking, Latest-Frame Ingestion (No Producer Backpressure) |
| **Compiler Gate** | `SS018FenceClockRunspaceAnalyzer` (Fail-Closed) |

---

## Building

An Android (.NET) app: .NET 11 preview SDK + **JDK 21** (the LiteRT-LM AAR is Java-21 bytecode), targeting `net11.0-android`. Build on physical hardware — emulators are a dead end for the CoreCLR + PowerShell path. The native shims (`libpsl-*.so`) are built with `src/runspace/native/build-native.ps1` (set `SS_NDK` or `ANDROID_NDK_HOME`).

## Acknowledgments

This is a solo project, but it was not built alone. The architecture, the doctrine, every design decision, and all hardware verification are mine — the force multiplier was AI pair-engineering: **Claude** (Anthropic) as the primary engineering collaborator across the kernel, the analyzer suite, and the specs, and **Antigravity** (Google) running code-generation work orders for the cmdlet surface. Nothing landed without being reviewed, corrected, and proven on a physical device. Something like this does not will itself into existence; it also does not direct itself.

## License

MIT — see [LICENSE](LICENSE).
