// DieWorker.cpp
// Per-die D3D12 compute worker implementation.
// Template: Consumer.cpp / PipelineNode from VirtuaCam.
// Transport: DirectPort NT handle sharing + dp12_queue_wait for GPU-side sync.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <sddl.h>
#include <format>
#include <filesystem>
#include "DieWorker.h"
#include "directport.h"

// Pull in ggml op enums — adjust path to match your llama.cpp location
#include "C:/BUILD/llama.cpp/ggml/include/ggml.h"

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "advapi32.lib")

using Microsoft::WRL::ComPtr;

// ---------------------------------------------------------------------------
// Push constant structs (from d3d12_dispatch_reference.h)
// SetComputeRoot32BitConstants(0, sizeof(pc)/4, &pc, 0)
// ---------------------------------------------------------------------------
struct PC_Unary {
    uint32_t ne;
    uint32_t ne00, ne01, ne02, ne03, nb00, nb01, nb02, nb03;
    uint32_t ne10, ne11, ne12, ne13, nb10, nb11, nb12, nb13;
    uint32_t misalign_offsets;
    float    param1, param2;
    uint32_t pad[12]; // fastdiv fields — zeroed
};

struct PC_Binary {
    uint32_t ne;
    uint32_t ne00, ne01, ne02, ne03, nb00, nb01, nb02, nb03;
    uint32_t ne10, ne11, ne12, ne13, nb10, nb11, nb12, nb13;
    uint32_t ne20, ne21, ne22, ne23, nb20, nb21, nb22, nb23;
    uint32_t misalign_offsets;
    float    param1, param2;
    int32_t  param3;
};

struct PC_GLU {
    uint32_t N;
    uint32_t ne00, ne20, mode;
    float    alpha, limit;
    uint32_t nb01, nb02, nb03;
    uint32_t ne01, ne02;
    uint32_t nb11, nb12, nb13;
    uint32_t ne11, ne12;
};

// ---------------------------------------------------------------------------
// DXIL loader — reads a pre-compiled .dxil blob from disk
// ---------------------------------------------------------------------------
static std::vector<uint8_t> LoadDXIL(const char* name) {
    auto path = std::filesystem::path("C:/BUILD/native_hlsl") / (std::string(name) + ".dxil");
    HANDLE hFile = CreateFileA(path.string().c_str(), GENERIC_READ, FILE_SHARE_READ,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) return {};
    DWORD size = GetFileSize(hFile, nullptr);
    std::vector<uint8_t> blob(size);
    DWORD read = 0;
    ReadFile(hFile, blob.data(), size, &read, nullptr);
    CloseHandle(hFile);
    return blob;
}

// ---------------------------------------------------------------------------
// Root signature builder
// All shaders share the same layout:
//   b0 = root constants (up to 32 DWORDs)
//   t0 = SRV (src)
//   u0 = UAV (dst)
// ---------------------------------------------------------------------------
static HRESULT CreateComputeRootSig(ID3D12Device* device, ID3D12RootSignature** ppSig) {
    D3D12_DESCRIPTOR_RANGE srvRange = { D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 4, 0, 0, D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND };
    D3D12_DESCRIPTOR_RANGE uavRange = { D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 4, 0, 0, D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND };

    D3D12_ROOT_PARAMETER params[3] = {};
    // [0] Root constants — push constants
    params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    params[0].Constants.ShaderRegister = 0;
    params[0].Constants.RegisterSpace  = 0;
    params[0].Constants.Num32BitValues = 32; // max push constant size
    params[0].ShaderVisibility         = D3D12_SHADER_VISIBILITY_ALL;
    // [1] SRV table (t0..t3)
    params[1].ParameterType                       = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[1].DescriptorTable.NumDescriptorRanges = 1;
    params[1].DescriptorTable.pDescriptorRanges   = &srvRange;
    params[1].ShaderVisibility                    = D3D12_SHADER_VISIBILITY_ALL;
    // [2] UAV table (u0..u3)
    params[2].ParameterType                       = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[2].DescriptorTable.NumDescriptorRanges = 1;
    params[2].DescriptorTable.pDescriptorRanges   = &uavRange;
    params[2].ShaderVisibility                    = D3D12_SHADER_VISIBILITY_ALL;

    D3D12_ROOT_SIGNATURE_DESC desc = {};
    desc.NumParameters = 3;
    desc.pParameters   = params;
    desc.Flags         = D3D12_ROOT_SIGNATURE_FLAG_NONE;

    ComPtr<ID3DBlob> blob, err;
    HRESULT hr = D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err);
    if (FAILED(hr)) return hr;
    return device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(),
                                       IID_PPV_ARGS(ppSig));
}

