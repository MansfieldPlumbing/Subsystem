// dpgpu.dll — C ABI over D3D12 GEMM for dpx P/Invoke. PERSISTENT device/queue/root-sig/PSO
// (created once, reused) so steady-state calls pay only buffer alloc + compute, not device creation.
#include <windows.h>
#include <wrl/client.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <cstring>
#include <cstdlib>
#include <unordered_map>
#pragma comment(lib,"d3d12.lib")
#pragma comment(lib,"dxgi.lib")
#pragma comment(lib,"dxguid.lib")
using Microsoft::WRL::ComPtr;

static ComPtr<ID3D12Device> g_dev;
static ComPtr<ID3D12CommandQueue> g_q;
static ComPtr<ID3D12CommandAllocator> g_alloc;
static ComPtr<ID3D12GraphicsCommandList> g_cl;
static ComPtr<ID3D12RootSignature> g_root;
static ComPtr<ID3D12Fence> g_fence;
static HANDLE g_ev = nullptr;
static UINT64 g_fv = 0;
static ComPtr<ID3D12PipelineState> g_pso;
static unsigned g_psoLen = 0;
// persistent scratch buffers — reused across calls (grown to max seen) to kill per-call CreateCommittedResource churn
static ComPtr<ID3D12Resource> g_aBuf,g_bBuf,g_cBuf,g_rbBuf;
static UINT g_aCap=0,g_bCap=0,g_cCap=0,g_rbCap=0;
static bool g_cInUAV=false;   // is g_cBuf currently in UNORDERED_ACCESS?

// Parked weight buffers registry
static std::unordered_map<int, ComPtr<ID3D12Resource>> g_weights;

struct CommandListKey {
    unsigned M, N, K;
    unsigned dxilLen;
    int weightId;
    unsigned weightOffsetBytes;
    int mode; // 0 = normal, 1 = parked_A, 2 = parked_B
    bool operator==(const CommandListKey& o) const {
        return M==o.M && N==o.N && K==o.K && dxilLen==o.dxilLen && weightId==o.weightId && weightOffsetBytes==o.weightOffsetBytes && mode==o.mode;
    }
};

namespace std {
    template <>
    struct hash<CommandListKey> {
        size_t operator()(const CommandListKey& k) const {
            size_t h = k.M;
            h ^= (size_t)k.N + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= (size_t)k.K + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= (size_t)k.dxilLen + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= (size_t)k.weightId + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= (size_t)k.weightOffsetBytes + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= (size_t)k.mode + 0x9e3779b9 + (h << 6) + (h >> 2);
            return h;
        }
    };
}

struct CachedCommandList {
    ComPtr<ID3D12CommandAllocator> alloc;
    ComPtr<ID3D12GraphicsCommandList> cl;
};

static std::unordered_map<CommandListKey, CachedCommandList> g_clCache;

