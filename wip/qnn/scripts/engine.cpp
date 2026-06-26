#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <iostream>
#include <fstream>
#include <vector>
#include <string>
#include <unordered_map>
#include <chrono>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

#define CHECK(x) if(FAILED(x)) { std::cout << "ERROR at line " << __LINE__ << " HRESULT: " << std::hex << x << std::endl; exit(1); }

// --- SAFETENSORS PARSER ---
struct TensorInfo {
    long long start;
    long long end;
};

std::unordered_map<std::string, TensorInfo> ParseSafetensors(const char* path, uint64_t& header_len_out) {
    std::unordered_map<std::string, TensorInfo> tensors;
    std::ifstream file(path, std::ios::binary);
    if (!file.is_open()) { std::cout << "FATAL: Could not open " << path << "\n"; exit(1); }

    uint64_t json_len = 0;
    file.read((char*)&json_len, sizeof(uint64_t));
    header_len_out = json_len;

    std::string json_str(json_len, '\0');
    file.read(&json_str[0], json_len);

    size_t pos = 0;
    while ((pos = json_str.find("\"", pos)) != std::string::npos) {
        size_t name_start = pos + 1;
        size_t name_end = json_str.find("\"", name_start);
        std::string name = json_str.substr(name_start, name_end - name_start);
        pos = name_end + 1;

        if (json_str.substr(pos, 2) != ":{") continue;

        TensorInfo info;
        size_t offsets_pos = json_str.find("\"data_offsets\":[", name_end);
        if (offsets_pos != std::string::npos) {
            sscanf_s(json_str.c_str() + offsets_pos, "\"data_offsets\":[%lld,%lld]", &info.start, &info.end);
            tensors[name] = info;
        }
    }
    return tensors;
}

std::vector<uint8_t> ReadBlob(const char* path) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file.is_open()) { std::cout << "FATAL: Could not open " << path << std::endl; exit(1); }
    std::streamsize size = file.tellg();
    file.seekg(0, std::ios::beg);
    std::vector<uint8_t> buffer(size);
    file.read((char*)buffer.data(), size);
    return buffer;
}

// --- D3D12 UBERSHADER STRUCTS ---
struct OpConstants {
    uint32_t op_type; uint32_t weight_offset; uint32_t scale_offset;
    uint32_t in_offset; uint32_t out_offset;
    uint32_t K_dim; uint32_t out_channels;
};

struct IndirectCommand { 
    OpConstants cb; 
    D3D12_DISPATCH_ARGUMENTS dispatchArgs; 
};