// ---------------------------------------------------------------------------
// DieWorker
// ---------------------------------------------------------------------------
DieWorker::DieWorker(int die_index, int cpu_core, const wchar_t* upstream_manifest)
    : m_dieIndex(die_index)
    , m_cpuCore(cpu_core)
    , m_upstreamManifestName(upstream_manifest ? upstream_manifest : L"")
{
    // Each die publishes its manifest under a well-known name
    // Downstream die opens this by name — same auto-discovery as VirtuaCam Multiplexer
    m_manifestName = std::wstring(kManifestPrefix) + std::to_wstring(die_index);
}

DieWorker::~DieWorker() {
    StopWorker();

    // Mirror PipelineNode::ShutdownSharing / DisconnectFromProducer
    if (m_upstream.pManifest)  UnmapViewOfFile(m_upstream.pManifest);
    if (m_upstream.hManifest)  CloseHandle(m_upstream.hManifest);
    if (m_upstream.hSharedTex) CloseHandle(m_upstream.hSharedTex);
    if (m_upstream.hSharedFence) CloseHandle(m_upstream.hSharedFence);

    if (m_pManifestOut)    UnmapViewOfFile(m_pManifestOut);
    if (m_hManifestOut)    CloseHandle(m_hManifestOut);
    if (m_hBoundaryTex)    CloseHandle(m_hBoundaryTex);
    if (m_hBoundaryFence)  CloseHandle(m_hBoundaryFence);
    if (m_hKickEvent)      CloseHandle(m_hKickEvent);
}

HRESULT DieWorker::Initialize(uint32_t tensor_width, uint32_t tensor_height) {
    HRESULT hr;
    hr = InitD3D12();       if (FAILED(hr)) return hr;
    hr = LoadPSOs();        if (FAILED(hr)) return hr;

    if (!m_upstreamManifestName.empty()) {
        hr = ConnectToUpstream(); if (FAILED(hr)) return hr;
    } else {
        // Die 0: no upstream die, gets kicked by host via event
        m_hKickEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    }

    hr = InitBoundaryOutput(tensor_width, tensor_height); if (FAILED(hr)) return hr;
    PublishManifest(tensor_width, tensor_height);
    return S_OK;
}

// ---------------------------------------------------------------------------
// InitD3D12 — mirrors PipelineNode::InitD3D11 but targets a specific adapter
// The v1.0 constraint fix: each die gets its own device on its own adapter.
// ---------------------------------------------------------------------------
HRESULT DieWorker::InitD3D12() {
    ComPtr<IDXGIFactory6> factory;
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return hr;

    // Enumerate to our target adapter — skip adapter 0 (P2000)
    int targetIndex = kDieAdapterBase + m_dieIndex;
    hr = factory->EnumAdapters1(targetIndex, &m_adapter);
    if (FAILED(hr)) return hr;

    DXGI_ADAPTER_DESC1 desc;
    m_adapter->GetDesc1(&desc);
    m_adapterLuid = desc.AdapterLuid;

    hr = D3D12CreateDevice(m_adapter.Get(), D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(&m_device));
    if (FAILED(hr)) return hr;

    // Compute-only queue — no graphics needed
    D3D12_COMMAND_QUEUE_DESC qDesc = {};
    qDesc.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    hr = m_device->CreateCommandQueue(&qDesc, IID_PPV_ARGS(&m_computeQueue));
    if (FAILED(hr)) return hr;

    hr = m_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&m_cmdAlloc));
    if (FAILED(hr)) return hr;

    hr = m_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE,
                                     m_cmdAlloc.Get(), nullptr, IID_PPV_ARGS(&m_cmdList));
    if (FAILED(hr)) return hr;
    m_cmdList->Close(); // start closed, reset before each use

    // Descriptor heap for SRV/UAV bindings
    D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
    heapDesc.Type           = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    heapDesc.NumDescriptors = 1024;
    heapDesc.Flags          = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    hr = m_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&m_uavHeap));
    return hr;
}