static wchar_t g_devName[128] = L"(none)";
extern "C" __declspec(dllexport) const wchar_t* dpgpu_device_name(){ return g_devName; }
static ComPtr<ID3D12Device> MakeDevice(){
    ComPtr<IDXGIFactory4> fac; CreateDXGIFactory2(0,IID_PPV_ARGS(&fac));
    int forceIdx=-1; { char envb[16]={0}; if(GetEnvironmentVariableA("DPGPU_ADAPTER",envb,sizeof(envb))>0) forceIdx=atoi(envb); }
    ComPtr<ID3D12Device> best; SIZE_T bestVram=0; ComPtr<IDXGIAdapter1> ad;
    for(UINT i=0; fac->EnumAdapters1(i,&ad)==S_OK; i++){
        DXGI_ADAPTER_DESC1 d; ad->GetDesc1(&d);
        if(d.Flags&DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        ComPtr<ID3D12Device> dv; if(FAILED(D3D12CreateDevice(ad.Get(),D3D_FEATURE_LEVEL_12_0,IID_PPV_ARGS(&dv)))) continue;
        if(forceIdx>=0){ if((int)i==forceIdx){ best=dv; wcscpy_s(g_devName,d.Description); break; } continue; }
        if(!best || d.DedicatedVideoMemory>bestVram){ best=dv; bestVram=d.DedicatedVideoMemory; wcscpy_s(g_devName,d.Description); }   // beefiest GPU by VRAM (V340 8GB > P2000 5GB), not adapter 0
    }
    if(best) return best;
    ComPtr<IDXGIAdapter> wa; fac->EnumWarpAdapter(IID_PPV_ARGS(&wa)); ComPtr<ID3D12Device> dv; D3D12CreateDevice(wa.Get(),D3D_FEATURE_LEVEL_12_0,IID_PPV_ARGS(&dv)); wcscpy_s(g_devName,L"WARP"); return dv; }

static bool EnsureInit(){
    if(g_dev) return true;
    g_dev=MakeDevice(); if(!g_dev) return false;
    D3D12_COMMAND_QUEUE_DESC qd={}; qd.Type=D3D12_COMMAND_LIST_TYPE_COMPUTE; if(FAILED(g_dev->CreateCommandQueue(&qd,IID_PPV_ARGS(&g_q)))) return false;
    g_dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE,IID_PPV_ARGS(&g_alloc));
    g_dev->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_COMPUTE,g_alloc.Get(),nullptr,IID_PPV_ARGS(&g_cl));
    g_cl->Close();   // start closed so the first per-call Reset is valid
    D3D12_ROOT_PARAMETER rp[4]={};
    rp[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; rp[0].Constants.Num32BitValues=3;
    rp[1].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV; rp[1].Descriptor.ShaderRegister=0;
    rp[2].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV; rp[2].Descriptor.ShaderRegister=1;
    rp[3].ParameterType=D3D12_ROOT_PARAMETER_TYPE_UAV; rp[3].Descriptor.ShaderRegister=0;
    D3D12_ROOT_SIGNATURE_DESC rsd={}; rsd.NumParameters=4; rsd.pParameters=rp; ComPtr<ID3DBlob> s,e;
    if(FAILED(D3D12SerializeRootSignature(&rsd,D3D_ROOT_SIGNATURE_VERSION_1,&s,&e))) return false;
    if(FAILED(g_dev->CreateRootSignature(0,s->GetBufferPointer(),s->GetBufferSize(),IID_PPV_ARGS(&g_root)))) return false;
    g_dev->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&g_fence));
    g_ev=CreateEvent(nullptr,FALSE,FALSE,nullptr);
    return true;
}

extern "C" __declspec(dllexport)
int dpgpu_register_weight(int id, const float* data, unsigned bytes) {
    if (!EnsureInit()) return -1;
    // 1. Create Default buffer (GPU local VRAM)
    ComPtr<ID3D12Resource> defBuf;
    D3D12_HEAP_PROPERTIES defHp = { D3D12_HEAP_TYPE_DEFAULT, D3D12_CPU_PAGE_PROPERTY_UNKNOWN, D3D12_MEMORY_POOL_UNKNOWN, 1, 1 };
    D3D12_RESOURCE_DESC rd = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, bytes, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1,0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_RESOURCE_FLAG_NONE };
    if (FAILED(g_dev->CreateCommittedResource(&defHp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&defBuf)))) return -2;

    // 2. Create Upload buffer (Staging)
    ComPtr<ID3D12Resource> upBuf;
    D3D12_HEAP_PROPERTIES upHp = { D3D12_HEAP_TYPE_UPLOAD, D3D12_CPU_PAGE_PROPERTY_UNKNOWN, D3D12_MEMORY_POOL_UNKNOWN, 1, 1 };
    D3D12_RESOURCE_DESC upRd = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, bytes, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1,0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_RESOURCE_FLAG_NONE };
    if (FAILED(g_dev->CreateCommittedResource(&upHp, D3D12_HEAP_FLAG_NONE, &upRd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&upBuf)))) return -3;

    // 3. Copy CPU data to Upload buffer
    void* p; D3D12_RANGE z = {0,0};
    upBuf->Map(0, &z, &p);
    memcpy(p, data, bytes);
    upBuf->Unmap(0, nullptr);

    // 4. Record Copy commands
    g_alloc->Reset();
    g_cl->Reset(g_alloc.Get(), nullptr);

    D3D12_RESOURCE_BARRIER b1 = {};
    b1.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b1.Transition.pResource = defBuf.Get();
    b1.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b1.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    b1.Transition.Subresource = 0;
    g_cl->ResourceBarrier(1, &b1);

    g_cl->CopyBufferRegion(defBuf.Get(), 0, upBuf.Get(), 0, bytes);

    D3D12_RESOURCE_BARRIER b2 = {};
    b2.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b2.Transition.pResource = defBuf.Get();
    b2.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b2.Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    b2.Transition.Subresource = 0;
    g_cl->ResourceBarrier(1, &b2);

    g_cl->Close();

    // 5. Execute commands and wait
    ID3D12CommandList* lists[] = { g_cl.Get() };
    g_q->ExecuteCommandLists(1, lists);
    g_fv++;
    g_q->Signal(g_fence.Get(), g_fv);
    g_fence->SetEventOnCompletion(g_fv, g_ev);
    WaitForSingleObject(g_ev, INFINITE);

    g_weights[id] = defBuf;
    return 0;
}

