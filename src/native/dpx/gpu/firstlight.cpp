// first-light: run native_hlsl leaky_relu.dxil on D3D12 compute, fp16, verify the math.
// Proves the dpx GPU backend thesis: our own kernel on D3D12, zero DirectML.
#include <windows.h>
#include <wrl/client.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <DirectXPackedVector.h>
#include <vector>
#include <cstdio>
#include <cmath>
#pragma comment(lib,"d3d12.lib")
#pragma comment(lib,"dxgi.lib")
#pragma comment(lib,"dxguid.lib")
using Microsoft::WRL::ComPtr;
using namespace DirectX::PackedVector;

#define CK(hr,msg) do{ HRESULT _h=(hr); if(FAILED(_h)){ printf("FAIL %s hr=0x%08lX\n",msg,(unsigned long)_h); return 1;} }while(0)

static std::vector<uint8_t> ReadAll(const char* p){
    FILE* f=fopen(p,"rb"); if(!f) return {};
    fseek(f,0,SEEK_END); long n=ftell(f); fseek(f,0,SEEK_SET);
    std::vector<uint8_t> b(n); fread(b.data(),1,n,f); fclose(f); return b;
}

static ComPtr<ID3D12Device> MakeDevice(){
    ComPtr<IDXGIFactory4> fac; CreateDXGIFactory2(0, IID_PPV_ARGS(&fac));
    ComPtr<IDXGIAdapter1> ad;
    for(UINT i=0; fac->EnumAdapters1(i,&ad)==S_OK; i++){
        DXGI_ADAPTER_DESC1 d; ad->GetDesc1(&d);
        if(d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        ComPtr<ID3D12Device> dv;
        if(SUCCEEDED(D3D12CreateDevice(ad.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dv)))){
            D3D12_FEATURE_DATA_D3D12_OPTIONS4 o4={}; dv->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS4,&o4,sizeof(o4));
            if(o4.Native16BitShaderOpsSupported){ wprintf(L"adapter: %s (native 16-bit: yes)\n", d.Description); return dv; }
        }
    }
    ComPtr<IDXGIAdapter> wa; fac->EnumWarpAdapter(IID_PPV_ARGS(&wa));
    ComPtr<ID3D12Device> dv; D3D12CreateDevice(wa.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dv));
    printf("adapter: WARP (no hw 16-bit device found)\n"); return dv;
}