// ---------------------------------------------------------------------------
// LoadPSOs — load every DXIL blob from C:\BUILD\native_hlsl at startup.
// All command lists are pre-recorded against these PSOs.
// ---------------------------------------------------------------------------
HRESULT DieWorker::LoadPSOs() {
    // Shared root signature for all compute shaders
    ComPtr<ID3D12RootSignature> rootSig;
    HRESULT hr = CreateComputeRootSig(m_device.Get(), &rootSig);
    if (FAILED(hr)) return hr;

    // List of all compiled DXIL shaders
    const char* shaders[] = {
        "silu", "gelu", "gelu_erf", "gelu_quick", "relu", "tanh", "sigmoid",
        "hardsigmoid", "hardswish", "exp", "neg", "abs", "softplus",
        "step", "round", "ceil", "floor", "trunc", "sgn", "xielu", "elu",
        "swiglu", "swiglu_oai", "geglu", "geglu_erf", "geglu_quick", "reglu",
        "norm", "group_norm",
        "copy", "contig_copy", "repeat", "roll", "pad", "diag", "diag_mask_inf",
        "log", "arange", "fill", "upscale", "tri",
        "im2col_3d", "count_experts",
        "mul_mat_split_k_reduce", "flash_attn_split_k_reduce", "ssm_conv",
        "dequant_q4_0",  // native rewrite — already compiled
        // TODO: dequant_q8_0, dequant_q4_1, dequant_q5_0, dequant_q5_1
    };

    for (const char* name : shaders) {
        auto blob = LoadDXIL(name);
        if (blob.empty()) continue; // skip missing — will assert at dispatch if needed

        D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature     = rootSig.Get();
        psoDesc.CS.pShaderBytecode = blob.data();
        psoDesc.CS.BytecodeLength  = blob.size();

        PsoEntry entry;
        entry.rootSig = rootSig;
        hr = m_device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&entry.pso));
        if (SUCCEEDED(hr)) {
            m_psoCache[name] = std::move(entry);
        }
    }
    return S_OK;
}

// ---------------------------------------------------------------------------
// ConnectToUpstream — mirrors PipelineNode::ConnectToProducer
// Opens the upstream die's shared texture and fence by NT name.
// Uses the device's OpenSharedHandleByName (the v1.0 fix — per-device, not global).
// ---------------------------------------------------------------------------
HRESULT DieWorker::ConnectToUpstream() {
    const wchar_t* manifestName = m_upstreamManifestName.c_str();

    HANDLE hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, manifestName);
    if (!hMap) return E_FAIL;

    TensorManifest* pMani = (TensorManifest*)MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, sizeof(TensorManifest));
    if (!pMani) { CloseHandle(hMap); return E_FAIL; }

    // Validate we're on the same adapter
    if (memcmp(&pMani->adapterLuid, &m_adapterLuid, sizeof(LUID)) != 0) {
        // Cross-adapter: allowed — the Switchtec fabric handles P2P
        // Don't fail, but note it for the cross-adapter heap flag
    }

    // Open texture NT handle by name using THIS die's device (v1.0 constraint fix)
    HRESULT hr = m_device->OpenSharedHandleByName(pMani->texName, GENERIC_ALL, &m_upstream.hSharedTex);
    if (FAILED(hr)) { UnmapViewOfFile(pMani); CloseHandle(hMap); return hr; }

    hr = m_device->OpenSharedHandle(m_upstream.hSharedTex, IID_PPV_ARGS(&m_upstream.sharedTex));
    if (FAILED(hr)) { CloseHandle(m_upstream.hSharedTex); UnmapViewOfFile(pMani); CloseHandle(hMap); return hr; }

    hr = m_device->OpenSharedHandleByName(pMani->fenceName, GENERIC_ALL, &m_upstream.hSharedFence);
    if (FAILED(hr)) { m_upstream.sharedTex.Reset(); CloseHandle(m_upstream.hSharedTex); UnmapViewOfFile(pMani); CloseHandle(hMap); return hr; }

    hr = m_device->OpenSharedHandle(m_upstream.hSharedFence, IID_PPV_ARGS(&m_upstream.sharedFence));
    if (FAILED(hr)) { CloseHandle(m_upstream.hSharedFence); m_upstream.sharedTex.Reset(); CloseHandle(m_upstream.hSharedTex); UnmapViewOfFile(pMani); CloseHandle(hMap); return hr; }

    // Private copy texture — compute reads from here, not directly from shared
    D3D12_RESOURCE_DESC rd = m_upstream.sharedTex->GetDesc();
    D3D12_HEAP_PROPERTIES hp = { D3D12_HEAP_TYPE_DEFAULT };
    m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
                                      D3D12_RESOURCE_STATE_COMMON, nullptr,
                                      IID_PPV_ARGS(&m_upstream.privateTex));

    m_upstream.hManifest  = hMap;
    m_upstream.pManifest  = pMani;
    m_upstream.isConnected = true;
    return S_OK;
}

