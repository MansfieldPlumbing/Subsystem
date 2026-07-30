// attention.hlsl — GPU Scaled Dot-Product Attention: Y = Softmax(Q @ K^T / sqrt(d_k)) @ V
cbuffer Params { uint M; uint Heads; uint Dim; uint SeqLen; };
ByteAddressBuffer Q;      // t0 — fp32 [M, Heads, Dim]
ByteAddressBuffer K;      // t1 — fp32 [SeqLen, Heads, Dim]
ByteAddressBuffer V;      // t2 — fp32 [SeqLen, Heads, Dim]
RWByteAddressBuffer Y;    // u0 — fp32 [M, Heads, Dim]

[numthreads(16, 16, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint m = tid.y;
    uint h = tid.x;
    if (m >= M || h >= Heads) return;

    float scale = rsqrt((float)Dim);
    uint qRowOff = (m * Heads + h) * Dim * 4;

    float maxScore = -1e30f;
    for (uint s = 0; s < SeqLen; s++)
    {
        uint kRowOff = (s * Heads + h) * Dim * 4;
        float score = 0.0f;
        for (uint d = 0; d < Dim; d++)
        {
            float qv = Q.Load<float>(qRowOff + d * 4);
            float kv = K.Load<float>(kRowOff + d * 4);
            score += qv * kv;
        }
        score *= scale;
        if (score > maxScore) maxScore = score;
    }

    float sumExp = 0.0f;
    for (uint d0 = 0; d0 < Dim; d0++)
    {
        Y.Store<float>((m * Heads + h) * Dim * 4 + d0 * 4, 0.0f);
    }

    for (uint s = 0; s < SeqLen; s++)
    {
        uint kRowOff = (s * Heads + h) * Dim * 4;
        float score = 0.0f;
        for (uint d = 0; d < Dim; d++)
        {
            float qv = Q.Load<float>(qRowOff + d * 4);
            float kv = K.Load<float>(kRowOff + d * 4);
            score += qv * kv;
        }
        float w = exp(score * scale - maxScore);
        sumExp += w;

        uint vRowOff = (s * Heads + h) * Dim * 4;
        for (uint d = 0; d < Dim; d++)
        {
            float vv = V.Load<float>(vRowOff + d * 4);
            uint yOff = (m * Heads + h) * Dim * 4 + d * 4;
            float cur = Y.Load<float>(yOff);
            Y.Store<float>(yOff, cur + w * vv);
        }
    }

    float invSum = 1.0f / max(sumExp, 1e-12f);
    for (uint d = 0; d < Dim; d++)
    {
        uint yOff = (m * Heads + h) * Dim * 4 + d * 4;
        float cur = Y.Load<float>(yOff);
        Y.Store<float>(yOff, cur * invSum);
    }
}
