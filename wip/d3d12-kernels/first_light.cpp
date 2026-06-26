// first_light.cpp  — Ticket #92 FIRST SLICE
// Die-0 D3D12 compute: run ONE kokoro node (leaky_relu.dxil) on the GPU,
// feed/read via a raw buffer, diff vs the per-node ORT oracle at fp16 tolerance.
//
// Reuses d3d12_dispatch_reference.h verbatim:
//   leaky_relu = a "Simple" op -> PushConst_Simple {KX,KY,param1..4}
//   bound as SetComputeRoot32BitConstants(0, 6, &pc, 0)
//   Dispatch( CEIL_DIV(ne, 512), 1, 1 )
//
// Bindings (from `dxc -Fc`):  parameter->b0,  _49->t0 (raw SRV),  _58->u0 (raw UAV)
// Bin format (Program.cs LoadBin): int32 dtype(1=f32) | int32 rank | int64[rank] dims | f32[ne]
#define _CRT_SECURE_NO_WARNINGS
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <DirectXPackedVector.h>
#include <vector>
#include <string>
#include <cstdio>
#include <cstdint>
#include <cmath>
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

using Microsoft::WRL::ComPtr;
using DirectX::PackedVector::XMConvertFloatToHalf;
using DirectX::PackedVector::XMConvertHalfToFloat;

#define HR(x) do { HRESULT _hr = (x); if (FAILED(_hr)) { printf("[FAIL] %s -> 0x%08X\n", #x, (unsigned)_hr); return 2; } } while(0)
#define CEIL_DIV(a,b) (((a)+(b)-1)/(b))

struct Bin { int dtype = 0; std::vector<long long> dims; std::vector<float> f;
    long long ne() const { long long n = 1; for (auto d : dims) n *= d; return n; } };

static Bin loadBin(const char* p) {
    Bin b; FILE* fp = fopen(p, "rb");
    if (!fp) { printf("[FAIL] cannot open bin %s\n", p); exit(3); }
    int rank = 0; fread(&b.dtype, 4, 1, fp); fread(&rank, 4, 1, fp);
    b.dims.resize(rank); fread(b.dims.data(), 8, rank, fp);
    long long n = b.ne(); b.f.resize(n);
    fread(b.f.data(), 4, (size_t)n, fp); fclose(fp);
    return b;
}
static std::vector<uint8_t> loadFile(const char* p) {
    FILE* fp = fopen(p, "rb"); if (!fp) { printf("[FAIL] cannot open %s\n", p); exit(3); }
    fseek(fp, 0, SEEK_END); long sz = ftell(fp); fseek(fp, 0, SEEK_SET);
    std::vector<uint8_t> v(sz); fread(v.data(), 1, sz, fp); fclose(fp); return v;
}

static D3D12_HEAP_PROPERTIES heapProps(D3D12_HEAP_TYPE t) {
    D3D12_HEAP_PROPERTIES h{}; h.Type = t; h.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY_UNKNOWN;
    h.MemoryPoolPreference = D3D12_MEMORY_POOL_UNKNOWN; h.CreationNodeMask = 1; h.VisibleNodeMask = 1; return h;
}
static D3D12_RESOURCE_DESC bufDesc(UINT64 bytes, D3D12_RESOURCE_FLAGS f) {
    D3D12_RESOURCE_DESC d{}; d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; d.Width = bytes; d.Height = 1;
    d.DepthOrArraySize = 1; d.MipLevels = 1; d.Format = DXGI_FORMAT_UNKNOWN; d.SampleDesc.Count = 1;
    d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR; d.Flags = f; return d;
}

int main(int argc, char** argv) {
    const char* dxilPath = (argc > 1) ? argv[1] : "leaky_relu.dxil";
    const char* inPath   = (argc > 2) ? argv[2] : "S:\\qnn-project\\workspace\\onnx-interp\\io\\all\\ort_decoder_decoder_encode_norm1_Add_3_output_0.bin";
    const char* expPath  = (argc > 3) ? argv[3] : "S:\\qnn-project\\workspace\\onnx-interp\\io\\all\\ort_decoder_decoder_encode_actv_LeakyRelu_output_0.bin";
    float alpha          = (argc > 4) ? (float)atof(argv[4]) : 0.2f;

    Bin in = loadBin(inPath), exp = loadBin(expPath);
    long long ne = in.ne();
    printf("node: encode/actv/LeakyRelu  alpha=%.4f  ne=%lld  in.dims=", alpha, ne);
    for (auto d : in.dims) printf("%lldx", d); printf("\b \n");
    if (exp.ne() != ne) { printf("[FAIL] input/expected ne mismatch %lld vs %lld\n", ne, exp.ne()); return 1; }

    // f32 input -> fp16 (matches _49.Load<half>)
    std::vector<uint16_t> inHalf(ne);
    for (long long i = 0; i < ne; i++) inHalf[i] = XMConvertFloatToHalf(in.f[(size_t)i]);
    UINT64 bufBytes = (UINT64)ne * 2;

#if defined(_DEBUG)
    { ComPtr<ID3D12Debug> dbg; if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dbg)))) dbg->EnableDebugLayer(); }
