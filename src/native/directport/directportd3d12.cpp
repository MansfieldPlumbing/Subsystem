// --- directportd3d12.cpp ---
// DirectPort D3D12 Implementation
//
// VENDORED copy of S:\virtuacam-claude\directport-sdk\directportd3d12.cpp, built here as a
// standalone directport.dll for ss.exe to P/Invoke. The ONLY change from canonical is the
// added dp12_get_adapter_luid export (see directport.h). Keep this in sync with canonical.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <sddl.h>
#include "directport.h"

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "advapi32.lib")

extern "C" {

// Global Singleton State
// ARCHITECTURAL NOTE (v1.0 constraint): g_dp12_device is a single global adapter.
static ID3D12Device* g_dp12_device = NULL;
static HRESULT g_dp12_last_hr = S_OK;  // diagnostic: last failure HRESULT from create_shared_resource

typedef struct {
    ID3D12Resource* resource;
    ID3D12Fence* fence;
    HANDLE hSharedTex;
    HANDLE hSharedFence;
    HANDLE hEvent;
    // Lazy upload path (dp12_upload_bgra): CPU pixels -> UPLOAD buffer -> CopyTextureRegion into the
    // DEFAULT/VRAM shared texture. The only way to feed a shared texture on a discrete (non-UMA) GPU,
    // where a CPU-mappable texture is invalid.
    ID3D12CommandQueue*        upQueue;
    ID3D12CommandAllocator*    upAlloc;
    ID3D12GraphicsCommandList* upList;
    ID3D12Resource*            upBuffer;
    UINT64                     upBufferSize;
} DP12_State;

static DXGI_FORMAT GetDXGIFormat(DP_FORMAT fmt) {
    switch(fmt) {
        case DP_FORMAT_VIDEO:       return DXGI_FORMAT_B8G8R8A8_UNORM;
        case DP_FORMAT_FLOAT:       return DXGI_FORMAT_R32_FLOAT;
        case DP_FORMAT_HALF:        return DXGI_FORMAT_R16_FLOAT;
        case DP_FORMAT_RAW_32BIT:   return DXGI_FORMAT_R32_UINT;
        default: return DXGI_FORMAT_UNKNOWN;
    }
}

DP_EXPORT bool dp12_init(void) {
    if (g_dp12_device) return true; // Already initialized
    if (FAILED(D3D12CreateDevice(NULL, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_dp12_device)))) {
        return false;
    }
    return true;
}

DP_EXPORT void dp12_shutdown(void) {
    if (g_dp12_device) {
        g_dp12_device->Release();
        g_dp12_device = NULL;
    }
}

