// add.hlsl — GPU Elementwise Vector Addition: Y = A + B
cbuffer Params { uint Count; uint P1; uint P2; uint P3; };
ByteAddressBuffer A;     // t0 — fp32 [Count]
ByteAddressBuffer B;     // t1 — fp32 [Count]
RWByteAddressBuffer Y;   // u0 — fp32 [Count]

[numthreads(256, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint i = tid.x;
    if (i >= Count) return;

    float a = A.Load<float>(i * 4);
    float b = B.Load<float>(i * 4);
    Y.Store<float>(i * 4, a + b);
}
