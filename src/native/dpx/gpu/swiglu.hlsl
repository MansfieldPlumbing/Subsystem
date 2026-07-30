// swiglu.hlsl — GPU SwiGLU Feed-Forward Activation: Y[i] = (Gate[i] / (1 + exp(-Gate[i]))) * Up[i]
cbuffer Params { uint Count; uint P1; uint P2; uint P3; };
ByteAddressBuffer Gate;   // t0 — fp32 [Count]
ByteAddressBuffer Up;     // t1 — fp32 [Count]
RWByteAddressBuffer Y;     // u0 — fp32 [Count]

[numthreads(256, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint i = tid.x;
    if (i >= Count) return;

    float g = Gate.Load<float>(i * 4);
    float u = Up.Load<float>(i * 4);

    float siluG = g / (1.0f + exp(-g));
    Y.Store<float>(i * 4, siluG * u);
}
