// ggml-d3d12-shared.h
// Shared types for the D3D12 inference pipeline.
// Mirrors BroadcastManifest/ProducerConnection from VirtuaCam,
// adapted for compute tensor boundaries instead of video frames.
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <stdint.h>

using Microsoft::WRL::ComPtr;

// ---------------------------------------------------------------------------
// TensorManifest
// Written by producer, read by consumer. Lives in named shared memory.
// Mirrors BroadcastManifest from Utilities.h — same InterlockedExchange64
// pattern on tokenValue so consumer sees a consistent snapshot.
// ---------------------------------------------------------------------------
struct TensorManifest {
    volatile uint64_t tokenValue;   // incremented per inference step, read by consumer
    uint32_t          width;        // tensor layout: cols  (e.g. hidden_dim)
    uint32_t          height;       // tensor layout: rows  (e.g. seq_len)
    DXGI_FORMAT       format;       // DP_FORMAT_HALF (R16_FLOAT) for activations
    LUID              adapterLuid;  // producer's adapter — consumer validates match
    WCHAR             texName[256]; // NT name of the shared boundary texture
    WCHAR             fenceName[256];
};

// ---------------------------------------------------------------------------
// DiePort  (replaces ProducerConnection from VirtuaCam)
// One instance per upstream die connection. Owned by the consuming DieWorker.
// ---------------------------------------------------------------------------
struct DiePort {
    bool                    isConnected   = false;
    uint64_t                lastSeenToken = 0;

    // Shared resources opened from upstream die's NT names
    ComPtr<ID3D12Resource>  sharedTex;       // upstream boundary texture (read-only)
    ComPtr<ID3D12Fence>     sharedFence;     // upstream fence
    HANDLE                  hSharedTex    = nullptr;
    HANDLE                  hSharedFence  = nullptr;

    // Private copy — compute reads from here, never from the shared tex directly
    ComPtr<ID3D12Resource>  privateTex;

    // Manifest
    HANDLE                  hManifest     = nullptr;
    TensorManifest*         pManifest     = nullptr;
};

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------
// DXGI adapter indices on Scott's ThinkStation P520:
//   0 = Quadro P2000 (display, skip)
//   1 = V340 die 0   \
//   2 = V340 die 1    |  target
//   3 = V340 die 2    |
//   4 = V340 die 3   /
static constexpr int   kDieCount          = 4;
static constexpr int   kDieAdapterBase    = 1;   // skip adapter 0 (P2000)
static constexpr UINT  kComputeThreads    = 512; // matches HLSL [numthreads(512,1,1)]
static constexpr WCHAR kManifestPrefix[]  = L"Global\\DirectPort_InferenceDie_Manifest_";

#define CEIL_DIV(a,b) (((a)+(b)-1)/(b))
