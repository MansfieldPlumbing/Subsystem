// naive fp32 GEMM — C[M,N] = A[M,K] @ B[K,N], row-major, one thread per output element.
// "just D3D12, our own GEMM" — the must-build keystone DieWorker left as a DirectML TODO.
// Off-road (naive) now; pave with tiled/groupshared mul_mm patterns later.
cbuffer Params { uint M; uint N; uint K; };
ByteAddressBuffer A;       // t0
ByteAddressBuffer B;       // t1
RWByteAddressBuffer C;     // u0

[numthreads(16, 16, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint row = tid.y, col = tid.x;
    if (row >= M || col >= N) return;
    float acc = 0.0f;
    for (uint k = 0; k < K; k++)
        acc += A.Load<float>((row * K + k) * 4) * B.Load<float>((k * N + col) * 4);
    C.Store<float>((row * N + col) * 4, acc);
}
