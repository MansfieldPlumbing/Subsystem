# DirectPort Adapter Protocol

## Blueprint v1.0

---

## The Core Principle

DirectPort is a push architecture. It owns memory. It owns fences. Everything else borrows.

Every external interface — Media Foundation, ONNX Runtime, OBS, Spout, WASAPI, Vulkan — has its own memory model, synchronization contract, and execution trigger. None of them match DirectPort's push model natively. That mismatch is the impedance problem. The adapter's only job is to absorb it at the boundary, invisibly, without touching the transport layer underneath.

\[DirectPort Producer\]

        |

        | NT Handle \+ Fence

        v

\[ Adapter \]   \<-- lives here. absorbs the impedance.

        |

        | whatever the consumer expects

        v

\[External Interface\]

The transport primitive does not change. The adapter changes.

---

## The Adapter Contract

Every adapter implements the same five-phase lifecycle regardless of what it wraps:

1\. DISCOVER   — find or receive the DirectPort manifest

2\. OPEN       — resolve NT handles into local device objects

3\. SYNC       — wait on the DirectPort fence (GPU-side preferred, CPU fallback)

4\. PRESENT    — deliver the frame in whatever form the consumer requires

5\. RELEASE    — close handles, decrement NT reference counts

The shape of each phase differs per adapter. The sequence never does.

---

## Phase Specifications

### Phase 1 — DISCOVER

The adapter must locate a `BroadcastManifest` from the NT object namespace.

struct BroadcastManifest {

    UINT64      frameValue;       // Current fence signal value

    UINT        width;

    UINT        height;

    DXGI\_FORMAT format;

    LUID        adapterLuid;      // Physical adapter identity

    WCHAR       textureName\[256\]; // NT object name for the texture handle

    WCHAR       fenceName\[256\];   // NT object name for the fence handle

};

Discovery mechanism: `OpenFileMappingW` on a known name pattern. For process-scoped producers the pattern is: `DirectPort_Producer_Manifest_<PID>`

For named global producers (Broker output, named services): `Global\<ServiceName>_Manifest`

The adapter must validate `adapterLuid` matches the consuming device. Cross-adapter transport requires `D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER` on the resource and a staging copy — this is an exceptional case, not the default path.

---

### Phase 2 — OPEN

Resolve the manifest's NT names into live device objects on the consuming device. This is the D3D11/D3D12 API boundary crossing.

**D3D11 consumers:**

D3D11 cannot resolve NT handle strings directly. Spin up a headless D3D12 device as a resolver — this is the same pattern used in `directportd3d11.cpp`:

// Open the NT handle via D3D12 resolver

ID3D12Device\* resolver;

D3D12CreateDevice(nullptr, D3D\_FEATURE\_LEVEL\_11\_0, IID\_PPV\_ARGS(\&resolver));

HANDLE hTex, hFence;

resolver-\>OpenSharedHandleByName(manifest.textureName, GENERIC\_ALL, \&hTex);

resolver-\>OpenSharedHandleByName(manifest.fenceName,   GENERIC\_ALL, \&hFence);

// Import into D3D11 consuming device

device1-\>OpenSharedResource1(hTex,   IID\_PPV\_ARGS(\&sharedTexture));

device5-\>OpenSharedFence(hFence,     IID\_PPV\_ARGS(\&sharedFence));

**D3D12 consumers:** Call `OpenSharedHandleByName` directly on the consuming device — no resolver needed.

**ONNX consumers (ORT 1.24+):**

OrtExternalMemoryDescriptor mem\_desc{};

mem\_desc.handle\_type   \= ORT\_EXTERNAL\_MEMORY\_HANDLE\_TYPE\_D3D12\_RESOURCE;

mem\_desc.native\_handle \= hTex;

mem\_desc.size\_bytes    \= bufferSize;

interopApi-\>ImportMemory(importer, \&mem\_desc, \&memHandle);

interopApi-\>ImportSemaphore(importer, \&fence\_desc, \&semHandle);

Handle ownership: the adapter owns all open handles. It is responsible for calling `CloseHandle` on all NT handles during RELEASE. Failure to do so leaks VRAM until reboot on VF partitions.