DP_EXPORT DP_HANDLE dp12_create_shared_resource(uint32_t width, uint32_t height, DP_FORMAT format, bool is_system_ram, const wchar_t* tex_name, const wchar_t* fence_name) {
    if (!g_dp12_device) return NULL;

    DP12_State* state = (DP12_State*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(DP12_State));
    if (!state) return NULL;

    D3D12_RESOURCE_DESC desc = {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width;
    desc.Height = height;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = GetDXGIFormat(format);
    desc.SampleDesc.Count = 1;
    // D3D11-openable flags: RENDER_TARGET (+ implicit shader-resource for the broker's sampler). UAV is
    // dropped — B8G8R8A8_UNORM + UAV is not broadly openable by the D3D11 consumer's OpenSharedResource1,
    // which leaves the broker unable to connect (NO SIGNAL). The producer writes via CopyTextureRegion,
    // so it needs no UAV.
    desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;

    D3D12_HEAP_PROPERTIES props = {};
    if (is_system_ram) {
        props.Type = D3D12_HEAP_TYPE_CUSTOM;
        props.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY_WRITE_COMBINE;
        props.MemoryPoolPreference = D3D12_MEMORY_POOL_L0;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        desc.Flags |= D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER;
    } else {
        props.Type = D3D12_HEAP_TYPE_DEFAULT;
        desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    }

    // Both ternary branches must be the enum type (not a bare 0) or the result degrades to int,
    // which MSVC 14.51's stricter conformance won't convert back to D3D12_HEAP_FLAGS. NONE is the
    // typed zero. (Canonical built under looser conformance; this is the only vendor-build fixup.)
    D3D12_HEAP_FLAGS heapFlags = D3D12_HEAP_FLAG_SHARED |
        (is_system_ram ? D3D12_HEAP_FLAG_SHARED_CROSS_ADAPTER : D3D12_HEAP_FLAG_NONE);

    HRESULT hr = g_dp12_device->CreateCommittedResource(
        &props,
        heapFlags,
        &desc,
        D3D12_RESOURCE_STATE_COMMON,
        NULL,
        IID_PPV_ARGS(&state->resource));
    if (FAILED(hr)) {
        g_dp12_last_hr = hr;
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    PSECURITY_DESCRIPTOR sd = NULL;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;AU)", SDDL_REVISION_1, &sd, NULL)) {
        state->resource->Release();
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }
    SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };

    // Same-adapter producer->broker uses a plain shared fence. CROSS_ADAPTER (needed only for the UMA
    // is_system_ram cross-adapter path) can make the D3D11 broker's OpenSharedFence reject it.
    D3D12_FENCE_FLAGS fenceFlags = D3D12_FENCE_FLAG_SHARED;
    if (is_system_ram) fenceFlags |= D3D12_FENCE_FLAG_SHARED_CROSS_ADAPTER;
    hr = g_dp12_device->CreateFence(0, fenceFlags, IID_PPV_ARGS(&state->fence));
    if (FAILED(hr)) {
        LocalFree(sd);
        state->resource->Release();
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    hr = g_dp12_device->CreateSharedHandle(state->resource, &sa, GENERIC_ALL, tex_name, &state->hSharedTex);
    if (FAILED(hr)) {
        LocalFree(sd);
        state->fence->Release();
        state->resource->Release();
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    hr = g_dp12_device->CreateSharedHandle(state->fence, &sa, GENERIC_ALL, fence_name, &state->hSharedFence);
    if (FAILED(hr)) {
        LocalFree(sd);
        CloseHandle(state->hSharedTex);
        state->fence->Release();
        state->resource->Release();
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    LocalFree(sd);
    state->hEvent = CreateEvent(NULL, FALSE, FALSE, NULL);
    return state;
}

DP_EXPORT DP_HANDLE dp12_open_shared_resource(const wchar_t* tex_name, const wchar_t* fence_name) {
    if (!g_dp12_device) return NULL;

    DP12_State* state = (DP12_State*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(DP12_State));
    if (!state) return NULL;

    HRESULT hr = g_dp12_device->OpenSharedHandleByName(tex_name, GENERIC_ALL, &state->hSharedTex);
    if (FAILED(hr) || state->hSharedTex == NULL) {
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    hr = g_dp12_device->OpenSharedHandle(state->hSharedTex, IID_PPV_ARGS(&state->resource));
    if (FAILED(hr)) {
        CloseHandle(state->hSharedTex);
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    hr = g_dp12_device->OpenSharedHandleByName(fence_name, GENERIC_ALL, &state->hSharedFence);
    if (FAILED(hr) || state->hSharedFence == NULL) {
        state->resource->Release();
        CloseHandle(state->hSharedTex);
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    hr = g_dp12_device->OpenSharedHandle(state->hSharedFence, IID_PPV_ARGS(&state->fence));
    if (FAILED(hr)) {
        CloseHandle(state->hSharedFence);
        state->resource->Release();
        CloseHandle(state->hSharedTex);
        HeapFree(GetProcessHeap(), 0, state);
        return NULL;
    }

    state->hEvent = CreateEvent(NULL, FALSE, FALSE, NULL);
    return state;
}

DP_EXPORT void* dp12_map_memory(DP_HANDLE handle, uint32_t* out_row_pitch) {
    DP12_State* state = (DP12_State*)handle;
    void* ptr = NULL;
    state->resource->Map(0, NULL, &ptr);
    if (out_row_pitch) {
        D3D12_RESOURCE_DESC desc = state->resource->GetDesc();
        uint32_t bpp = (desc.Format == DXGI_FORMAT_R16_FLOAT) ? 2 : 4;
        *out_row_pitch = (uint32_t)((desc.Width * bpp + 255) & ~255);
    }
    return ptr;
}

DP_EXPORT void dp12_unmap_memory(DP_HANDLE handle) {
    ((DP12_State*)handle)->resource->Unmap(0, NULL);
}

DP_EXPORT void dp12_signal_fence(DP_HANDLE handle, uint64_t frame_value) {
    ((DP12_State*)handle)->fence->Signal(frame_value);
}

DP_EXPORT void dp12_cpu_wait(DP_HANDLE handle, uint64_t target_value) {
    DP12_State* state = (DP12_State*)handle;
    if (state->fence->GetCompletedValue() < target_value) {
        state->fence->SetEventOnCompletion(target_value, state->hEvent);
        WaitForSingleObject(state->hEvent, INFINITE);
    }
}

DP_EXPORT void dp12_queue_wait(DP_HANDLE handle, ID3D12CommandQueue* pQueue, uint64_t target_value) {
    DP12_State* state = (DP12_State*)handle;
    pQueue->Wait(state->fence, target_value);
}

DP_EXPORT uint64_t dp12_get_completed_value(DP_HANDLE handle) {
    return ((DP12_State*)handle)->fence->GetCompletedValue();
}

DP_EXPORT void* dp12_get_resource_handle(DP_HANDLE handle) {
    return ((DP12_State*)handle)->hSharedTex;
}

DP_EXPORT void* dp12_get_fence_handle(DP_HANDLE handle) {
    return ((DP12_State*)handle)->hSharedFence;
}

// LOCAL ADDITION: expose the device's adapter LUID so a producer can populate the
// BroadcastManifest's same-GPU filter. ID3D12Device::GetAdapterLuid() returns it directly;
// LUID is 8 bytes (DWORD LowPart + LONG HighPart) reinterpreted little-endian into int64.
DP_EXPORT int32_t dp12_last_hresult(void) { return (int32_t)g_dp12_last_hr; }

// LOCAL ADDITION (diagnostic): does the adapter report UMA? On a non-UMA (discrete) GPU a CPU-visible
// (L0) TEXTURE is invalid — the is_system_ram=true path only works on UMA/integrated or server cards.
DP_EXPORT bool dp12_is_uma(void) {
    if (!g_dp12_device) return false;
    D3D12_FEATURE_DATA_ARCHITECTURE arch = {};
    arch.NodeIndex = 0;
    if (FAILED(g_dp12_device->CheckFeatureSupport(D3D12_FEATURE_ARCHITECTURE, &arch, sizeof(arch)))) return false;
    return arch.UMA != FALSE;
}

DP_EXPORT bool dp12_get_adapter_luid(int64_t* out_luid) {
    if (!g_dp12_device || !out_luid) return false;
    LUID luid = g_dp12_device->GetAdapterLuid();
    *out_luid = *reinterpret_cast<int64_t*>(&luid);
    return true;
}

// LOCAL ADDITION: upload a CPU BGRA image into the shared DEFAULT texture and signal the shared fence on
// the copy queue (GPU-ordered) so the broker sees frame_value. CPU-waits the copy so the single upload
// buffer is safe to reuse next frame. Returns false on any failure. This is the discrete-GPU producer
// path (is_system_ram=false); on UMA the is_system_ram CPU-map path is the faster alternative.
DP_EXPORT bool dp12_upload_bgra(DP_HANDLE handle, const void* src, uint32_t src_row_pitch,
                                uint32_t width, uint32_t height, uint64_t frame_value) {
    DP12_State* s = (DP12_State*)handle;
    if (!s || !s->resource || !src || !g_dp12_device) return false;

    D3D12_RESOURCE_DESC td = s->resource->GetDesc();
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = {};
    UINT   numRows = 0;
    UINT64 rowBytes = 0, total = 0;
    g_dp12_device->GetCopyableFootprints(&td, 0, 1, 0, &fp, &numRows, &rowBytes, &total);

    if (!s->upQueue) {
        D3D12_COMMAND_QUEUE_DESC qd = {}; qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (FAILED(g_dp12_device->CreateCommandQueue(&qd, IID_PPV_ARGS(&s->upQueue)))) return false;
        if (FAILED(g_dp12_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&s->upAlloc)))) return false;
        if (FAILED(g_dp12_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, s->upAlloc, NULL, IID_PPV_ARGS(&s->upList)))) return false;
        s->upList->Close();
    }
    if (!s->upBuffer || s->upBufferSize < total) {
        if (s->upBuffer) { s->upBuffer->Release(); s->upBuffer = NULL; }
        D3D12_HEAP_PROPERTIES up = {}; up.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC bd = {};
        bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = total; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
        bd.Format = DXGI_FORMAT_UNKNOWN; bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        if (FAILED(g_dp12_device->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
                D3D12_RESOURCE_STATE_GENERIC_READ, NULL, IID_PPV_ARGS(&s->upBuffer)))) return false;
        s->upBufferSize = total;
    }

    // Copy CPU rows into the upload buffer at the footprint's 256-aligned row pitch.
    BYTE* dst = NULL; D3D12_RANGE noRead = { 0, 0 };
    if (FAILED(s->upBuffer->Map(0, &noRead, (void**)&dst))) return false;
    const BYTE* srcb = (const BYTE*)src;
    UINT copyRow = (UINT)((src_row_pitch < rowBytes) ? src_row_pitch : rowBytes);
    for (UINT y = 0; y < height && y < numRows; y++)
        memcpy(dst + fp.Offset + (SIZE_T)y * fp.Footprint.RowPitch, srcb + (SIZE_T)y * src_row_pitch, copyRow);
    s->upBuffer->Unmap(0, NULL);

    // Record: COMMON -> COPY_DEST, copy, COPY_DEST -> COMMON (shared resources sync in COMMON).
    s->upAlloc->Reset();
    s->upList->Reset(s->upAlloc, NULL);
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = s->resource; b.Transition.Subresource = 0;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_DEST;
    s->upList->ResourceBarrier(1, &b);

    D3D12_TEXTURE_COPY_LOCATION dstLoc = {};
    dstLoc.pResource = s->resource; dstLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dstLoc.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
    srcLoc.pResource = s->upBuffer; srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; srcLoc.PlacedFootprint = fp;
    s->upList->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, NULL);

    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COMMON;
    s->upList->ResourceBarrier(1, &b);
    s->upList->Close();

    ID3D12CommandList* lists[] = { s->upList };
    s->upQueue->ExecuteCommandLists(1, lists);
    s->upQueue->Signal(s->fence, frame_value);   // broker waits on this; GPU-ordered after the copy
    if (s->fence->GetCompletedValue() < frame_value) {
        s->fence->SetEventOnCompletion(frame_value, s->hEvent);
        WaitForSingleObject(s->hEvent, INFINITE);
    }
    return true;
}

DP_EXPORT void dp12_close(DP_HANDLE handle) {
    DP12_State* state = (DP12_State*)handle;
    if (state->upBuffer) state->upBuffer->Release();
    if (state->upList)   state->upList->Release();
    if (state->upAlloc)  state->upAlloc->Release();
    if (state->upQueue)  state->upQueue->Release();
    if (state->hEvent)       CloseHandle(state->hEvent);
    if (state->hSharedFence) CloseHandle(state->hSharedFence);
    if (state->hSharedTex)   CloseHandle(state->hSharedTex);
    if (state->fence)        state->fence->Release();
    if (state->resource)     state->resource->Release();
    HeapFree(GetProcessHeap(), 0, state);
}

} // extern "C"