#endif
    ComPtr<IDXGIFactory4> factory; HR(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));

    // --- adapter pick: first HW device with native-16bit shader ops; else WARP ---
    ComPtr<ID3D12Device> device; std::string adapterName = "(none)"; bool is16 = false, isWarp = false;
    auto tryAdapter = [&](IDXGIAdapter1* a, bool warp) -> bool {
        DXGI_ADAPTER_DESC1 ad{}; a->GetDesc1(&ad);
        if (!warp && (ad.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) return false;
        ComPtr<ID3D12Device> dev;
        if (FAILED(D3D12CreateDevice(a, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dev)))) return false;
        D3D12_FEATURE_DATA_D3D12_OPTIONS4 o4{};
        dev->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS4, &o4, sizeof(o4));
        if (!warp && !o4.Native16BitShaderOpsSupported) return false;  // need fp16 for this kernel
        device = dev; is16 = o4.Native16BitShaderOpsSupported; isWarp = warp;
        char nm[128]; size_t cv; wcstombs_s(&cv, nm, ad.Description, 127); adapterName = nm; return true;
    };
    for (UINT i = 0; ; i++) { ComPtr<IDXGIAdapter1> a; if (factory->EnumAdapters1(i, &a) == DXGI_ERROR_NOT_FOUND) break; if (tryAdapter(a.Get(), false)) break; }
    if (!device) { ComPtr<IDXGIAdapter1> wa; if (SUCCEEDED(factory->EnumWarpAdapter(IID_PPV_ARGS(&wa)))) tryAdapter(wa.Get(), true); }
    if (!device) { printf("[FAIL] no D3D12 device (FL12.0)\n"); return 1; }
    printf("adapter: %s  [%s, native16bit=%s]\n", adapterName.c_str(), isWarp ? "WARP" : "HW", is16 ? "yes" : "no");

    ComPtr<ID3D12CommandQueue> queue; D3D12_COMMAND_QUEUE_DESC qd{}; qd.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    HR(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)));
    ComPtr<ID3D12CommandAllocator> alloc; HR(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&alloc)));
    ComPtr<ID3D12GraphicsCommandList> list; HR(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, alloc.Get(), nullptr, IID_PPV_ARGS(&list)));
    ComPtr<ID3D12Fence> fence; HR(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
    HANDLE fenceEvt = CreateEvent(nullptr, FALSE, FALSE, nullptr);

    // --- root signature: [0]=6x32bit const @b0, [1]=raw SRV @t0, [2]=raw UAV @u0 ---
    D3D12_ROOT_PARAMETER rp[3]{};
    rp[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; rp[0].Constants.Num32BitValues = 6;
    rp[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;  // t0
    rp[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;  // u0
    for (auto& p : rp) p.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    D3D12_ROOT_SIGNATURE_DESC rsd{}; rsd.NumParameters = 3; rsd.pParameters = rp;
    ComPtr<ID3DBlob> rsBlob, rsErr;
    HR(D3D12SerializeRootSignature(&rsd, D3D_ROOT_SIGNATURE_VERSION_1_0, &rsBlob, &rsErr));
    ComPtr<ID3D12RootSignature> rootSig;
    HR(device->CreateRootSignature(0, rsBlob->GetBufferPointer(), rsBlob->GetBufferSize(), IID_PPV_ARGS(&rootSig)));

    std::vector<uint8_t> dxil = loadFile(dxilPath);
    D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc{}; psoDesc.pRootSignature = rootSig.Get();
    psoDesc.CS.pShaderBytecode = dxil.data(); psoDesc.CS.BytecodeLength = dxil.size();
    ComPtr<ID3D12PipelineState> pso; HR(device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&pso)));

    // --- buffers: input(upload), output(default UAV), readback ---
    ComPtr<ID3D12Resource> inBuf, outBuf, rbBuf;
    auto up = heapProps(D3D12_HEAP_TYPE_UPLOAD); auto def = heapProps(D3D12_HEAP_TYPE_DEFAULT); auto rb = heapProps(D3D12_HEAP_TYPE_READBACK);
    auto dIn = bufDesc(bufBytes, D3D12_RESOURCE_FLAG_NONE);
    auto dOut = bufDesc(bufBytes, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    auto dRb = bufDesc(bufBytes, D3D12_RESOURCE_FLAG_NONE);
    HR(device->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &dIn, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&inBuf)));
    HR(device->CreateCommittedResource(&def, D3D12_HEAP_FLAG_NONE, &dOut, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr, IID_PPV_ARGS(&outBuf)));
    HR(device->CreateCommittedResource(&rb, D3D12_HEAP_FLAG_NONE, &dRb, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&rbBuf)));
    { void* m = nullptr; D3D12_RANGE rr{0,0}; HR(inBuf->Map(0, &rr, &m)); memcpy(m, inHalf.data(), (size_t)bufBytes); inBuf->Unmap(0, nullptr); }

    // --- record ---
    struct PushConst_Simple { uint32_t KX, KY; float p1, p2, p3, p4; } pc{ (uint32_t)ne, 0, alpha, 0, 0, 0 };
    list->SetComputeRootSignature(rootSig.Get());
    list->SetPipelineState(pso.Get());
    list->SetComputeRoot32BitConstants(0, 6, &pc, 0);
    list->SetComputeRootShaderResourceView(1, inBuf->GetGPUVirtualAddress());
    list->SetComputeRootUnorderedAccessView(2, outBuf->GetGPUVirtualAddress());
    list->Dispatch((UINT)CEIL_DIV(ne, 512), 1, 1);
    D3D12_RESOURCE_BARRIER bar{}; bar.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    bar.Transition.pResource = outBuf.Get(); bar.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    bar.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS; bar.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    list->ResourceBarrier(1, &bar);
    list->CopyResource(rbBuf.Get(), outBuf.Get());
    HR(list->Close());
    ID3D12CommandList* lists[] = { list.Get() }; queue->ExecuteCommandLists(1, lists);
    HR(queue->Signal(fence.Get(), 1)); HR(fence->SetEventOnCompletion(1, fenceEvt)); WaitForSingleObject(fenceEvt, INFINITE);

    // --- readback fp16 -> f32, diff vs ORT f32 oracle ---
    std::vector<uint16_t> outHalf(ne);
    { void* m = nullptr; D3D12_RANGE rr{0, (SIZE_T)bufBytes}; HR(rbBuf->Map(0, &rr, &m)); memcpy(outHalf.data(), m, (size_t)bufBytes); D3D12_RANGE wr{0,0}; rbBuf->Unmap(0, &wr); }

    double maxd = 0, ss = 0, nb = 0; long long argmax = 0;
    for (long long i = 0; i < ne; i++) {
        float g = XMConvertHalfToFloat(outHalf[(size_t)i]); float o = exp.f[(size_t)i];
        double d = fabs((double)g - o); if (d > maxd) { maxd = d; argmax = i; }
        ss += d * d; nb += (double)o * o;
    }
    double rmse = sqrt(ss / ne), orms = sqrt(nb / ne), relRmse = orms > 0 ? rmse / orms : 0;
    printf("samples: gpu[0..3] =");
    for (int i = 0; i < 4 && i < ne; i++) printf(" %.5f", XMConvertHalfToFloat(outHalf[i]));
    printf("   ort[0..3] =");
    for (int i = 0; i < 4 && i < ne; i++) printf(" %.5f", exp.f[i]);
    printf("\n");
    printf("max|diff|=%.3e (@%lld)  rmse=%.3e  oracle_rms=%.3e  rel_rmse=%.3e\n", maxd, argmax, rmse, orms, relRmse);
    bool pass = relRmse < 1e-2;  // fp16 round-trip tolerance
    printf("==> %s  (fp16 tolerance rel_rmse<1e-2)\n", pass ? "FIRST LIGHT PASS \xE2\x9C\x93" : "MISMATCH");
    CloseHandle(fenceEvt);
    return pass ? 0 : 1;
}