// ---------------------------------------------------------------------------
// InitBoundaryOutput — mirrors PipelineNode::InitializeSharing
// Creates this die's output texture published downstream.
// Uses THIS die's device directly (bypasses DirectPort singleton — v1.0 fix).
// ---------------------------------------------------------------------------
HRESULT DieWorker::InitBoundaryOutput(uint32_t width, uint32_t height) {
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension        = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    rd.Width            = width;
    rd.Height           = height;
    rd.DepthOrArraySize = 1;
    rd.MipLevels        = 1;
    rd.Format           = DXGI_FORMAT_R16_FLOAT; // DP_FORMAT_HALF — activations
    rd.SampleDesc.Count = 1;
    rd.Flags            = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS |
                          D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    rd.Layout           = D3D12_TEXTURE_LAYOUT_UNKNOWN;

    D3D12_HEAP_PROPERTIES hp = { D3D12_HEAP_TYPE_DEFAULT };

    HRESULT hr = m_device->CreateCommittedResource(
        &hp,
        D3D12_HEAP_FLAG_SHARED,
        &rd,
        D3D12_RESOURCE_STATE_COMMON,
        nullptr,
        IID_PPV_ARGS(&m_boundaryTex));
    if (FAILED(hr)) return hr;

    hr = m_device->CreateFence(0,
        D3D12_FENCE_FLAG_SHARED | D3D12_FENCE_FLAG_SHARED_CROSS_ADAPTER,
        IID_PPV_ARGS(&m_boundaryFence));
    if (FAILED(hr)) return hr;

    // NT handle names for downstream auto-discovery
    PSECURITY_DESCRIPTOR sd = nullptr;
    ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"D:P(A;;GA;;;AU)", SDDL_REVISION_1, &sd, nullptr);
    SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };

    std::wstring texName   = std::wstring(L"Global\\DirectPort_InferenceTex_")   + std::to_wstring(m_dieIndex);
    std::wstring fenceName = std::wstring(L"Global\\DirectPort_InferenceFence_") + std::to_wstring(m_dieIndex);

    m_device->CreateSharedHandle(m_boundaryTex.Get(),   &sa, GENERIC_ALL, texName.c_str(),   &m_hBoundaryTex);
    m_device->CreateSharedHandle(m_boundaryFence.Get(), &sa, GENERIC_ALL, fenceName.c_str(), &m_hBoundaryFence);
    LocalFree(sd);

    return S_OK;
}

