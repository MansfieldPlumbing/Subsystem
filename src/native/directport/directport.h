// --- directport.h ---
// DirectPort Interprocess GPU Memory Protocol
// Version 6.1 - Defect corrections: NT handle leak fix, wait function split,
//               HRESULT propagation, null checks, SDDL validation.
//
// VENDORED into the Subsystem work tree (src/native/directport) so ss.exe can build a
// thin directport.dll that exports the dp12_* C API (the canonical SDK statically links it
// into the VirtuaCam producer DLLs and exposes no standalone .dll). Canonical source:
// S:\virtuacam-claude\directport-sdk — kept pristine; the ONE local addition is
// dp12_get_adapter_luid (the broker filters producers by LUID and the C API otherwise
// hides the device). Fenced to one file per the ouroboros foreign-name rule.
#pragma once
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

#define DP_EXPORT __declspec(dllexport)

// Data Format Enum (Media Agnostic)
// Defines memory layout only. Semantic meaning is determined by the Producer/Consumer.
typedef enum {
    DP_FORMAT_VIDEO     = 0, // DXGI_FORMAT_B8G8R8A8_UNORM (4 bytes per unit)
    DP_FORMAT_FLOAT     = 1, // DXGI_FORMAT_R32_FLOAT      (4 bytes per unit)
    DP_FORMAT_HALF      = 2, // DXGI_FORMAT_R16_FLOAT      (2 bytes per unit)
    DP_FORMAT_RAW_32BIT = 3  // DXGI_FORMAT_R32_UINT       (4 bytes per unit)
} DP_FORMAT;

typedef void* DP_HANDLE;

// ============================================================================
// D3D12 API (Primary Implementation)
// ============================================================================

DP_EXPORT bool      dp12_init(void);
DP_EXPORT void      dp12_shutdown(void);

DP_EXPORT DP_HANDLE dp12_create_shared_resource(uint32_t width, uint32_t height, DP_FORMAT format, bool is_system_ram, const wchar_t* tex_name, const wchar_t* fence_name);
DP_EXPORT DP_HANDLE dp12_open_shared_resource(const wchar_t* tex_name, const wchar_t* fence_name);

DP_EXPORT void*     dp12_map_memory(DP_HANDLE handle, uint32_t* out_row_pitch);
DP_EXPORT void      dp12_unmap_memory(DP_HANDLE handle);

DP_EXPORT void      dp12_signal_fence(DP_HANDLE handle, uint64_t frame_value);
DP_EXPORT void      dp12_cpu_wait(DP_HANDLE handle, uint64_t target_value);
#ifdef __cplusplus
DP_EXPORT void      dp12_queue_wait(DP_HANDLE handle, struct ID3D12CommandQueue* pQueue, uint64_t target_value);
#endif
DP_EXPORT uint64_t  dp12_get_completed_value(DP_HANDLE handle);

DP_EXPORT void*     dp12_get_resource_handle(DP_HANDLE handle);
DP_EXPORT void*     dp12_get_fence_handle(DP_HANDLE handle);

// LOCAL ADDITION (not in the canonical SDK): returns the LUID of the adapter dp12_init
// created its device on, packed little-endian into an int64 (LowPart = low 32 bits). A
// producer writes this into BroadcastManifest.adapterLuid so the VirtuaCam broker accepts
// it (D3D shared textures cannot cross adapters — the broker rejects a mismatched LUID).
// Returns false if the subsystem is not initialized.
DP_EXPORT bool      dp12_get_adapter_luid(int64_t* out_luid);

// LOCAL ADDITION (diagnostic): the HRESULT of the last create_shared_resource failure (else S_OK).
DP_EXPORT int32_t   dp12_last_hresult(void);

// LOCAL ADDITION (diagnostic): adapter UMA flag (gates the is_system_ram CPU-texture path).
DP_EXPORT bool      dp12_is_uma(void);

// LOCAL ADDITION: upload a CPU BGRA frame into the (is_system_ram=false) shared VRAM texture via an
// UPLOAD buffer + copy, then signal the shared fence with frame_value. The discrete-GPU producer path.
DP_EXPORT bool      dp12_upload_bgra(DP_HANDLE handle, const void* src, uint32_t src_row_pitch,
                                     uint32_t width, uint32_t height, uint64_t frame_value);

DP_EXPORT void      dp12_close(DP_HANDLE handle);

// ============================================================================
// D3D11 API (D3D11 Compatibility Layer) — declared for parity; not built into this DLL.
// ============================================================================

DP_EXPORT bool      dp11_init(void);
DP_EXPORT void      dp11_shutdown(void);
DP_EXPORT DP_HANDLE dp11_create_shared_resource(uint32_t width, uint32_t height, DP_FORMAT format, const wchar_t* tex_name, const wchar_t* fence_name);
DP_EXPORT DP_HANDLE dp11_open_shared_resource(const wchar_t* tex_name, const wchar_t* fence_name);
DP_EXPORT void      dp11_signal_fence(DP_HANDLE handle, uint64_t frame_value);
DP_EXPORT void      dp11_wait_fence(DP_HANDLE handle, uint64_t target_value);
DP_EXPORT void      dp11_close(DP_HANDLE handle);

#ifdef __cplusplus
}
#endif
