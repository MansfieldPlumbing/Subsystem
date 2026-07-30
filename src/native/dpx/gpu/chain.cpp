// chain first-light: GEMM(A@B) -> leaky_relu, two PSOs, one command list, a UAV barrier between.
// This is the frame-graph dispatch core: node output feeds the next dispatch on the GPU, no readback.
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
static std::vector<uint8_t> ReadAll(const char* p){ FILE* f=fopen(p,"rb"); if(!f) return {}; fseek(f,0,SEEK_END); long n=ftell(f); fseek(f,0,SEEK_SET); std::vector<uint8_t> b(n); fread(b.data(),1,n,f); fclose(f); return b; }
static ComPtr<ID3D12Device> MakeDevice(){
    ComPtr<IDXGIFactory4> fac; CreateDXGIFactory2(0,IID_PPV_ARGS(&fac)); ComPtr<IDXGIAdapter1> ad;
    for(UINT i=0; fac->EnumAdapters1(i,&ad)==S_OK; i++){ DXGI_ADAPTER_DESC1 d; ad->GetDesc1(&d); if(d.Flags&DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        ComPtr<ID3D12Device> dv; if(SUCCEEDED(D3D12CreateDevice(ad.Get(),D3D_FEATURE_LEVEL_12_0,IID_PPV_ARGS(&dv)))){ wprintf(L"adapter: %s\n",d.Description); return dv; } }
    ComPtr<IDXGIAdapter> wa; fac->EnumWarpAdapter(IID_PPV_ARGS(&wa)); ComPtr<ID3D12Device> dv; D3D12CreateDevice(wa.Get(),D3D_FEATURE_LEVEL_12_0,IID_PPV_ARGS(&dv)); printf("adapter: WARP\n"); return dv; }

int main(){
    const UINT M=2,K=2,N=2;
    float A[M*K]={1,2, 3,4};            // [2x2]
    float B[K*N]={1,-2, -3,1};          // [2x2]  -> A@B = [-5,0 ; -9,-2]
    float alpha=0.1f;
    float expect[M*N]={-0.5f,0.0f, -0.9f,-0.2f};   // leaky_relu(A@B, 0.1)
    std::vector<float> Cout(M*N,0);

    ComPtr<ID3D12Device> dev=MakeDevice(); if(!dev) return 1;
    ComPtr<ID3D12CommandQueue> q; D3D12_COMMAND_QUEUE_DESC qd={}; qd.Type=D3D12_COMMAND_LIST_TYPE_COMPUTE; CK(dev->CreateCommandQueue(&qd,IID_PPV_ARGS(&q)),"q");
    ComPtr<ID3D12CommandAllocator> al; CK(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE,IID_PPV_ARGS(&al)),"al");
    ComPtr<ID3D12GraphicsCommandList> cl; CK(dev->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_COMPUTE,al.Get(),nullptr,IID_PPV_ARGS(&cl)),"cl");

    auto mkRoot=[&](D3D12_ROOT_PARAMETER* rp,UINT n,ComPtr<ID3D12RootSignature>& r)->HRESULT{
        D3D12_ROOT_SIGNATURE_DESC rsd={}; rsd.NumParameters=n; rsd.pParameters=rp; ComPtr<ID3DBlob> s,e;
        HRESULT hr=D3D12SerializeRootSignature(&rsd,D3D_ROOT_SIGNATURE_VERSION_1,&s,&e); if(FAILED(hr)){ if(e) printf("rs:%s\n",(char*)e->GetBufferPointer()); return hr; }
        return dev->CreateRootSignature(0,s->GetBufferPointer(),s->GetBufferSize(),IID_PPV_ARGS(&r)); };

    D3D12_ROOT_PARAMETER gp[4]={};
    gp[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; gp[0].Constants.Num32BitValues=3;
    gp[1].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV; gp[1].Descriptor.ShaderRegister=0;
    gp[2].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV; gp[2].Descriptor.ShaderRegister=1;
    gp[3].ParameterType=D3D12_ROOT_PARAMETER_TYPE_UAV; gp[3].Descriptor.ShaderRegister=0;
    ComPtr<ID3D12RootSignature> rootG; CK(mkRoot(gp,4,rootG),"rootG");
    D3D12_ROOT_PARAMETER lp[2]={};
    lp[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; lp[0].Constants.Num32BitValues=2;
    lp[1].ParameterType=D3D12_ROOT_PARAMETER_TYPE_UAV; lp[1].Descriptor.ShaderRegister=0;
    ComPtr<ID3D12RootSignature> rootL; CK(mkRoot(lp,2,rootL),"rootL");

    auto mkPso=[&](const char* path,ID3D12RootSignature* rs,ComPtr<ID3D12PipelineState>& p)->HRESULT{
        auto d=ReadAll(path); if(d.empty()){ printf("no %s\n",path); return E_FAIL; }
        D3D12_COMPUTE_PIPELINE_STATE_DESC pd={}; pd.pRootSignature=rs; pd.CS.pShaderBytecode=d.data(); pd.CS.BytecodeLength=d.size();
        return dev->CreateComputePipelineState(&pd,IID_PPV_ARGS(&p)); };
    ComPtr<ID3D12PipelineState> psoG,psoL;
    CK(mkPso("S:\\qnn-project\\workspace\\onnx-interp\\_gpu\\gemm.dxil",rootG.Get(),psoG),"psoG");
    CK(mkPso("S:\\qnn-project\\workspace\\onnx-interp\\_gpu\\leaky_f32.dxil",rootL.Get(),psoL),"psoL");
    printf("two PSOs built (gemm + leaky_f32)\n");

    auto buf=[&](D3D12_HEAP_TYPE ht,D3D12_RESOURCE_STATES st,D3D12_RESOURCE_FLAGS fl,UINT bytes,ComPtr<ID3D12Resource>&r){
        D3D12_HEAP_PROPERTIES hp={}; hp.Type=ht; D3D12_RESOURCE_DESC rd={}; rd.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; rd.Width=bytes; rd.Height=1; rd.DepthOrArraySize=1; rd.MipLevels=1; rd.SampleDesc.Count=1; rd.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; rd.Flags=fl;
        return dev->CreateCommittedResource(&hp,D3D12_HEAP_FLAG_NONE,&rd,st,nullptr,IID_PPV_ARGS(&r)); };
    ComPtr<ID3D12Resource> aBuf,bBuf,cBuf,rbBuf;
    CK(buf(D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE,sizeof(A),aBuf),"a");
    CK(buf(D3D12_HEAP_TYPE_UPLOAD,D3D12_RESOURCE_STATE_GENERIC_READ,D3D12_RESOURCE_FLAG_NONE,sizeof(B),bBuf),"b");
    CK(buf(D3D12_HEAP_TYPE_DEFAULT,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,(UINT)(Cout.size()*4),cBuf),"c");
    CK(buf(D3D12_HEAP_TYPE_READBACK,D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_FLAG_NONE,(UINT)(Cout.size()*4),rbBuf),"rb");
    { void* p; D3D12_RANGE z={0,0}; aBuf->Map(0,&z,&p); memcpy(p,A,sizeof(A)); aBuf->Unmap(0,nullptr); bBuf->Map(0,&z,&p); memcpy(p,B,sizeof(B)); bBuf->Unmap(0,nullptr); }

    // pass 1: GEMM -> C
    cl->SetPipelineState(psoG.Get()); cl->SetComputeRootSignature(rootG.Get());
    UINT gc[3]={M,N,K}; cl->SetComputeRoot32BitConstants(0,3,gc,0);
    cl->SetComputeRootShaderResourceView(1,aBuf->GetGPUVirtualAddress());
    cl->SetComputeRootShaderResourceView(2,bBuf->GetGPUVirtualAddress());
    cl->SetComputeRootUnorderedAccessView(3,cBuf->GetGPUVirtualAddress());
    cl->Dispatch((N+15)/16,(M+15)/16,1);
    // UAV barrier: GEMM writes to C must be visible to the leaky dispatch (the push edge)
    D3D12_RESOURCE_BARRIER uav={}; uav.Type=D3D12_RESOURCE_BARRIER_TYPE_UAV; uav.UAV.pResource=cBuf.Get(); cl->ResourceBarrier(1,&uav);
    // pass 2: leaky_relu in-place on C
    cl->SetPipelineState(psoL.Get()); cl->SetComputeRootSignature(rootL.Get());
    UINT lc[2]={M*N,0}; memcpy(&lc[1],&alpha,4); cl->SetComputeRoot32BitConstants(0,2,lc,0);
    cl->SetComputeRootUnorderedAccessView(1,cBuf->GetGPUVirtualAddress());
    cl->Dispatch((M*N+63)/64,1,1);

    D3D12_RESOURCE_BARRIER tb={}; tb.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; tb.Transition.pResource=cBuf.Get();
    tb.Transition.StateBefore=D3D12_RESOURCE_STATE_UNORDERED_ACCESS; tb.Transition.StateAfter=D3D12_RESOURCE_STATE_COPY_SOURCE; tb.Transition.Subresource=0;
    cl->ResourceBarrier(1,&tb); cl->CopyResource(rbBuf.Get(),cBuf.Get()); CK(cl->Close(),"close");
    ID3D12CommandList* L[]={cl.Get()}; q->ExecuteCommandLists(1,L);
    ComPtr<ID3D12Fence> fence; dev->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence));
    HANDLE ev=CreateEvent(nullptr,FALSE,FALSE,nullptr); q->Signal(fence.Get(),1); fence->SetEventOnCompletion(1,ev); WaitForSingleObject(ev,INFINITE);
    { void* p; D3D12_RANGE r={0,(SIZE_T)(Cout.size()*4)}; rbBuf->Map(0,&r,&p); memcpy(Cout.data(),p,Cout.size()*4); rbBuf->Unmap(0,nullptr); }

    printf("\n leaky_relu(A@B) = [%.2f %.2f ; %.2f %.2f]   expect [-0.5 0 ; -0.9 -0.2]\n", Cout[0],Cout[1],Cout[2],Cout[3]);
    int ok=1; for(UINT i=0;i<M*N;i++) if(fabs(Cout[i]-expect[i])>1e-4f) ok=0;
    printf(ok? "CHAIN FIRST LIGHT: 2-node GPU pipeline (GEMM->leaky), UAV-barrier push edge, output matches. The frame-graph dispatch core works.\n" : "MISMATCH\n");
    return ok?0:2;
}