int main(){
    ComPtr<ID3D12Device> dev = MakeDevice();
    if(!dev){ printf("no device\n"); return 1; }

    ComPtr<ID3D12CommandQueue> q;
    D3D12_COMMAND_QUEUE_DESC qd={}; qd.Type=D3D12_COMMAND_LIST_TYPE_COMPUTE;
    CK(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q)),"queue");
    ComPtr<ID3D12CommandAllocator> alloc;
    CK(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&alloc)),"alloc");
    ComPtr<ID3D12GraphicsCommandList> cl;
    CK(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, alloc.Get(), nullptr, IID_PPV_ARGS(&cl)),"cmdlist");

    // root sig: [0]=32bit constants b0(6) , [1]=root SRV t0 (input), [2]=root UAV u0 (output)
    D3D12_ROOT_PARAMETER rp[3]={};
    rp[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; rp[0].Constants.Num32BitValues=6;
    rp[1].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV;
    rp[2].ParameterType=D3D12_ROOT_PARAMETER_TYPE_UAV;
    D3D12_ROOT_SIGNATURE_DESC rsd={}; rsd.NumParameters=3; rsd.pParameters=rp;
    ComPtr<ID3DBlob> sig,err;
    if(FAILED(D3D12SerializeRootSignature(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err))){
        if(err) printf("rootsig: %s\n",(char*)err->GetBufferPointer()); return 1; }
    ComPtr<ID3D12RootSignature> root;
    CK(dev->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), IID_PPV_ARGS(&root)),"createroot");

    auto dxil = ReadAll("S:\\qnn-project\\workspace\\onnx-interp\\_spirv_test\\leaky_relu.dxil");
    if(dxil.empty()){ printf("no dxil\n"); return 1; }
    D3D12_COMPUTE_PIPELINE_STATE_DESC pd={}; pd.pRootSignature=root.Get();
    pd.CS.pShaderBytecode=dxil.data(); pd.CS.BytecodeLength=dxil.size();
    ComPtr<ID3D12PipelineState> pso;
    CK(dev->CreateComputePipelineState(&pd, IID_PPV_ARGS(&pso)),"createpso");
    printf("PSO built from leaky_relu.dxil (%zu bytes)\n", dxil.size());

    const int N=8; float in_f[N]={-3,-2,-1,0,1,2,3,4};
    std::vector<uint16_t> in_h(N), out_h(N);
    for(int i=0;i<N;i++) in_h[i]=XMConvertFloatToHalf(in_f[i]);
    UINT bytes=N*sizeof(uint16_t);

    auto buf=[&](D3D12_HEAP_TYPE ht, D3D12_RESOURCE_STATES st, D3D12_RESOURCE_FLAGS fl, ComPtr<ID3D12Resource>& r){
        D3D12_HEAP_PROPERTIES hp={}; hp.Type=ht;
        D3D12_RESOURCE_DESC rd={}; rd.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; rd.Width=bytes; rd.Height=1;
        rd.DepthOrArraySize=1; rd.MipLevels=1; rd.SampleDesc.Count=1; rd.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; rd.Flags=fl;
        return dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, st, nullptr, IID_PPV_ARGS(&r));
    };
    ComPtr<ID3D12Resource> inBuf,outBuf,rbBuf;
    CK(buf(D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ, D3D12_RESOURCE_FLAG_NONE, inBuf),"inbuf");
    CK(buf(D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, outBuf),"outbuf");
    CK(buf(D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, rbBuf),"rbbuf");
    { void* p=nullptr; D3D12_RANGE z={0,0}; inBuf->Map(0,&z,&p); memcpy(p,in_h.data(),bytes); inBuf->Unmap(0,nullptr); }

    cl->SetPipelineState(pso.Get());
    cl->SetComputeRootSignature(root.Get());
    UINT consts[6]={ (UINT)N, 1u, 0,0,0,0 }; float alpha=0.01f; memcpy(&consts[2],&alpha,4);
    cl->SetComputeRoot32BitConstants(0,6,consts,0);
    cl->SetComputeRootShaderResourceView(1, inBuf->GetGPUVirtualAddress());
    cl->SetComputeRootUnorderedAccessView(2, outBuf->GetGPUVirtualAddress());
    cl->Dispatch((N+511)/512,1,1);
    D3D12_RESOURCE_BARRIER b={}; b.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition.pResource=outBuf.Get();
    b.Transition.StateBefore=D3D12_RESOURCE_STATE_UNORDERED_ACCESS; b.Transition.StateAfter=D3D12_RESOURCE_STATE_COPY_SOURCE; b.Transition.Subresource=0;
    cl->ResourceBarrier(1,&b);
    cl->CopyResource(rbBuf.Get(), outBuf.Get());
    CK(cl->Close(),"close");
    ID3D12CommandList* lists[]={cl.Get()}; q->ExecuteCommandLists(1,lists);

    ComPtr<ID3D12Fence> fence; dev->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence));
    HANDLE ev=CreateEvent(nullptr,FALSE,FALSE,nullptr);
    q->Signal(fence.Get(),1); fence->SetEventOnCompletion(1,ev); WaitForSingleObject(ev,INFINITE);

    { void* p=nullptr; D3D12_RANGE r={0,bytes}; rbBuf->Map(0,&r,&p); memcpy(out_h.data(),p,bytes); rbBuf->Unmap(0,nullptr); }
    printf("\n idx     in      gpu_out    expected   ok\n");
    int allok=1;
    for(int i=0;i<N;i++){
        float o=XMConvertHalfToFloat(out_h[i]);
        float e= in_f[i]>0? in_f[i] : in_f[i]*0.01f;
        int ok=fabs(o-e)<0.01f; if(!ok)allok=0;
        printf("  %2d  %6.2f   %8.4f   %8.4f   %s\n", i, in_f[i], o, e, ok?"ok":"X");
    }
    printf(allok? "\nFIRST LIGHT: leaky_relu ran on D3D12 compute, fp16, output matches. Zero DirectML.\n"
                : "\nMISMATCH\n");
    return allok?0:2;
}
