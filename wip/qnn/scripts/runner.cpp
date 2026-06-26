#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <iostream>
#include <fstream>
#include <vector>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

#define CHECK(x) if(FAILED(x)) { std::cout << "ERROR at line " << __LINE__ << " HRESULT: " << std::hex << x << std::endl; exit(1); }

std::vector<uint8_t> ReadFile(const char* path) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file.is_open()) { std::cout << "ERROR: Cannot open " << path << std::endl; exit(1); }
    std::streamsize size = file.tellg();
    file.seekg(0, std::ios::beg);
    std::vector<uint8_t> buffer(size);
    file.read((char*)buffer.data(), size);
    return buffer;
}

int main() {
    ID3D12Device* device;
    CHECK(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)));
    
    ID3D12CommandQueue* queue;
    D3D12_COMMAND_QUEUE_DESC qDesc = { D3D12_COMMAND_LIST_TYPE_DIRECT, 0, D3D12_COMMAND_QUEUE_FLAG_NONE, 0 };
    CHECK(device->CreateCommandQueue(&qDesc, IID_PPV_ARGS(&queue)));

    ID3D12CommandAllocator* allocator;
    CHECK(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)));
    
    ID3D12GraphicsCommandList* cmdList;
    CHECK(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, nullptr, IID_PPV_ARGS(&cmdList)));

    auto shader_cso = ReadFile("matmul_s8_gfx900.cso");

    D3D12_ROOT_PARAMETER rootParams[4];
    rootParams[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; // t0: Weights
    rootParams[0].Descriptor = {0, 0};
    rootParams[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    rootParams[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; // t1: Acts
    rootParams[1].Descriptor = {1, 0};
    rootParams[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    rootParams[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; // t2: Scales
    rootParams[2].Descriptor = {2, 0};
    rootParams[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    rootParams[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV; // u0: Output
    rootParams[3].Descriptor = {0, 0};
    rootParams[3].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

    D3D12_ROOT_SIGNATURE_DESC rsDesc = { 4, rootParams, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
    ID3DBlob *sigBlob, *errBlob;
    D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &sigBlob, &errBlob);
    ID3D12RootSignature* rootSig;
    CHECK(device->CreateRootSignature(0, sigBlob->GetBufferPointer(), sigBlob->GetBufferSize(), IID_PPV_ARGS(&rootSig)));

    D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {};
    psoDesc.pRootSignature = rootSig;
    psoDesc.CS = { shader_cso.data(), shader_cso.size() };
    ID3D12PipelineState* pso;
    CHECK(device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&pso)));

    // Load our liberated assets
    auto data_W = ReadFile("C:\\bin\\llama-trace\\gfx900_test\\midblock_1280_LINEAR.bin");
    auto data_A = ReadFile("C:\\bin\\llama-trace\\gfx900_test\\dummy_activations_s8.bin");
    auto data_S = ReadFile("C:\\bin\\llama-trace\\gfx900_test\\scales_fp32.bin");
    
    D3D12_HEAP_PROPERTIES uploadHeap = { D3D12_HEAP_TYPE_UPLOAD, D3D12_CPU_PAGE_PROPERTY_UNKNOWN, D3D12_MEMORY_POOL_UNKNOWN, 1, 1 };
    D3D12_HEAP_PROPERTIES defaultHeap = { D3D12_HEAP_TYPE_DEFAULT, D3D12_CPU_PAGE_PROPERTY_UNKNOWN, D3D12_MEMORY_POOL_UNKNOWN, 1, 1 };
    D3D12_HEAP_PROPERTIES readbackHeap = { D3D12_HEAP_TYPE_READBACK, D3D12_CPU_PAGE_PROPERTY_UNKNOWN, D3D12_MEMORY_POOL_UNKNOWN, 1, 1 };

    ID3D12Resource *bufW, *bufA, *bufS, *bufOut, *bufRb;
    
    D3D12_RESOURCE_DESC descW = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, (UINT64)data_W.size(), 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1,0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_RESOURCE_FLAG_NONE };
    CHECK(device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &descW, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufW)));
    D3D12_RESOURCE_DESC descA = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, (UINT64)data_A.size(), 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1,0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_RESOURCE_FLAG_NONE };
    CHECK(device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &descA, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufA)));
    D3D12_RESOURCE_DESC descS = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, (UINT64)data_S.size(), 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1,0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_RESOURCE_FLAG_NONE };
    CHECK(device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &descS, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufS)));
    D3D12_RESOURCE_DESC descOut = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, 1280 * 4, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1,0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS };
    CHECK(device->CreateCommittedResource(&defaultHeap, D3D12_HEAP_FLAG_NONE, &descOut, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr, IID_PPV_ARGS(&bufOut)));
    D3D12_RESOURCE_DESC descRb = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, 1280 * 4, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1,0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_RESOURCE_FLAG_NONE };
    CHECK(device->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &descRb, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&bufRb)));

    void* ptr; 
    bufW->Map(0, nullptr, &ptr); memcpy(ptr, data_W.data(), data_W.size()); bufW->Unmap(0, nullptr);
    bufA->Map(0, nullptr, &ptr); memcpy(ptr, data_A.data(), data_A.size()); bufA->Unmap(0, nullptr);
    bufS->Map(0, nullptr, &ptr); memcpy(ptr, data_S.data(), data_S.size()); bufS->Unmap(0, nullptr);

    cmdList->SetPipelineState(pso);
    cmdList->SetComputeRootSignature(rootSig);
    cmdList->SetComputeRootShaderResourceView(0, bufW->GetGPUVirtualAddress());
    cmdList->SetComputeRootShaderResourceView(1, bufA->GetGPUVirtualAddress());
    cmdList->SetComputeRootShaderResourceView(2, bufS->GetGPUVirtualAddress());
    cmdList->SetComputeRootUnorderedAccessView(3, bufOut->GetGPUVirtualAddress());

    cmdList->Dispatch(20, 1, 1);

    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = bufOut;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    cmdList->ResourceBarrier(1, &barrier);

    cmdList->CopyResource(bufRb, bufOut);
    CHECK(cmdList->Close());

    ID3D12CommandList* lists[] = { cmdList };
    queue->ExecuteCommandLists(1, lists);

    ID3D12Fence* fence;
    CHECK(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
    HANDLE event = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    queue->Signal(fence, 1);
    fence->SetEventOnCompletion(1, event);
    WaitForSingleObject(event, INFINITE);

    float* pOut;
    CHECK(bufRb->Map(0, nullptr, (void**)&pOut));
    std::cout << "--- GFX900 DE-SWIZZLED INFERENCE SUCCESS ---" << std::endl;
    for (int i = 0; i < 10; i++) {
        std::cout << "Channel " << i << ": " << pOut[i] << std::endl;
    }
    bufRb->Unmap(0, nullptr);

    return 0;
}