---

### Phase 3 — SYNC

Wait on the DirectPort fence before reading the shared resource. There are two modes. Use GPU-side whenever possible.

**GPU-side (preferred — 170ns PCIe crossbar latency):**

// D3D11

context4-\>Wait(sharedFence.Get(), manifest.frameValue);

// D3D12

commandQueue-\>Wait(d3d12Fence.Get(), manifest.frameValue);

// ONNX (ORT 1.24)

interopApi-\>WaitSemaphore(importer, semHandle, stream, frameValue);

CPU returns immediately. The command queue stalls internally at the hardware level. No OS scheduler involvement.

**CPU-side (use only for readback or final output):**

if (sharedFence-\>GetCompletedValue() \< targetValue) {

    sharedFence-\>SetEventOnCompletion(targetValue, hEvent);

    WaitForSingleObject(hEvent, INFINITE);

}

Incurs 1–15.6ms OS scheduler quantum. Never use inside an inference loop.

---

### Phase 4 — PRESENT

Deliver the frame in the form the consumer requires. This is the impedance-specific logic. Every adapter is different here. The examples below are extracted from working implementations.

See the adapter reference implementations below.

---

### Phase 5 — RELEASE

// Close NT handles in this order: fence first, texture second

if (hSharedFence) { CloseHandle(hSharedFence); hSharedFence \= nullptr; }

if (hSharedTex)   { CloseHandle(hSharedTex);   hSharedTex   \= nullptr; }

// Release D3D objects

sharedFence.Reset();

sharedTexture.Reset();

// Unmap manifest

if (pManifestView) { UnmapViewOfFile(pManifestView); pManifestView \= nullptr; }

if (hManifest)     { CloseHandle(hManifest);         hManifest     \= nullptr; }

The NT Object Manager decrements reference counts on `CloseHandle`. VRAM is freed when the count reaches zero across all processes. If a process exits without calling RELEASE, the kernel reclaims handles automatically — but explicit release is required for long-running daemonized deployments to prevent VRAM fragmentation on VF partitions.

---

## Adapter Reference Implementations

### Adapter 1 — Media Foundation (Pull-to-Push)

**Impedance:** Media Foundation is pull-based. The pipeline calls `RequestSample` → adapter must return an `IMFSample` synchronously. DirectPort is push-based. The adapter must absorb the pull trigger and return the most recent pushed frame.

**Source:** `BrokerClient.cpp` / `VirtuaCam.cpp`

**OPEN:**

// BrokerClient::FindAndConnectToBroker

HANDLE hManifest \= OpenFileMappingW(FILE\_MAP\_READ, FALSE, BROKER\_MANIFEST\_NAME);

BroadcastManifest\* pView \= MapViewOfFile(hManifest, FILE\_MAP\_READ, ...);

// Resolve via D3D12 (D3D11 lacks OpenSharedHandleByName)

HANDLE hTex   \= GetHandleFromName(pView-\>textureName);

HANDLE hFence \= GetHandleFromName(pView-\>fenceName);

device1-\>OpenSharedResource1(hTex,   IID\_PPV\_ARGS(\&sharedTexture));

device5-\>OpenSharedFence(hFence,     IID\_PPV\_ARGS(\&sharedFence));

**SYNC:**

// BrokerClient::Generate

UINT64 latestFrame \= pManifestView-\>frameValue;

if (latestFrame \> lastSeenFrame) {

    context4-\>Wait(sharedFence.Get(), latestFrame);  // GPU-side

    context-\>CopyResource(privateTexture.Get(), sharedTexture.Get());

    lastSeenFrame \= latestFrame;

}

**PRESENT:**

// Blit into MF output texture, then wrap in IMFSample

MFCreateDXGISurfaceBuffer(\_\_uuidof(ID3D11Texture2D), outputTexture, 0, 0, \&buffer);

sample-\>AddBuffer(buffer);

// If consumer wants NV12, run through VideoProcessorMFT converter

