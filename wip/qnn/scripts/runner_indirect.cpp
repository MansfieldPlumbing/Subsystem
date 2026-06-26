#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <iostream>
#include <fstream>
#include <vector>
#include "safetensors.h" 

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

#define CHECK(x) if(FAILED(x)) { std::cout << "ERROR at line " << __LINE__ << " HRESULT: " << std::hex << x << std::endl; exit(1); }

std::vector<uint8_t> ReadVec(const char* path) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file.is_open()) { std::cout << "ERROR: Cannot open " << path << std::endl; exit(1); }
    std::streamsize size = file.tellg();
    file.seekg(0, std::ios::beg);
    std::vector<uint8_t> buffer(size);
    file.read((char*)buffer.data(), size);
    return buffer;
}

struct OpConstants {
    uint32_t op_type; uint32_t weight_offset; uint32_t scale_offset;
    uint32_t in_offset; uint32_t out_offset;
    uint32_t K_dim; uint32_t out_channels;
};
struct IndirectCommand { OpConstants cb; D3D12_DISPATCH_ARGUMENTS dispatchArgs; };

int main() {
    ID3D12Device* device;
    CHECK(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)));
    ID3D12CommandQueue* queue;
    D3D12_COMMAND_QUEUE_DESC qDesc = { D3D12_COMMAND_LIST_TYPE_DIRECT };
    CHECK(device->CreateCommandQueue(&qDesc, IID_PPV_ARGS(&queue)));
    ID3D12CommandAllocator* allocator;
    CHECK(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)));
    ID3D12GraphicsCommandList* cmdList;
    CHECK(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, nullptr, IID_PPV_ARGS(&cmdList)));

    uint64_t header_len = 0;
    auto tensor_map = ParseSafetensorsHeader("C:\\bin\\qnn\\unet_out\\vega_unet.safetensors", header_len);
    
    std::string W_name = "down_blocks.0.attentions.0.transformer_blocks.0.attn1.to_q.weight";
    std::string S_name = W_name + ".scale";

    auto& info_W = tensor_map[W_name];
    auto& info_S = tensor_map[S_name];

    auto model_data = ReadVec("C:\\bin\\qnn\\unet_out\\vega_unet.safetensors");
    auto cso = ReadVec("C:\\bin\\qnn\\MegaMath.cso");

    // *** MODIFIED: Create dummy activations in-memory. No more file dependencies! ***
    uint32_t K_dim = (uint32_t)((info_W.end - info_W.start) / ((info_S.end - info_S.start)/4));
    std::vector<uint8_t> data_A(K_dim, 1);
    
    uint32_t out_channels = (uint32_t)(info_S.end - info_S.start) / 4;

    D3D12_ROOT_PARAMETER p[5];
    p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; p[0].Constants = {0,0,sizeof(OpConstants)/4};
    for(int i=1;i<=3;i++) { p[i].ParameterType=D3D12_ROOT_PARAMETER_TYPE_SRV; p[i].Descriptor={ (uint32_t)i-1,0}; }
    p[4].ParameterType=D3D12_ROOT_PARAMETER_TYPE_UAV; p[4].Descriptor={0,0};
    for(int i=0;i<5;i++) p[i].ShaderVisibility=D3D12_SHADER_VISIBILITY_ALL;
    D3D12_ROOT_SIGNATURE_DESC rsDesc = { 5, p, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
    ID3DBlob *sigBlob; CHECK(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &sigBlob, nullptr));
    ID3D12RootSignature* rootSig; CHECK(device->CreateRootSignature(0, sigBlob->GetBufferPointer(), sigBlob->GetBufferSize(), IID_PPV_ARGS(&rootSig)));
    
    D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {}; psoDesc.pRootSignature = rootSig; psoDesc.CS = {cso.data(), cso.size()};
    ID3D12PipelineState* pso; CHECK(device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&pso)));
    
    ID3D12Resource *bufModel, *bufA, *bufOut, *bufRb, *bufIndirect;
    D3D12_HEAP_PROPERTIES upHeap = { D3D12_HEAP_TYPE_UPLOAD };
    D3D12_HEAP_PROPERTIES defHeap = { D3D12_HEAP_TYPE_DEFAULT };
    D3D12_HEAP_PROPERTIES rbHeap = { D3D12_HEAP_TYPE_READBACK };
    
    D3D12_RESOURCE_DESC descModel = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, (UINT64)model_data.size(), 1 }; descModel.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    CHECK(device->CreateCommittedResource(&upHeap, D3D12_HEAP_FLAG_NONE, &descModel, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufModel)));
    
    D3D12_RESOURCE_DESC descA = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, (UINT64)data_A.size(), 1 }; descA.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    CHECK(device->CreateCommittedResource(&upHeap, D3D12_HEAP_FLAG_NONE, &descA, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufA)));

    D3D12_RESOURCE_DESC descOut = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, out_channels * 4, 1 }; descOut.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; descOut.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    CHECK(device->CreateCommittedResource(&defHeap, D3D12_HEAP_FLAG_NONE, &descOut, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr, IID_PPV_ARGS(&bufOut)));
    CHECK(device->CreateCommittedResource(&rbHeap, D3D12_HEAP_FLAG_NONE, &descOut, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&bufRb)));

    IndirectCommand cmd = {};
    cmd.cb.op_type = 0;
    cmd.cb.weight_offset = (uint32_t)(8 + header_len + info_W.start);
    cmd.cb.scale_offset  = (uint32_t)(8 + header_len + info_S.start);
    cmd.cb.in_offset = 0; cmd.cb.out_offset = 0;
    cmd.cb.K_dim = K_dim; cmd.cb.out_channels = out_channels;
    cmd.dispatchArgs.ThreadGroupCountX = out_channels / 64; 
    cmd.dispatchArgs.ThreadGroupCountY = 1; cmd.dispatchArgs.ThreadGroupCountZ = 1;
    
    D3D12_RESOURCE_DESC descInd = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, sizeof(IndirectCommand), 1 }; descInd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    CHECK(device->CreateCommittedResource(&upHeap, D3D12_HEAP_FLAG_NONE, &descInd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufIndirect)));
    
    void* ptr;
    bufModel->Map(0,nullptr,&ptr); memcpy(ptr, model_data.data(), model_data.size()); bufModel->Unmap(0,nullptr);
    bufA->Map(0,nullptr,&ptr); memcpy(ptr, data_A.data(), data_A.size()); bufA->Unmap(0,nullptr);
    bufIndirect->Map(0,nullptr,&ptr); memcpy(ptr, &cmd, sizeof(cmd)); bufIndirect->Unmap(0,nullptr);
    
    D3D12_INDIRECT_ARGUMENT_DESC args[2] = {};
    args[0].Type = D3D12_INDIRECT_ARGUMENT_TYPE_CONSTANT; args[0].Constant = {0,0,sizeof(OpConstants)/4};
    args[1].Type = D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH;
    D3D12_COMMAND_SIGNATURE_DESC sigDesc = {}; sigDesc.ByteStride=sizeof(IndirectCommand); sigDesc.NumArgumentDescs=2; sigDesc.pArgumentDescs=args;
    ID3D12CommandSignature* cmdSig; CHECK(device->CreateCommandSignature(&sigDesc, rootSig, IID_PPV_ARGS(&cmdSig)));
    
    cmdList->SetPipelineState(pso);
    cmdList->SetComputeRootSignature(rootSig);
    cmdList->SetComputeRootShaderResourceView(1, bufModel->GetGPUVirtualAddress());
    cmdList->SetComputeRootShaderResourceView(2, bufA->GetGPUVirtualAddress());
    cmdList->SetComputeRootShaderResourceView(3, bufModel->GetGPUVirtualAddress());
    cmdList->SetComputeRootUnorderedAccessView(4, bufOut->GetGPUVirtualAddress());
    cmdList->ExecuteIndirect(cmdSig, 1, bufIndirect, 0, nullptr, 0);

    D3D12_RESOURCE_BARRIER barrier = {}; barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = bufOut; barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS; barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    cmdList->ResourceBarrier(1, &barrier);
    cmdList->CopyResource(bufRb, bufOut);
    CHECK(cmdList->Close());

    ID3D12CommandList* lists[] = { cmdList };
    queue->ExecuteCommandLists(1, lists);

    ID3D12Fence* fence; CHECK(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
    HANDLE event = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    queue->Signal(fence, 1); fence->SetEventOnCompletion(1, event);
    WaitForSingleObject(event, INFINITE);

    float* pOut; CHECK(bufRb->Map(0, nullptr, (void**)&pOut));
    std::cout << "\n--- FULL SAFETENSORS MODEL EXECUTION ---" << std::endl;
    for (int i = 0; i < 10; i++) std::cout << "Channel " << i << ": " << pOut[i] << std::endl;
    bufRb->Unmap(0, nullptr);

    return 0;
}