// ---------------------------------------------------------------------------
// PublishManifest — writes the TensorManifest to named shared memory.
// Downstream die's ConnectToUpstream() opens this by m_manifestName.
// ---------------------------------------------------------------------------
void DieWorker::PublishManifest(uint32_t width, uint32_t height) {
    PSECURITY_DESCRIPTOR sd = nullptr;
    ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"D:P(A;;GA;;;AU)", SDDL_REVISION_1, &sd, nullptr);
    SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };

    m_hManifestOut = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE,
                                        0, sizeof(TensorManifest), m_manifestName.c_str());
    m_pManifestOut = (TensorManifest*)MapViewOfFile(m_hManifestOut, FILE_MAP_ALL_ACCESS,
                                                     0, 0, sizeof(TensorManifest));
    LocalFree(sd);
    if (!m_pManifestOut) return;

    ZeroMemory(m_pManifestOut, sizeof(TensorManifest));
    m_pManifestOut->width       = width;
    m_pManifestOut->height      = height;
    m_pManifestOut->format      = DXGI_FORMAT_R16_FLOAT;
    m_pManifestOut->adapterLuid = m_adapterLuid;

    std::wstring texName   = std::wstring(L"Global\\DirectPort_InferenceTex_")   + std::to_wstring(m_dieIndex);
    std::wstring fenceName = std::wstring(L"Global\\DirectPort_InferenceFence_") + std::to_wstring(m_dieIndex);
    wcscpy_s(m_pManifestOut->texName,   texName.c_str());
    wcscpy_s(m_pManifestOut->fenceName, fenceName.c_str());
}

void DieWorker::AssignNodes(ggml_cgraph* graph, int start_node, int end_node) {
    m_graph     = graph;
    m_nodeStart = start_node;
    m_nodeEnd   = end_node;
}

// ---------------------------------------------------------------------------
// StartWorker — spins the pinned thread
// ---------------------------------------------------------------------------
void DieWorker::StartWorker() {
    m_isRunning = true;
    m_thread = std::thread([this] {
        // Pin to assigned physical core
        SetThreadAffinityMask(GetCurrentThread(), 1ull << m_cpuCore);

        // Name the thread for PIX/RGP debugging
        std::wstring name = L"DieWorker_" + std::to_wstring(m_dieIndex);
        SetThreadDescription(GetCurrentThread(), name.c_str());

        WorkerThread();
    });
}

void DieWorker::StopWorker() {
    m_isRunning = false;
    if (m_hKickEvent) SetEvent(m_hKickEvent); // unblock die 0
    if (m_thread.joinable()) m_thread.join();
}

void DieWorker::KickInference(uint64_t token_index) {
    // Die 0 only: host kicks off a new token
    m_tokenValue = token_index;
    if (m_hKickEvent) SetEvent(m_hKickEvent);
}

// ---------------------------------------------------------------------------
// WorkerThread — THE HOT LOOP
// Mirrors BrokerThreadFunction from Broker.cpp and ProcessLoop from Consumer.cpp.
//
// Die 0:  WaitForSingleObject(kickEvent) → DispatchGraph → SignalFence
// Die N:  dp12_queue_wait → CopyTex → DispatchGraph → SignalFence
//
// dp12_queue_wait is GPU-side only — CPU returns immediately.
// The command queue stalls at hardware level until upstream fence fires.
// 170ns PCIe crossbar latency per the DirectPort architecture doc.
// ---------------------------------------------------------------------------
void DieWorker::WorkerThread() {
    while (m_isRunning) {
        if (m_upstream.isConnected) {
            // --- Consumer path (dies 1-3) ---
            // Poll for a new token from upstream (non-blocking read)
            uint64_t latestToken = m_upstream.pManifest->tokenValue;
            if (latestToken <= m_upstream.lastSeenToken) {
                // Nothing new yet — yield and check again
                // With pinned core + speculative prefetch this is ~ns loop
                _mm_pause();
                continue;
            }

            // GPU queue waits on upstream fence — CPU returns immediately
            // pQueue will stall at hardware level until fence value is met
            m_upstream.lastSeenToken = latestToken;
            m_computeQueue->Wait(m_upstream.sharedFence.Get(), latestToken);

            // Copy upstream boundary tex to private (same pattern as Consumer.cpp)
            m_cmdAlloc->Reset();
            m_cmdList->Reset(m_cmdAlloc.Get(), nullptr);

            // Transition shared → copy source
            D3D12_RESOURCE_BARRIER barriers[2] = {};
            barriers[0].Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barriers[0].Transition.pResource   = m_upstream.sharedTex.Get();
            barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
            barriers[0].Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_SOURCE;
            barriers[1]                        = barriers[0];
            barriers[1].Transition.pResource   = m_upstream.privateTex.Get();
            barriers[1].Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_DEST;
            m_cmdList->ResourceBarrier(2, barriers);

            m_cmdList->CopyResource(m_upstream.privateTex.Get(), m_upstream.sharedTex.Get());

            // Transition private back to UAV for compute
            barriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
            barriers[1].Transition.StateAfter  = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            m_cmdList->ResourceBarrier(1, &barriers[1]);

            DispatchGraph();

            m_cmdList->Close();
            ID3D12CommandList* lists[] = { m_cmdList.Get() };
            m_computeQueue->ExecuteCommandLists(1, lists);

        } else {
            // --- Die 0: wait for host kick ---
            WaitForSingleObject(m_hKickEvent, INFINITE);
            if (!m_isRunning) break;

            m_cmdAlloc->Reset();
            m_cmdList->Reset(m_cmdAlloc.Get(), nullptr);
            DispatchGraph();
            m_cmdList->Close();
            ID3D12CommandList* lists[] = { m_cmdList.Get() };
            m_computeQueue->ExecuteCommandLists(1, lists);
        }

        // --- Signal downstream (same as Broker.cpp InterlockedExchange64 pattern) ---
        m_tokenValue++;
        m_computeQueue->Signal(m_boundaryFence.Get(), m_tokenValue);
        if (m_pManifestOut) {
            InterlockedExchange64(
                reinterpret_cast<volatile LONGLONG*>(&m_pManifestOut->tokenValue),
                (LONGLONG)m_tokenValue);
        }
    }
}