**Key insight:** The `CopyResource` into a private texture is necessary here because `IMFSample` must own its buffer — the shared texture cannot be handed directly to MF. This is the unavoidable impedance cost of the MF pull model. The GPU-side fence wait ensures the copy is never reading mid-write.

---

### Adapter 2 — ONNX Runtime / DirectML (Zero-Copy Inference)

**Impedance:** ONNX Runtime owns its memory arena by default. The adapter must force ONNX to operate on DirectPort-owned VRAM instead, eliminating the PCIe round-trip that would otherwise occur every inference call.

**Source:** `FaceSwap.cpp` / `OnnxRuntime.cpp` / `OnnxInterop.h`

**OPEN (ORT 1.24 path):**

// Import NT handle as ONNX external memory

OrtExternalMemoryDescriptor mem\_desc{};

mem\_desc.version       \= ORT\_API\_VERSION;

mem\_desc.handle\_type   \= ORT\_EXTERNAL\_MEMORY\_HANDLE\_TYPE\_D3D12\_RESOURCE;

mem\_desc.native\_handle \= hSharedTex;

mem\_desc.size\_bytes    \= bufferSize;

interopApi-\>ImportMemory(importer, \&mem\_desc, \&memHandle);

// Wrap as tensor — ONNX now writes to DirectPort VRAM directly

OrtExternalTensorDescriptor tensor\_desc{};

tensor\_desc.element\_type \= ONNX\_TENSOR\_ELEMENT\_DATA\_TYPE\_FLOAT;

tensor\_desc.shape        \= shape.data();

tensor\_desc.rank         \= shape.size();

interopApi-\>CreateTensorFromMemory(importer, memHandle, \&tensor\_desc, \&outTensor);

**SYNC (GPU-side via ORT semaphore):**

// Tell ONNX to wait on the DirectPort fence before computing

interopApi-\>WaitSemaphore(importer, inputSemHandle, stream, frameValue);

// Submit inference — returns immediately, GPU waits internally

session-\>Run(runOptions, inputNames, inputs, 2, outputNames, \&outputTensor, 1);

// Tell ONNX to signal the output fence when done

interopApi-\>SignalSemaphore(importer, outputSemHandle, stream, outputFrameValue);

**Legacy path (pre-ORT 1.24):**

// D3D12 copy to private buffer, then wrap

// This is what FaceSwap.cpp currently does — the ABI index \[178\] approach

// is fragile; prefer the ORT 1.24 public API when available

auto tensor \= Ort::Value::CreateTensor(memory\_info, resource,

    elementCount, shape.data(), shape.size(), type);

**Key insight:** The zero-copy path eliminates the staging buffer entirely. ONNX computes in-place on the DirectPort allocation. The output tensor points to the same physical VRAM address the next consumer in the chain will read from. The fence chain is: Producer signals → ONNX WaitSemaphore → ONNX computes → ONNX SignalSemaphore → Consumer WaitSemaphore. No CPU involvement after submission.

---

### Adapter 3 — OBS Plugin (GPU Texture Source)

**Impedance:** OBS has its own graphics subsystem (`libobs-d3d11`). It manages its own D3D11 device. The adapter must import the DirectPort texture into OBS's device context and present it as an `obs_source_t` texture each frame.

**OPEN:**

// OBS plugin init — get OBS's D3D11 device

gs\_device\_t\* gs\_device \= obs\_get\_graphics();

ID3D11Device\* obsDevice \= gs\_get\_d3d11\_device(gs\_device);

ID3D11Device1\* obsDevice1;

obsDevice-\>QueryInterface(IID\_PPV\_ARGS(\&obsDevice1));

// Resolve manifest (same pattern as all adapters)

HANDLE hManifest \= OpenFileMappingW(FILE\_MAP\_READ, FALSE, manifestName);

BroadcastManifest\* pView \= MapViewOfFile(hManifest, FILE\_MAP\_READ, ...);

// Open shared texture into OBS device

HANDLE hTex \= GetHandleFromName(pView-\>textureName); // D3D12 resolver

ID3D11Texture2D\* sharedTex;

