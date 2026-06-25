#include <windows.h>
#include <d3d12.h>
#include <iostream>
#include <stddef.h>

int main() {
    std::cout << "SizeOf: " << sizeof(D3D12_COMPUTE_PIPELINE_STATE_DESC) << "\n";
    std::cout << "OffsetOf_pRootSignature: " << offsetof(D3D12_COMPUTE_PIPELINE_STATE_DESC, pRootSignature) << "\n";
    std::cout << "OffsetOf_CS: " << offsetof(D3D12_COMPUTE_PIPELINE_STATE_DESC, CS) << "\n";
    std::cout << "OffsetOf_NodeMask: " << offsetof(D3D12_COMPUTE_PIPELINE_STATE_DESC, NodeMask) << "\n";
    std::cout << "OffsetOf_CachedPSO: " << offsetof(D3D12_COMPUTE_PIPELINE_STATE_DESC, CachedPSO) << "\n";
    std::cout << "OffsetOf_Flags: " << offsetof(D3D12_COMPUTE_PIPELINE_STATE_DESC, Flags) << "\n";
    return 0;
}