// ---------------------------------------------------------------------------
// DispatchGraph — walks assigned graph nodes and dispatches each op
// ---------------------------------------------------------------------------
void DieWorker::DispatchGraph() {
    if (!m_graph) return;

    m_cmdList->SetDescriptorHeaps(1, m_uavHeap.GetAddressOf());

    for (int i = m_nodeStart; i < m_nodeEnd; i++) {
        ggml_tensor* node = m_graph->nodes[i];
        DispatchNode(node);
    }
}

void DieWorker::DispatchNode(ggml_tensor* node) {
    switch (node->op) {
    case GGML_OP_MUL_MAT:
        DispatchMulMat(node);
        break;

    case GGML_OP_UNARY: {
        const char* name = nullptr;
        switch (ggml_get_unary_op(node)) {
            case GGML_UNARY_OP_SILU:        name = "silu";        break;
            case GGML_UNARY_OP_GELU:        name = "gelu";        break;
            case GGML_UNARY_OP_GELU_ERF:    name = "gelu_erf";    break;
            case GGML_UNARY_OP_GELU_QUICK:  name = "gelu_quick";  break;
            case GGML_UNARY_OP_RELU:        name = "relu";        break;
            case GGML_UNARY_OP_TANH:        name = "tanh";        break;
            case GGML_UNARY_OP_SIGMOID:     name = "sigmoid";     break;
            case GGML_UNARY_OP_NEG:         name = "neg";         break;
            case GGML_UNARY_OP_ABS:         name = "abs";         break;
            case GGML_UNARY_OP_EXP:         name = "exp";         break;
            case GGML_UNARY_OP_SGN:         name = "sgn";         break;
            case GGML_UNARY_OP_CEIL:        name = "ceil";        break;
            case GGML_UNARY_OP_FLOOR:       name = "floor";       break;
            case GGML_UNARY_OP_ROUND:       name = "round";       break;
            case GGML_UNARY_OP_TRUNC:       name = "trunc";       break;
            case GGML_UNARY_OP_STEP:        name = "step";        break;
            case GGML_UNARY_OP_SOFTPLUS:    name = "softplus";    break;
            case GGML_UNARY_OP_HARDSIGMOID: name = "hardsigmoid"; break;
            case GGML_UNARY_OP_HARDSWISH:   name = "hardswish";   break;
            case GGML_UNARY_OP_XIELU:       name = "xielu";       break;
            default: return;
        }
        auto it = m_psoCache.find(name);
        if (it != m_psoCache.end()) DispatchUnary(node, it->second);
        break;
    }

    case GGML_OP_ADD:
    case GGML_OP_SUB:
    case GGML_OP_MUL:
    case GGML_OP_DIV: {
        // All binary ops use the same shader key pattern
        // TODO: map to correct binary shader name
        break;
    }

    case GGML_OP_GLU: {
        const char* name = nullptr;
        switch (ggml_get_glu_op(node)) {
            case GGML_GLU_OP_SWIGLU:       name = "swiglu";       break;
            case GGML_GLU_OP_SWIGLU_OAI:   name = "swiglu_oai";   break;
            case GGML_GLU_OP_GEGLU:        name = "geglu";        break;
            case GGML_GLU_OP_GEGLU_ERF:    name = "geglu_erf";    break;
            case GGML_GLU_OP_GEGLU_QUICK:  name = "geglu_quick";  break;
            case GGML_GLU_OP_REGLU:        name = "reglu";        break;
            default: return;
        }
        auto it = m_psoCache.find(name);
        if (it != m_psoCache.end()) DispatchGLU(node, it->second);
        break;
    }

    case GGML_OP_NORM: {
        auto it = m_psoCache.find("norm");
        if (it != m_psoCache.end()) DispatchNorm(node, it->second);
        break;
    }
    case GGML_OP_GROUP_NORM: {
        auto it = m_psoCache.find("group_norm");
        if (it != m_psoCache.end()) DispatchNorm(node, it->second);
        break;
    }

    case GGML_OP_CPY:
    case GGML_OP_CONT:
    case GGML_OP_DUP: {
        auto it = m_psoCache.find("copy");
        if (it != m_psoCache.end()) DispatchCopy(node, it->second);
        break;
    }

    case GGML_OP_GET_ROWS: {
        // Dequant path — pick shader by weight type
        const char* name = nullptr;
        if (node->src[0]) {
            switch (node->src[0]->type) {
                case GGML_TYPE_Q4_0: name = "dequant_q4_0"; break;
                // TODO: case GGML_TYPE_Q8_0: name = "dequant_q8_0"; break;
                // TODO: case GGML_TYPE_Q4_1: name = "dequant_q4_1"; break;
                default: break;
            }
        }
        if (name) {
            auto it = m_psoCache.find(name);
            if (it != m_psoCache.end()) DispatchDequant(node, it->second);
        }
        break;
    }

    case GGML_OP_NONE:
    case GGML_OP_RESHAPE:
    case GGML_OP_VIEW:
    case GGML_OP_TRANSPOSE:
    case GGML_OP_PERMUTE:
        break; // metadata ops — no GPU work

    default:
        // TODO: log unhandled op
        break;
    }
}