obsDevice1-\>OpenSharedResource1(hTex, IID\_PPV\_ARGS(\&sharedTex));

// Wrap for OBS rendering

gs\_texture\_t\* obsTexture \= gs\_texture\_create\_from\_d3d11\_tex(sharedTex);

**SYNC \+ PRESENT:**

// Called each frame from obs\_source\_video\_render

UINT64 latestFrame \= pView-\>frameValue;

if (latestFrame \> lastSeenFrame) {

    ID3D11DeviceContext4\* ctx4;

    obsContext-\>QueryInterface(IID\_PPV\_ARGS(\&ctx4));

    ctx4-\>Wait(sharedFence.Get(), latestFrame);  // GPU-side

    lastSeenFrame \= latestFrame;

}

gs\_effect\_t\* effect \= obs\_get\_base\_effect(OBS\_EFFECT\_DEFAULT);

gs\_technique\_begin(gs\_effect\_get\_technique(effect, "Draw"));

obs\_source\_draw(obsTexture, 0, 0, 0, 0, false);

gs\_technique\_end();

**Key insight:** OBS's `gs_texture_create_from_d3d11_tex` wraps an existing D3D11 texture in OBS's texture handle without copying. The shared texture lives in VRAM once. OBS renders directly from it. This eliminates the virtual camera path entirely — no MF pipeline, no NV12 conversion, no frame rate cap from the virtual camera driver.

---

### Adapter 4 — Spout-Compatible Layer

**Impedance:** Spout uses the older non-named DXGI shared handle path (`D3D11_RESOURCE_MISC_SHARED` without `SHARED_NTHANDLE`). It has no fence synchronization — consumers poll `IDXGIKeyedMutex`. The adapter must present a DirectPort stream as a Spout sender without the Spout library touching the underlying NT handle path.

**OPEN:**

// Create a Spout-format D3D11 texture as a staging surface

// (Spout cannot consume NT handles directly)

D3D11\_TEXTURE2D\_DESC spoutDesc{};

spoutDesc.MiscFlags \= D3D11\_RESOURCE\_MISC\_SHARED;  // legacy path, no NTHANDLE

device-\>CreateTexture2D(\&spoutDesc, nullptr, \&spoutStagingTex);

IDXGIKeyedMutex\* keyedMutex;

spoutStagingTex-\>QueryInterface(IID\_PPV\_ARGS(\&keyedMutex));

// Also open the DirectPort texture normally

device1-\>OpenSharedResource1(hDirectPortTex, IID\_PPV\_ARGS(\&directPortTex));

device5-\>OpenSharedFence(hFence, IID\_PPV\_ARGS(\&sharedFence));

**SYNC \+ PRESENT:**

// Wait on DirectPort fence (GPU-side)

context4-\>Wait(sharedFence.Get(), manifest.frameValue);

// Copy into Spout staging texture under keyed mutex

keyedMutex-\>AcquireSync(0, 16);

context-\>CopyResource(spoutStagingTex, directPortTex);

keyedMutex-\>ReleaseSync(1);

// Register with Spout sender — it now polls the staging texture

spoutSender.SendTexture(spoutStagingTex, width, height);

**Key insight:** This is the only adapter that requires a copy — the legacy Spout path has no fence mechanism, so the copy under `IDXGIKeyedMutex` is the synchronization primitive. The copy is unavoidable but it is one copy per frame at GPU speed, not a PCIe readback. The fence guarantees the copy only fires after the producer's write completes.

---

### Adapter 5 — WASAPI Audio (TTS Output)

**Impedance:** WASAPI is PCM audio — it expects CPU-accessible float buffers at a fixed sample rate and buffer size. DirectPort-TTS produces audio tokens on the GPU as part of an inference pipeline. The adapter must read the inference output buffer into CPU-accessible memory on the WASAPI callback schedule without stalling the inference pipeline.

**Source:** `DirectPort-TTS` / `WASAPIRenderer.cpp`

**OPEN:**

// Audio-specific: no texture, just a float buffer resource

// DirectPort-TTS creates: ID3D12Resource (CUSTOM heap, ROW\_MAJOR, is\_system\_ram=true)

