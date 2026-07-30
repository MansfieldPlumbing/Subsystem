// rope.hlsl — GPU Rotary Position Embedding for Q/K vectors: Y = RoPE(X, pos)
cbuffer Params { uint M; uint Heads; uint Dim; uint Pos; };
ByteAddressBuffer X;       // t0 — fp32 [M, Heads, Dim]
ByteAddressBuffer CosSin;  // t1 — fp32 [Dim] (cos in first half, sin in second half)
RWByteAddressBuffer Y;     // u0 — fp32 [M, Heads, Dim]

[numthreads(64, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint i = tid.x;
    uint halfDim = Dim / 2;
    uint totalPairs = M * Heads * halfDim;
    if (i >= totalPairs) return;

    uint m = i / (Heads * halfDim);
    uint rem = i % (Heads * halfDim);
    uint h = rem / halfDim;
    uint d = rem % halfDim;

    uint idx0 = ((m * Heads + h) * Dim + d) * 4;
    uint idx1 = ((m * Heads + h) * Dim + d + halfDim) * 4;

    float x0 = X.Load<float>(idx0);
    float x1 = X.Load<float>(idx1);

    float cosV = CosSin.Load<float>(d * 4);
    float sinV = CosSin.Load<float>((d + halfDim) * 4);

    float y0 = x0 * cosV - x1 * sinV;
    float y1 = x0 * sinV + x1 * cosV;

    Y.Store<float>(idx0, y0);
    Y.Store<float>(idx1, y1);
}
