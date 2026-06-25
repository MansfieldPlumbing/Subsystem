// DieWorker.h
// Per-die D3D12 compute worker.
// Mirrors PipelineNode from Consumer.h — one instance per V340 die.
// Each worker:
//   - Owns its own ID3D12Device* targeting a specific DXGI adapter
//   - Connects to the upstream die's boundary texture via DirectPort NT names
//   - Publishes its own boundary texture downstream
//   - Runs a pinned CPU thread that blocks on dp12_queue_wait then dispatches
#pragma once
#include "ggml-d3d12-shared.h"
#include "directport.h"
#include <atomic>
#include <thread>
#include <vector>
#include <unordered_map>
#include <string>

// Forward declare ggml types to avoid pulling in all of ggml.h
struct ggml_tensor;
struct ggml_cgraph;

// PSO entry — one per DXIL blob loaded at startup
struct PsoEntry {
    ComPtr<ID3D12PipelineState> pso;
    ComPtr<ID3D12RootSignature> rootSig;
};

class DieWorker {
public:
    // die_index: 0-3, maps to DXGI adapter (kDieAdapterBase + die_index)
    // cpu_core:  physical core to pin the worker thread to
    // upstream_manifest: NT name of the upstream die's TensorManifest
    //                    (nullptr for die 0 — it reads from host)
    DieWorker(int die_index, int cpu_core, const wchar_t* upstream_manifest);
    ~DieWorker();

    // Call once after construction. Creates D3D12 device, loads PSOs,
    // connects to upstream, publishes downstream manifest.
    HRESULT Initialize(uint32_t tensor_width, uint32_t tensor_height);

    // Assign the ggml graph nodes this die is responsible for.
    // Called before StartWorker. start_node..end_node exclusive.
    void AssignNodes(ggml_cgraph* graph, int start_node, int end_node);

    // Spin up the pinned worker thread. Blocks internally on upstream fence.
    void StartWorker();

    // Signal the worker to drain and stop.
    void StopWorker();

    // The NT manifest name this die publishes (downstream die calls dp12_open on it)
    const wchar_t* GetManifestName() const { return m_manifestName.c_str(); }

    bool IsRunning() const { return m_isRunning.load(); }

    // For die 0 only: called by host to kick off a new token
    void KickInference(uint64_t token_index);

private:
    // --- Init helpers (mirror PipelineNode::InitD3D11 / ConnectToProducer) ---
    HRESULT InitD3D12();
    HRESULT LoadPSOs();
    HRESULT ConnectToUpstream();
    HRESULT InitBoundaryOutput(uint32_t width, uint32_t height);
    void    PublishManifest(uint32_t width, uint32_t height);

    // --- Hot path (mirrors PipelineNode::RenderFrame) ---
    void WorkerThread();
    void DispatchGraph();
    void DispatchNode(ggml_tensor* node);

    // --- Per-op dispatch ---
    void DispatchUnary(ggml_tensor* node, PsoEntry& pso);
    void DispatchBinary(ggml_tensor* node, PsoEntry& pso);
    void DispatchGLU(ggml_tensor* node, PsoEntry& pso);
    void DispatchNorm(ggml_tensor* node, PsoEntry& pso);
    void DispatchMulMat(ggml_tensor* node);   // routes to DirectML GEMM
    void DispatchDequant(ggml_tensor* node, PsoEntry& pso);
    void DispatchCopy(ggml_tensor* node, PsoEntry& pso);

    // --- D3D12 core ---
    ComPtr<IDXGIAdapter1>           m_adapter;
    ComPtr<ID3D12Device>            m_device;
    ComPtr<ID3D12CommandQueue>      m_computeQueue;
    ComPtr<ID3D12CommandAllocator>  m_cmdAlloc;
    ComPtr<ID3D12GraphicsCommandList> m_cmdList;
    ComPtr<ID3D12DescriptorHeap>    m_uavHeap;
    LUID                            m_adapterLuid = {};

    // --- Boundary output (this die → downstream) ---
    ComPtr<ID3D12Resource>  m_boundaryTex;      // shared texture published downstream
    ComPtr<ID3D12Fence>     m_boundaryFence;    // shared fence
    HANDLE                  m_hBoundaryTex    = nullptr;
    HANDLE                  m_hBoundaryFence  = nullptr;
    uint64_t                m_tokenValue      = 0;
    HANDLE                  m_hManifestOut    = nullptr;
    TensorManifest*         m_pManifestOut    = nullptr;

    // --- Upstream connection (mirrors ProducerConnection) ---
    DiePort                 m_upstream;

    // --- PSO cache ---
    // Key: shader name (e.g. "silu", "norm", "dequant_q4_0")
    std::unordered_map<std::string, PsoEntry> m_psoCache;

    // --- Graph work ---
    ggml_cgraph*  m_graph      = nullptr;
    int           m_nodeStart  = 0;
    int           m_nodeEnd    = 0;

    // --- Thread ---
    std::thread         m_thread;
    std::atomic<bool>   m_isRunning  = false;
    HANDLE              m_hKickEvent = nullptr; // used by die 0 only

    // --- Identity ---
    int            m_dieIndex;
    int            m_cpuCore;
    std::wstring   m_upstreamManifestName;
    std::wstring   m_manifestName; // this die's published manifest name
};