int main() {
    std::cout << "[*] Booting GFX900 Native Inference Engine...\n";
    
    // 1. INIT D3D12
    ID3D12Device* device;
    CHECK(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)));
    ID3D12CommandQueue* queue;
    D3D12_COMMAND_QUEUE_DESC qDesc = { D3D12_COMMAND_LIST_TYPE_DIRECT };
    CHECK(device->CreateCommandQueue(&qDesc, IID_PPV_ARGS(&queue)));
    ID3D12CommandAllocator* allocator;
    CHECK(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)));
    ID3D12GraphicsCommandList* cmdList;
    CHECK(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, nullptr, IID_PPV_ARGS(&cmdList)));

    // 2. PARSE THE REAL MODEL
    uint64_t header_len = 0;
    std::string model_path = "C:\\bin\\qnn\\unet_out\\vega_unet.safetensors";
    auto tensor_map = ParseSafetensors(model_path.c_str(), header_len);
    
    std::cout << "[*] Loading " << model_path << " into memory...\n";
    auto model_data = ReadBlob(model_path.c_str());
    auto cso = ReadBlob("C:\\bin\\qnn\\MegaMath.cso");

    // 3. BUILD THE EXECUTION GRAPH (Indirect Commands)
    std::vector<IndirectCommand> cmdBuffer;
    int layer_idx = 0;
    
    // We create a Ping-Pong execution flow through our activation heap
    uint32_t ping_offset = 0;
    uint32_t pong_offset = 16 * 1024 * 1024; // 16MB offset
    
    for (const auto& kv : tensor_map) {
        std::string name = kv.first;
        if (name.find(".weight") != std::string::npos && name.find(".scale") == std::string::npos) {
            std::string scale_name = name + ".scale";
            if (tensor_map.count(scale_name)) {
                auto& w_info = kv.second;
                auto& s_info = tensor_map[scale_name];

                uint32_t out_channels = (uint32_t)(s_info.end - s_info.start) / 4;
                uint32_t K_dim = (uint32_t)(w_info.end - w_info.start) / out_channels;

                IndirectCommand cmd = {};
                cmd.cb.op_type = 0; // Matrix Multiplication mapped to V_PK_MAD_I16
                cmd.cb.weight_offset = (uint32_t)(8 + header_len + w_info.start);
                cmd.cb.scale_offset  = (uint32_t)(8 + header_len + s_info.start);
                
                // Alternate reading/writing to avoid race conditions
                cmd.cb.in_offset  = (layer_idx % 2 == 0) ? ping_offset : pong_offset;
                cmd.cb.out_offset = (layer_idx % 2 == 0) ? pong_offset : ping_offset;
                
                cmd.cb.K_dim = K_dim; 
                cmd.cb.out_channels = out_channels;
                
                cmd.dispatchArgs.ThreadGroupCountX = (out_channels + 63) / 64; 
                cmd.dispatchArgs.ThreadGroupCountY = 1; 
                cmd.dispatchArgs.ThreadGroupCountZ = 1;
                
                cmdBuffer.push_back(cmd);
                layer_idx++;
            }
        }
    }
    std::cout << "[+] Hardware Command Buffer compiled. Layers queued: " << cmdBuffer.size() << "\n";

    // 4. BIND SHADER & ROOTS
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
    
    // 5. ALLOCATE VRAM
    D3D12_HEAP_PROPERTIES upHeap = { D3D12_HEAP_TYPE_UPLOAD };
    D3D12_HEAP_PROPERTIES defHeap = { D3D12_HEAP_TYPE_DEFAULT };
    D3D12_HEAP_PROPERTIES rbHeap = { D3D12_HEAP_TYPE_READBACK };
    
    ID3D12Resource *bufModel, *bufActs, *bufRb, *bufIndirect;
    
    // The Safetensors file (821MB)
    D3D12_RESOURCE_DESC descModel = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, (UINT64)model_data.size(), 1 }; descModel.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    CHECK(device->CreateCommittedResource(&upHeap, D3D12_HEAP_FLAG_NONE, &descModel, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufModel)));
    
    // The Activation Arena (32MB VRAM for Ping-Ponging outputs)
    D3D12_RESOURCE_DESC descActs = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, 32 * 1024 * 1024, 1 }; descActs.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; descActs.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    CHECK(device->CreateCommittedResource(&defHeap, D3D12_HEAP_FLAG_NONE, &descActs, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr, IID_PPV_ARGS(&bufActs)));
    
    // CPU Readback Buffer (FIXED: NO UAV FLAGS ALLOWED HERE!)
    D3D12_RESOURCE_DESC descRb = descActs; descRb.Flags = D3D12_RESOURCE_FLAG_NONE; 
    CHECK(device->CreateCommittedResource(&rbHeap, D3D12_HEAP_FLAG_NONE, &descRb, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&bufRb)));

    // ExecuteIndirect Hardware Command Buffer
    D3D12_RESOURCE_DESC descInd = { D3D12_RESOURCE_DIMENSION_BUFFER, 0, cmdBuffer.size() * sizeof(IndirectCommand), 1 }; descInd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    CHECK(device->CreateCommittedResource(&upHeap, D3D12_HEAP_FLAG_NONE, &descInd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&bufIndirect)));
    
    // Map data to GPU
    void* ptr;
    bufModel->Map(0,nullptr,&ptr); memcpy(ptr, model_data.data(), model_data.size()); bufModel->Unmap(0,nullptr);
    bufIndirect->Map(0,nullptr,&ptr); memcpy(ptr, cmdBuffer.data(), cmdBuffer.size() * sizeof(IndirectCommand)); bufIndirect->Unmap(0,nullptr);
    
    D3D12_INDIRECT_ARGUMENT_DESC args[2] = {};
    args[0].Type = D3D12_INDIRECT_ARGUMENT_TYPE_CONSTANT; args[0].Constant = {0,0,sizeof(OpConstants)/4};
    args[1].Type = D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH;
    D3D12_COMMAND_SIGNATURE_DESC sigDesc = {}; sigDesc.ByteStride=sizeof(IndirectCommand); sigDesc.NumArgumentDescs=2; sigDesc.pArgumentDescs=args;
    ID3D12CommandSignature* cmdSig; CHECK(device->CreateCommandSignature(&sigDesc, rootSig, IID_PPV_ARGS(&cmdSig)));
    
    // 6. FIRE THE UBERSHADER!
    std::cout << "[*] Executing full Ubershader graph on Vega GPU...\n";
    auto t_start = std::chrono::high_resolution_clock::now();

    cmdList->SetPipelineState(pso);
    cmdList->SetComputeRootSignature(rootSig);
    cmdList->SetComputeRootShaderResourceView(1, bufModel->GetGPUVirtualAddress()); // Weights
    cmdList->SetComputeRootShaderResourceView(2, bufActs->GetGPUVirtualAddress());  // Inputs (Act Arena)
    cmdList->SetComputeRootShaderResourceView(3, bufModel->GetGPUVirtualAddress()); // Scales
    cmdList->SetComputeRootUnorderedAccessView(4, bufActs->GetGPUVirtualAddress()); // Outputs (Act Arena)

    // THE MAGIC CALL: This fires off ALL 100+ neural network layers instantly
    cmdList->ExecuteIndirect(cmdSig, (UINT)cmdBuffer.size(), bufIndirect, 0, nullptr, 0);

    D3D12_RESOURCE_BARRIER barrier = {}; barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = bufActs; barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS; barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    cmdList->ResourceBarrier(1, &barrier);
    cmdList->CopyResource(bufRb, bufActs);
    CHECK(cmdList->Close());

    ID3D12CommandList* lists[] = { cmdList };
    queue->ExecuteCommandLists(1, lists);

    ID3D12Fence* fence; CHECK(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
    HANDLE event = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    queue->Signal(fence, 1); fence->SetEventOnCompletion(1, event);
    WaitForSingleObject(event, INFINITE);

    auto t_end = std::chrono::high_resolution_clock::now();
    double elapsed_ms = std::chrono::duration<double, std::milli>(t_end - t_start).count();

    float* pOut; CHECK(bufRb->Map(0, nullptr, (void**)&pOut));
    std::cout << "\n=================================================" << std::endl;
    std::cout << " [V] EXECUTION COMPLETE" << std::endl;
    std::cout << "=================================================" << std::endl;
    std::cout << " -> Layers Computed: " << cmdBuffer.size() << std::endl;
    std::cout << " -> Execution Time:  " << elapsed_ms << " ms" << std::endl;
    std::cout << " -> Hardware:        Native AMD GFX900 (Vega 10)\n";
    bufRb->Unmap(0, nullptr);

    return 0;
}
