// GEMM first-light: run our gemm.dxil on D3D12, verify A[2x3]@B[3x2] = [[4,5],[10,11]] (the dp-onnx selftest matrix).
#include <windows.h>
#include <wrl/client.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <vector>
#include <cstdio>
#include <cmath>
#pragma comment(lib,"d3d12.lib")
#pragma comment(lib,"dxgi.lib")
#pragma comment(lib,"dxguid.lib")
using Microsoft::WRL::ComPtr;
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
        if(SUCCEEDED(D3D12CreateDevice(ad.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dv)))){ wprintf(L"adapter: %s\n", d.Description); return dv; }
    }
    ComPtr<IDXGIAdapter> wa; fac->EnumWarpAdapter(IID_PPV_ARGS(&wa));
    ComPtr<ID3D12Device> dv; D3D12CreateDevice(wa.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&dv)); printf("adapter: WARP\n"); return dv;
}

int main(){
    const UINT M=2, N=2, K=3;
    float A[M*K]={1,2,3, 4,5,6};        // [2x3]
    float B[K*N]={1,0, 0,1, 1,1};       // [3x2]
    float expect[M*N]={4,5, 10,11};
    std::vector<float> C(M*N,0);

    ComPtr<ID3D12Device> dev=MakeDevice(); if(!dev) return 1;
    ComPtr<ID3D12CommandQueue> q; D3D12_COMMAND_QUEUE_DESC qd={}; qd.Type=D3D12_COMMAND_LIST_TYPE_COMPUTE;
    CK(dev->CreateCommandQueue(&qd,IID_PPV_ARGS(&q)),"queue");
    ComPtr<ID3D12CommandAllocator> alloc; CK(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE,IID_PPV_ARGS(&alloc)),"alloc");
    ComPtr<ID3D12GraphicsCommandList> cl; CK(dev->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_COMPUTE,alloc.Get(),nullptr,IID_PPV_ARGS(&cl)),"cl");

    // root: [0] consts b0(3) , [1] SRV t0 (A), [2] SRV t1 (B), [3] UAV u0 (C)
    D3D12_ROOT_PARAMETER rp[4]={};
    rp[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; rp[0].Constants.Num32BitValues=3;
    rp[1].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV; rp[1].Descriptor.ShaderRegister=0;
    rp[2].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV; rp[2].Descriptor.ShaderRegister=1;
    rp[3].ParameterType=D3D12_ROOT_PARAMETER_TYPE_UAV; rp[3].Descriptor.ShaderRegister=0;
    D3D12_ROOT_SIGNATURE_DESC rsd={}; rsd.NumParameters=4; rsd.pParameters=rp;
    ComPtr<ID3DBlob> sig,err;
    if(FAILED(D3D12SerializeRootSignature(&rsd,D3D_ROOT_SIGNATURE_VERSION_1,&sig,&err))){ if(err) printf("rs: %s\n",(char*)err->GetBufferPointer()); return 1; }
    ComPtr<ID3D12RootSignature> root; CK(dev->CreateRootSignature(0,sig->GetBufferPointer(),sig->GetBufferSize(),IID_PPV_ARGS(&root)),"root");

    auto dxil=ReadAll("S:\\qnn-project\\workspace\\onnx-interp\\_gpu\\gemm.dxil");
    if(dxil.empty()){ printf("no gemm.dxil\n"); return 1; }
    D3D12_COMPUTE_PIPELINE_STATE_DESC pd={}; pd.pRootSignature=root.Get(); pd.CS.pShaderBytecode=dxil.data(); pd.CS.BytecodeLength=dxil.size();
    ComPtr<ID3D12PipelineState> pso; CK(dev->CreateComputePipelineState(&pd,IID_PPV_ARGS(&pso)),"pso");
    printf("PSO built from gemm.dxil (%zu bytes)\n", dxil.size());

    auto buf=[&](D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl,UINT bytes,ComPtr<ID3D12Resource>&r){
        D3D12_HEAP_PROPERTIES hp={}; hp.Type=ht;
        D3D12_RESOURCE_DESC rd={}; rd.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; rd.Width=bytes; rd.Height=1; rd.DepthOrArraySize=1; rd.MipLevels=1; rd.SampleDesc.Count=1; rd.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; rd.Flags=fl;
        return dev->CreateCommittedResource(&hp,D3D12_HEAP_FLAG_NONE,&rd,st,nullptr,IID_PPV_ARGS(&r)); };
    ComPtr<ID3D12Resource> aBuf,bBuf,cBuf,rbBuf;
    CK(buf(D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE,sizeof(A),aBuf),"a");
    CK(buf(D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE,sizeof(B),bBuf),"b");
    CK(buf(D3D12_HEAP_TYPE_DEFAULT,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,(UINT)(C.size()*4),cBuf),"c");
    CK(buf(D3D12_HEAP_TYPE_READBACK,D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_FLAG_NONE,(UINT)(C.size()*4),rbBuf),"rb");
    { void* p; D3D12_RANGE z={0,0}; aBuf->Map(0,&z,&p); memcpy(p,A,sizeof(A)); aBuf->Unmap(0,nullptr);
      bBuf->Map(0,&z,&p); memcpy(p,B,sizeof(B)); bBuf->Unmap(0,nullptr); }

    cl->SetPipelineState(pso.Get()); cl->SetComputeRootSignature(root.Get());
    UINT consts[3]={M,N,K}; cl->SetComputeRoot32BitConstants(0,3,consts,0);
    cl->SetComputeRootShaderResourceView(1,aBuf->GetGPUVirtualAddress());
    cl->SetComputeRootShaderResourceView(2,bBuf->GetGPUVirtualAddress());
    cl->SetComputeRootUnorderedAccessView(3,cBuf->GetGPUVirtualAddress());
    cl->Dispatch((N+15)/16,(M+15)/16,1);
    D3D12_RESOURCE_BARRIER ba={}; ba.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; ba.Transition.pResource=cBuf.Get();
    ba.Transition.StateBefore=D3D12_RESOURCE_STATE_UNORDERED_ACCESS; ba.Transition.StateAfter=D3D12_RESOURCE_STATE_COPY_SOURCE; ba.Transition.Subresource=0;
    cl->ResourceBarrier(1,&ba); cl->CopyResource(rbBuf.Get(),cBuf.Get()); CK(cl->Close(),"close");
    ID3D12CommandList* L[]={cl.Get()}; q->ExecuteCommandLists(1,L);
    ComPtr<ID3D12Fence> fence; dev->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence));
    HANDLE ev=CreateEvent(nullptr,FALSE,FALSE,nullptr); q->Signal(fence.Get(),1); fence->SetEventOnCompletion(1,ev); WaitForSingleObject(ev,INFINITE);
    { void* p; D3D12_RANGE r={0,(SIZE_T)(C.size()*4)}; rbBuf->Map(0,&r,&p); memcpy(C.data(),p,C.size()*4); rbBuf->Unmap(0,nullptr); }

    printf("\n A[2x3]@B[3x2]:  C = [%.1f %.1f ; %.1f %.1f]   expect [4 5 ; 10 11]\n", C[0],C[1],C[2],C[3]);
    int ok=1; for(UINT i=0;i<M*N;i++) if(fabs(C[i]-expect[i])>1e-4f) ok=0;
    printf(ok? "GEMM FIRST LIGHT: our fp32 GEMM ran on D3D12, matches. Conv/MatMul are now unblocked.\n" : "MISMATCH\n");
    return ok?0:2;
}