extern "C" __declspec(dllexport)
int dpgpu_gemm_parked_A(int weightId, unsigned weightOffsetBytes, const float* B, float* C, unsigned M, unsigned N, unsigned K, const unsigned char* dxil, unsigned dxilLen) {
    if (!EnsureInit()) return -1;
    auto it = g_weights.find(weightId);
    if (it == g_weights.end()) return -2;
    ID3D12Resource* wBuf = it->second.Get();

    UINT bB=K*N*4, cB=M*N*4;
    auto buf=[&](D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl,UINT bytes,ComPtr<ID3D12Resource>&r){
        D3D12_HEAP_PROPERTIES hp={}; hp.Type=ht; D3D12_RESOURCE_DESC rd={}; rd.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; rd.Width=bytes?bytes:4; rd.Height=1; rd.DepthOrArraySize=1; rd.MipLevels=1; rd.SampleDesc.Count=1; rd.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; rd.Flags=fl;
        return g_dev->CreateCommittedResource(&hp,D3D12_HEAP_FLAG_NONE,&rd,st,nullptr,IID_PPV_ARGS(&r)); };
    auto ensure=[&](ComPtr<ID3D12Resource>&r,UINT&cap,UINT need,D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl){ 
        if(r && cap>=need) return; 
        cap=need; r.Reset(); buf(ht,st,fl,need,r); 
        g_clCache.clear();
        FILE* f = fopen("S:\\spawn-fusion\\onnx-interp\\cache_debug.log", "a");
        if(f) { fprintf(f, "CACHE CLEARED: grown to %u\n", need); fclose(f); }
    };
    bool cFresh = !(g_cBuf && g_cCap>=cB);
    ensure(g_bBuf,g_bCap,bB,D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE);
    ensure(g_cBuf,g_cCap,cB,D3D12_HEAP_TYPE_DEFAULT,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    ensure(g_rbBuf,g_rbCap,cB,D3D12_HEAP_TYPE_READBACK,D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_FLAG_NONE);
    if(cFresh) g_cInUAV=true;

    { void* p; D3D12_RANGE z={0,0}; g_bBuf->Map(0,&z,&p); memcpy(p,B,bB); g_bBuf->Unmap(0,nullptr); }

    CommandListKey key = { M, N, K, dxilLen, weightId, weightOffsetBytes, 1 };
    auto itCl = g_clCache.find(key);
    FILE* fLog = fopen("S:\\spawn-fusion\\onnx-interp\\cache_debug.log", "a");
    if (fLog) {
        if (itCl == g_clCache.end()) fprintf(fLog, "Cache MISS: M=%u N=%u K=%u Mode=1 weightId=%d offset=%u\n", M, N, K, weightId, weightOffsetBytes);
        else fprintf(fLog, "Cache HIT: M=%u N=%u K=%u Mode=1 weightId=%d offset=%u\n", M, N, K, weightId, weightOffsetBytes);
        fclose(fLog);
    }
    ID3D12GraphicsCommandList* clToExecute = nullptr;
    if (itCl == g_clCache.end()) {
        CachedCommandList cached;
        if (FAILED(g_dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&cached.alloc)))) return -4;
        if (FAILED(g_dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, cached.alloc.Get(), nullptr, IID_PPV_ARGS(&cached.cl)))) return -5;

        if(!g_pso || g_psoLen!=dxilLen){
            D3D12_COMPUTE_PIPELINE_STATE_DESC pd={}; pd.pRootSignature=g_root.Get(); pd.CS.pShaderBytecode=dxil; pd.CS.BytecodeLength=dxilLen;
            g_pso.Reset(); if(FAILED(g_dev->CreateComputePipelineState(&pd,IID_PPV_ARGS(&g_pso)))) return -6; g_psoLen=dxilLen;
        }

        cached.cl->SetPipelineState(g_pso.Get());
        cached.cl->SetComputeRootSignature(g_root.Get());
        UINT gc[3]={M,N,K}; cached.cl->SetComputeRoot32BitConstants(0,3,gc,0);
        cached.cl->SetComputeRootShaderResourceView(1,wBuf->GetGPUVirtualAddress() + weightOffsetBytes);
        cached.cl->SetComputeRootShaderResourceView(2,g_bBuf->GetGPUVirtualAddress());
        cached.cl->SetComputeRootUnorderedAccessView(3,g_cBuf->GetGPUVirtualAddress());
        cached.cl->Dispatch((N+15)/16,(M+15)/16,1);

        D3D12_RESOURCE_BARRIER tb={}; tb.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; tb.Transition.pResource=g_cBuf.Get(); tb.Transition.StateBefore=D3D12_RESOURCE_STATE_UNORDERED_ACCESS; tb.Transition.StateAfter=D3D12_RESOURCE_STATE_COPY_SOURCE; tb.Transition.Subresource=0;
        cached.cl->ResourceBarrier(1,&tb);
        cached.cl->CopyBufferRegion(g_rbBuf.Get(),0,g_cBuf.Get(),0,cB);

        D3D12_RESOURCE_BARRIER tbBack = tb;
        tbBack.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
        tbBack.Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        cached.cl->ResourceBarrier(1, &tbBack);

        cached.cl->Close();

        g_clCache[key] = cached;
        clToExecute = cached.cl.Get();
    } else {
        clToExecute = itCl->second.cl.Get();
    }

    ID3D12CommandList* L[]={clToExecute}; g_q->ExecuteCommandLists(1,L);
    g_fv++; g_q->Signal(g_fence.Get(),g_fv); g_fence->SetEventOnCompletion(g_fv,g_ev); WaitForSingleObject(g_ev,INFINITE);

    { void* p; D3D12_RANGE r={0,cB}; g_rbBuf->Map(0,&r,&p); memcpy(C,p,cB); D3D12_RANGE w={0,0}; g_rbBuf->Unmap(0,&w); }
    return 0;
}