// This allows dp12\_map\_memory to succeed

DP\_HANDLE audioPort \= dp12\_open\_shared\_resource(

    L"DirectPortTTS\_AudioBuffer",

    L"DirectPortTTS\_AudioFence"

);

uint32\_t rowPitch;

float\* mappedBuffer \= (float\*)dp12\_map\_memory(audioPort, \&rowPitch);

**SYNC \+ PRESENT:**

// WASAPI callback fires on hardware schedule (\~10ms intervals)

void OnAudioCallback(float\* pData, UINT32 numFrames) {

    uint64\_t latestToken \= dp12\_get\_completed\_value(audioPort);

    if (latestToken \> lastConsumedToken) {

        // CPU-side wait acceptable here — audio callback is already on a

        // dedicated thread, blocking is expected

        dp12\_cpu\_wait(audioPort, latestToken);

        memcpy(pData, mappedBuffer, numFrames \* sizeof(float) \* channels);

        lastConsumedToken \= latestToken;

    } else {

        // Inference hasn't produced new tokens — output silence or repeat

        memset(pData, 0, numFrames \* sizeof(float) \* channels);

    }

}

**Key insight:** Audio is the one domain where `dp12_cpu_wait` is the correct synchronization primitive. WASAPI callbacks run on a dedicated audio thread with a hard deadline — blocking is acceptable there. The `is_system_ram=true` flag on the DirectPort resource is what makes `dp12_map_memory` valid. All other adapters should use GPU-side waits.

---

## Adapter Decision Matrix

| Consumer Interface | Sync Mode | Copy Required | Key Constraint |
| :---- | :---- | :---- | :---- |
| Media Foundation | GPU `context4->Wait` | Yes — MF owns buffer | MF pull model forces staging copy |
| ONNX / DirectML | GPU semaphore (ORT 1.24) | No — zero-copy | Resource must outlive SignalSemaphore |
| OBS Plugin | GPU `context4->Wait` | No — `gs_texture_create_from_d3d11_tex` | Must use OBS device, not own device |
| Spout | GPU fence → keyed mutex copy | Yes — Spout has no fence | One copy at GPU speed, not PCIe readback |
| WASAPI | CPU `dp12_cpu_wait` | Yes — audio thread memcpy | `is_system_ram=true` required on resource |
| Vulkan (planned) | `VK_KHR_external_fence_win32` | No | Import NT fence as VkSemaphore |

---

## Security Descriptor

All adapters use the same SDDL string for NT handle creation:

ConvertStringSecurityDescriptorToSecurityDescriptorW(

    L"D:P(A;;GA;;;AU)",  // Grant ALL to Authenticated Users

    SDDL\_REVISION\_1, \&sd, NULL);

For production deployments requiring tighter access control, replace `AU` (Authenticated Users) with the specific SID of the consuming process. The `Number Parsimony` enforcement target in the DirectPort roadmap addresses this — binding shared handles to specific process security principals.

---

## Common Failure Modes

**VRAM leak on VF partition:** Cause: adapter exits without RELEASE phase. Fix: always call `CloseHandle` on both `hSharedTex` and `hSharedFence`.

**Stale frame on reconnect:** Cause: `lastSeenFrame` not reset to 0 after disconnect/reconnect. Fix: reset `lastSeenFrame = 0` in RELEASE, re-read `manifest.frameValue` in OPEN.

**Cross-adapter silent failure:** Cause: consuming device LUID does not match `manifest.adapterLuid`, but LUID check is disabled for robustness (as in `BrokerClient.cpp`). Fix: re-enable LUID check or explicitly handle cross-adapter copy path.

**NT resolver device leak:** Cause: headless D3D12 resolver device created per-frame in a hot path. Fix: create resolver device once at adapter init, reuse for lifetime. This is the `g_dp11_resolver12` pattern in `directportd3d11.cpp`.

**ONNX resource lifetime violation:** Cause: `ID3D12Resource` released before `SignalSemaphore` fires. Fix: hold `ComPtr` on the resource until the output fence value is confirmed via `dp12_get_completed_value`.  
