// rmsnorm.hlsl — GPU RMSNorm for SimplifiedLayerNormalization: Y[m, d] = (X[m, d] / sqrt(mean(X[m, :]^2) + eps)) * Gamma[d]
cbuffer Params { uint M; uint D; float Epsilon; uint Padding; };
ByteAddressBuffer X;       // t0 — fp32 [M, D]
ByteAddressBuffer Gamma;   // t1 — fp32 [D]
RWByteAddressBuffer Y;     // u0 — fp32 [M, D]

[numthreads(64, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint m = tid.x;
    if (m >= M) return;

    uint rowOff = m * D * 4;
    float sumSq = 0.0f;
    for (uint d = 0; d < D; d++)
    {
        float x = X.Load<float>(rowOff + d * 4);
        sumSq += x * x;
    }
    float rmsRecip = rsqrt((sumSq / (float)D) + Epsilon);

    for (uint d = 0; d < D; d++)
    {
        float x = X.Load<float>(rowOff + d * 4);
        float g = Gamma.Load<float>(d * 4);
        Y.Store<float>(rowOff + d * 4, x * rmsRecip * g);
    }
}