extern "C" __declspec(dllexport)
int dpgpu_gemm_parked_B(const float* A, int weightId, unsigned weightOffsetBytes, float* C, unsigned M, unsigned N, unsigned K, const unsigned char* dxil, unsigned dxilLen) {
    if (!EnsureInit()) return -1;
    auto it = g_weights.find(weightId);
    if (it == g_weights.end()) return -2;
    ID3D12Resource* wBuf = it->second.Get();

    UINT aB=M*K*4, cB=M*N*4;
    auto buf=[&](D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl,UINT bytes,ComPtr<ID3D12Resource>&r){
        D3D12_HEAP_PROPERTIES hp={}; hp.Type=ht; D3D12_RESOURCE_DESC rd={}; rd.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; rd.Width=bytes?bytes:4; rd.Height=1; rd.DepthOrArraySize=1; rd.MipLevels=1; rd.SampleDesc.Count=1; rd.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; rd.Flags=fl;
        return g_dev->CreateCommittedResource(&hp,D3D12_HEAP_FLAG_NONE,&rd,st,nullptr,IID_PPV_ARGS(&r)); };
    auto ensure=[&](ComPtr<ID3D12Resource>&r,UINT&cap,UINT need,D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl){ 
        if(r && cap>=need) return; 
        cap=need; r.Reset(); buf(ht,st,fl,need,r); 
        g_clCache.clear();
    };
    bool cFresh = !(g_cBuf && g_cCap>=cB);
    ensure(g_aBuf,g_aCap,aB,D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE);
    ensure(g_cBuf,g_cCap,cB,D3D12_HEAP_TYPE_DEFAULT,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    ensure(g_rbBuf,g_rbCap,cB,D3D12_HEAP_TYPE_READBACK,D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_FLAG_NONE);
    if(cFresh) g_cInUAV=true;

    { void* p; D3D12_RANGE z={0,0}; g_aBuf->Map(0,&z,&p); memcpy(p,A,aB); g_aBuf->Unmap(0,nullptr); }

    CommandListKey key = { M, N, K, dxilLen, weightId, weightOffsetBytes, 2 };
    auto itCl = g_clCache.find(key);
    FILE* fLog = fopen("S:\\spawn-fusion\\onnx-interp\\cache_debug.log", "a");
    if (fLog) {
        if (itCl == g_clCache.end()) fprintf(fLog, "Cache MISS: M=%u N=%u K=%u Mode=2 weightId=%d offset=%u\n", M, N, K, weightId, weightOffsetBytes);
        else fprintf(fLog, "Cache HIT: M=%u N=%u K=%u Mode=2 weightId=%d offset=%u\n", M, N, K, weightId, weightOffsetBytes);
        fclose(fLog);
    }
    ID3D12GraphicsCommandList* clToExecute = nullptr;
    if (itCl == g_clCache.end()) {
        CachedCommandList cached;
        if (FAILED(g_dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&cached.alloc)))) return -4;
        if (FAILED(g_dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, cached.alloc.Get(), nullptr, IID_PPV_ARGS(&cached.cl)))) return -5;

        if(!g_pso || g_psoLen!=dxilLen){
            D3D12_COMPUTE_PIPELINE_STATE_DESC pd={}; pd.pRootSignature=g_root.Get(); pd.CS.pShaderBytecode=dxil; pd.CS.BytecodeLength=dxilLen;
            g_pso.Reset(); if(FAILED(g_dev->CreateComputePipelineState(&pd,IID_PPV_ARGS(&g_pso)))) return -6; g_psoLen=dxilLen;
        }

        cached.cl->SetPipelineState(g_pso.Get());
        cached.cl->SetComputeRootSignature(g_root.Get());
        UINT gc[3]={M,N,K}; cached.cl->SetComputeRoot32BitConstants(0,3,gc,0);
        cached.cl->SetComputeRootShaderResourceView(1,g_aBuf->GetGPUVirtualAddress());
        cached.cl->SetComputeRootShaderResourceView(2,wBuf->GetGPUVirtualAddress() + weightOffsetBytes);
        cached.cl->SetComputeRootUnorderedAccessView(3,g_cBuf->GetGPUVirtualAddress());
        cached.cl->Dispatch((N+15)/16,(M+15)/16,1);

        D3D12_RESOURCE_BARRIER tb={}; tb.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; tb.Transition.pResource=g_cBuf.Get(); tb.Transition.StateBefore=D3D12_RESOURCE_STATE_UNORDERED_ACCESS; tb.Transition.StateAfter=D3D12_RESOURCE_STATE_COPY_SOURCE; tb.Transition.Subresource=0;
        cached.cl->ResourceBarrier(1,&tb);
        cached.cl->CopyBufferRegion(g_rbBuf.Get(),0,g_cBuf.Get(),0,cB);

        D3D12_RESOURCE_BARRIER tbBack = tb;
        tbBack.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
        tbBack.Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        cached.cl->ResourceBarrier(1, &tbBack);

        cached.cl->Close();

        g_clCache[key] = cached;
        clToExecute = cached.cl.Get();
    } else {
        clToExecute = itCl->second.cl.Get();
    }

    ID3D12CommandList* L[]={clToExecute}; g_q->ExecuteCommandLists(1,L);
    g_fv++; g_q->Signal(g_fence.Get(),g_fv); g_fence->SetEventOnCompletion(g_fv,g_ev); WaitForSingleObject(g_ev,INFINITE);

    { void* p; D3D12_RANGE r={0,cB}; g_rbBuf->Map(0,&r,&p); memcpy(C,p,cB); D3D12_RANGE w={0,0}; g_rbBuf->Unmap(0,&w); }
    return 0;
}