// ---------------------------------------------------------------------------
// Dispatch helpers
// ---------------------------------------------------------------------------
void DieWorker::DispatchUnary(ggml_tensor* node, PsoEntry& pso) {
    PC_Unary pc = {};
    pc.ne   = (uint32_t)ggml_nelements(node);
    auto* s = node->src[0];
    pc.ne00 = (uint32_t)s->ne[0]; pc.ne01 = (uint32_t)s->ne[1];
    pc.ne02 = (uint32_t)s->ne[2]; pc.ne03 = (uint32_t)s->ne[3];
    pc.nb00 = (uint32_t)s->nb[0]; pc.nb01 = (uint32_t)s->nb[1];
    pc.nb02 = (uint32_t)s->nb[2]; pc.nb03 = (uint32_t)s->nb[3];
    pc.ne10 = (uint32_t)node->ne[0]; pc.ne11 = (uint32_t)node->ne[1];
    pc.ne12 = (uint32_t)node->ne[2]; pc.ne13 = (uint32_t)node->ne[3];
    pc.nb10 = (uint32_t)node->nb[0]; pc.nb11 = (uint32_t)node->nb[1];
    pc.nb12 = (uint32_t)node->nb[2]; pc.nb13 = (uint32_t)node->nb[3];

    m_cmdList->SetPipelineState(pso.pso.Get());
    m_cmdList->SetComputeRootSignature(pso.rootSig.Get());
    m_cmdList->SetComputeRoot32BitConstants(0, sizeof(pc) / 4, &pc, 0);
    m_cmdList->Dispatch(CEIL_DIV(pc.ne, kComputeThreads), 1, 1);
}

