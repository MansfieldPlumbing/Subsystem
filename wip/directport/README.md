# DirectPort — the canonical reference design

> The one source of truth. If a question about DirectPort can't be answered from this folder,
> the folder is wrong — fix it here, never re-explain it elsewhere.

## What it is (one breath)
A **push** IPC transport. It **owns the memory and the fences**; everything else borrows. The
primitive is an **NT shared handle + a value-based fence (timeline)** — Cutler's object manager
meets a plumbing **Port**. It is the VOM at GPU / cross-process scope: producer writes a
256-aligned shared texture/buffer, signals the fence, consumers `Wait` on the fence value. Zero
copy same-adapter, no OS scheduler in the hot path. The wait latency is UNMEASURED — no receipt
exists; a sub-microsecond hardware-crossbar wakeup is the theoretical ideal, never observed on
this hardware. Do not cite a number until a bench prints one. (A "~170 ns" figure circulated in
older copies of this doc; it was aspiration, not measurement — retired 2026-07-02, Scott.)

## The contract (read the spec)
- **`DirectPort_Adapter_Protocol.md`** — THE spec. The `BroadcastManifest` (the descriptor in the
  NT namespace) + the **5-phase adapter lifecycle** every consumer follows:
  **DISCOVER → OPEN → SYNC → PRESENT → RELEASE**.
- **The adapter absorbs the impedance.** Every foreign interface is pull/owns-its-own-memory; the
  adapter converts at the boundary so the transport never changes. Reference adapters in the spec:
  **Media Foundation** (pull→push), **ONNX/DirectML** (zero-copy inference), **OBS**, **Spout**,
  **WASAPI** (audio), **Vulkan**.

## Naming (Cutler + plumbing — never collides with `engine`/`surface`)
| Concept | Name |
|---|---|
| the transport / primitive | **DirectPort** / **Port** (NT LPC + plumbing) |
| shared-region descriptor | **BroadcastManifest** |
| producer/consumer VOM handle type | **Broadcast** (NEVER `Surface` — collides with Android) |
| the broker (1 source → many) | **Manifold** |
| the impedance absorber | **Adapter** (a pipe fitting) |

## Layout
```
DirectPort\              the canonical reference implementation (full)
  DirectPort\            core: DirectPort.cpp/.h, Manifest — the transport
  DirectPort\ (adapters) DirectPortONNX (inference), DirectPortCamera (MediaFoundation),
                         DirectPortGL (OpenGL), DirectPortNumpy (python) + *Wrapper.cpp
  Examples\              D3D11 + D3D12: Producer / Consumer / Multiplexer / ShaderFilter /
                         ShaderProducer / BufferToTexture / TextureToBuffer
  Vulkan\                directport.android.vulkan.cpp + DirectPortVk.cs — the Android/Vulkan port
                         (mechanical D3D12→Vulkan: VkImage + timeline semaphore + opaque fd / SCM_RIGHTS)
  Scripts\               python IPC/onnx/gl/numpy tests
DirectPort-SDK\          the LEAN core (directport.h + d3d11 + d3d12). Make this repo PRIVATE.
DirectPort_Adapter_Protocol.md   the spec
```

## Backends — one model, mechanical translation
| | Windows | Android |
|---|---|---|
| device | D3D11 / D3D12 | Vulkan |
| shared mem | NT shared handle | opaque Unix fd (SCM_RIGHTS) |
| fence | `ID3D12Fence` (value) | `VkSemaphore` timeline (KHR) |
| GPU wait | `queue->Wait(fence, val)` | `vkQueueSubmit` timeline wait |

## Its role in the system
DirectPort is **PHASE 1 — the spine** (`integration-order-2026-06-17`): the ONE primitive that
VirtuaCam, the TUI/DWM present-surface, the compute-mesh (onnx/QNN compute leaves), and the
agent-browser frame all ride. Producers/consumers mount as VOM `Broadcast` handles; the **Manifold**
(ss tray-broker) discovers producers and supervises DirectPort leaf processes (reap by fence /
`is_alive` → no zombies).

## Repos of origin (collapse these — this folder supersedes them)
`DirectPort` ← MansfieldPlumbing/DirectPort-Legacy · `DirectPort-SDK` ← MansfieldPlumbing/DirectPort-SDK.
Working copies with extras NOT yet folded: `C:\dev\DirectPort-main` (DirectPortCore.cpp 84 KB,
DirectPortCapture.cpp), `S:\reference\ipc-test` (renderer.cpp working test),
`S:\reference\directport-sdk\wren\specs\wren_manifest.h` (manifest spec).