extern "C" __declspec(dllexport)
int dpgpu_gemm(const float* A,const float* B,float* C,unsigned M,unsigned N,unsigned K,const unsigned char* dxil,unsigned dxilLen){
    if(!EnsureInit()) return -1;

    UINT aB=M*K*4,bB=K*N*4,cB=M*N*4;
    auto buf=[&](D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl,UINT bytes,ComPtr<ID3D12Resource>&r){
        D3D12_HEAP_PROPERTIES hp={}; hp.Type=ht; D3D12_RESOURCE_DESC rd={}; rd.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; rd.Width=bytes?bytes:4; rd.Height=1; rd.DepthOrArraySize=1; rd.MipLevels=1; rd.SampleDesc.Count=1; rd.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; rd.Flags=fl;
        return g_dev->CreateCommittedResource(&hp,D3D12_HEAP_FLAG_NONE,&rd,st,nullptr,IID_PPV_ARGS(&r)); };
    auto ensure=[&](ComPtr<ID3D12Resource>&r,UINT&cap,UINT need,D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl){ 
        if(r && cap>=need) return; 
        cap=need; r.Reset(); buf(ht,st,fl,need,r); 
        g_clCache.clear();
    };
    bool cFresh = !(g_cBuf && g_cCap>=cB);
    ensure(g_aBuf,g_aCap,aB,D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE);
    ensure(g_bBuf,g_bCap,bB,D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE);
    ensure(g_cBuf,g_cCap,cB,D3D12_HEAP_TYPE_DEFAULT,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    ensure(g_rbBuf,g_rbCap,cB,D3D12_HEAP_TYPE_READBACK,D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_FLAG_NONE);
    if(cFresh) g_cInUAV=true;

    { void* p; D3D12_RANGE z={0,0}; g_aBuf->Map(0,&z,&p); memcpy(p,A,aB); g_aBuf->Unmap(0,nullptr); g_bBuf->Map(0,&z,&p); memcpy(p,B,bB); g_bBuf->Unmap(0,nullptr); }

    CommandListKey key = { M, N, K, dxilLen, 0, 0, 0 };
    auto itCl = g_clCache.find(key);
    FILE* fLog = fopen("S:\\spawn-fusion\\onnx-interp\\cache_debug.log", "a");
    if (fLog) {
        if (itCl == g_clCache.end()) fprintf(fLog, "Cache MISS: M=%u N=%u K=%u Mode=0\n", M, N, K);
        else fprintf(fLog, "Cache HIT: M=%u N=%u K=%u Mode=0\n", M, N, K);
        fclose(fLog);
    }
    ID3D12GraphicsCommandList* clToExecute = nullptr;
    if (itCl == g_clCache.end()) {
        CachedCommandList cached;
        if (FAILED(g_dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&cached.alloc)))) return -4;
        if (FAILED(g_dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, cached.alloc.Get(), nullptr, IID_PPV_ARGS(&cached.cl)))) return -5;

        if(!g_pso || g_psoLen!=dxilLen){
            D3D12_COMPUTE_PIPELINE_STATE_DESC pd={}; pd.pRootSignature=g_root.Get(); pd.CS.pShaderBytecode=dxil; pd.CS.BytecodeLength=dxilLen;
            g_pso.Reset(); if(FAILED(g_dev->CreateComputePipelineState(&pd,IID_PPV_ARGS(&g_pso)))) return -6; g_psoLen=dxilLen;
        }

        cached.cl->SetPipelineState(g_pso.Get());
        cached.cl->SetComputeRootSignature(g_root.Get());
        UINT gc[3]={M,N,K}; cached.cl->SetComputeRoot32BitConstants(0,3,gc,0);
        cached.cl->SetComputeRootShaderResourceView(1,g_aBuf->GetGPUVirtualAddress());
        cached.cl->SetComputeRootShaderResourceView(2,g_bBuf->GetGPUVirtualAddress());
        cached.cl->SetComputeRootUnorderedAccessView(3,g_cBuf->GetGPUVirtualAddress());
        cached.cl->Dispatch((N+15)/16,(M+15)/16,1);

        D3D12_RESOURCE_BARRIER tb={}; tb.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; tb.Transition.pResource=g_cBuf.Get(); tb.Transition.StateBefore=D3D12_RESOURCE_STATE_UNORDERED_ACCESS; tb.Transition.StateAfter=D3D12_RESOURCE_STATE_COPY_SOURCE; tb.Transition.Subresource=0;
        cached.cl->ResourceBarrier(1,&tb);
        cached.cl->CopyBufferRegion(g_rbBuf.Get(),0,g_cBuf.Get(),0,cB);

        D3D12_RESOURCE_BARRIER tbBack = tb;
        tbBack.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
        tbBack.Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        cached.cl->ResourceBarrier(1, &tbBack);

        cached.cl->Close();

        g_clCache[key] = cached;
        clToExecute = cached.cl.Get();
    } else {
        clToExecute = itCl->second.cl.Get();
    }

    ID3D12CommandList* L[]={clToExecute}; g_q->ExecuteCommandLists(1,L);
    g_fv++; g_q->Signal(g_fence.Get(),g_fv); g_fence->SetEventOnCompletion(g_fv,g_ev); WaitForSingleObject(g_ev,INFINITE);

    { void* p; D3D12_RANGE r={0,cB}; g_rbBuf->Map(0,&r,&p); memcpy(C,p,cB); D3D12_RANGE w={0,0}; g_rbBuf->Unmap(0,&w); }
    return 0;
}