void DieWorker::DispatchNorm(ggml_tensor* node, PsoEntry& pso) {
    PC_Unary pc = {};
    pc.ne   = (uint32_t)ggml_nelements(node);
    auto* s = node->src[0];
    pc.ne00 = (uint32_t)s->ne[0]; pc.ne01 = (uint32_t)s->ne[1];
    pc.ne02 = (uint32_t)s->ne[2]; pc.ne03 = (uint32_t)s->ne[3];
    pc.nb00 = (uint32_t)s->nb[0]; pc.nb01 = (uint32_t)s->nb[1];
    pc.nb02 = (uint32_t)s->nb[2]; pc.nb03 = (uint32_t)s->nb[3];
    pc.param1 = 1e-6f; // eps — override from node->op_params if exposed

    uint32_t nrows = pc.ne / pc.ne00; // one workgroup per row

    m_cmdList->SetPipelineState(pso.pso.Get());
    m_cmdList->SetComputeRootSignature(pso.rootSig.Get());
    m_cmdList->SetComputeRoot32BitConstants(0, sizeof(pc) / 4, &pc, 0);
    m_cmdList->Dispatch(nrows, 1, 1);
}

void DieWorker::DispatchGLU(ggml_tensor* node, PsoEntry& pso) {
    PC_GLU pc = {};
    pc.N    = (uint32_t)ggml_nelements(node);
    pc.ne00 = (uint32_t)node->src[0]->ne[0];
    pc.ne20 = (uint32_t)node->ne[0];
    pc.mode = 0;

    m_cmdList->SetPipelineState(pso.pso.Get());
    m_cmdList->SetComputeRootSignature(pso.rootSig.Get());
    m_cmdList->SetComputeRoot32BitConstants(0, sizeof(pc) / 4, &pc, 0);
    m_cmdList->Dispatch(CEIL_DIV(pc.N, kComputeThreads), 1, 1);
}

void DieWorker::DispatchCopy(ggml_tensor* node, PsoEntry& pso) {
    PC_Unary pc = {};
    pc.ne = (uint32_t)ggml_nelements(node);
    m_cmdList->SetPipelineState(pso.pso.Get());
    m_cmdList->SetComputeRootSignature(pso.rootSig.Get());
    m_cmdList->SetComputeRoot32BitConstants(0, sizeof(pc) / 4, &pc, 0);
    m_cmdList->Dispatch(CEIL_DIV(pc.ne, kComputeThreads), 1, 1);
}

void DieWorker::DispatchDequant(ggml_tensor* node, PsoEntry& pso) {
    // Dequant shaders: [numthreads(32,1,1)], one thread per element (QK=32 per block)
    uint32_t ne = (uint32_t)ggml_nelements(node);
    PC_Unary pc = {};
    pc.ne = ne;
    m_cmdList->SetPipelineState(pso.pso.Get());
    m_cmdList->SetComputeRootSignature(pso.rootSig.Get());
    m_cmdList->SetComputeRoot32BitConstants(0, sizeof(pc) / 4, &pc, 0);
    m_cmdList->Dispatch(CEIL_DIV(ne, 32u), 1, 1);
}

void DieWorker::DispatchMulMat(ggml_tensor* node) {
    // Route to DirectML GEMM — placeholder
    // TODO: bind DML operator, set input/output descriptors, record via IDMLCommandRecorder
    (void)node;
}

void DieWorker::DispatchBinary(ggml_tensor* node, PsoEntry& pso) {
    PC_Binary pc = {};
    pc.ne = (uint32_t)ggml_nelements(node);
    // TODO: fill ne/nb fields from node->src[0], src[1], node
    m_cmdList->SetPipelineState(pso.pso.Get());
    m_cmdList->SetComputeRootSignature(pso.rootSig.Get());
    m_cmdList->SetComputeRoot32BitConstants(0, sizeof(pc) / 4, &pc, 0);
    m_cmdList->Dispatch(CEIL_DIV(pc.ne, kComputeThreads), 1, 1);
}
