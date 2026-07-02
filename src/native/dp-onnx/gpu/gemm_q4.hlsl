// gemm_q4.hlsl — q4-aware GPU GEMM for MatMulNBits: Y[M,N] = A[M,K] @ dequant(B)^T, one thread per (m,n).
// B is packed uint8 [N, K/bs, bs*4/8] SEQUENTIAL nibbles (byte = k>>1, low nibble = even k — pinned by
// tests/test.dpx.q4-packing-order.ps1; this is NOT ggml's split-nibble layout). scale is per (n, block b=k/bs),
// zp is an optional packed-nibble buffer (same low-nibble-first order), defaulting to 8 (2^(bits-1)) when absent.
// Never materializes the dequantized fp32 weight — dequant+multiply+accumulate happens per k inside the loop.
cbuffer Params { uint M; uint N; uint K; uint BlockSize; uint HasZp; };
ByteAddressBuffer A;        // t0 — fp32 [M,K]
ByteAddressBuffer Bq;       // t1 — packed uint8 nibbles [N, K/bs, bs/2]
ByteAddressBuffer Scales;   // t2 — fp32 [N, K/bs]
ByteAddressBuffer Zp;       // t3 — packed uint8 nibbles [N, ceil(K/bs/2)] (unused when HasZp==0)
RWByteAddressBuffer C;      // u0 — fp32 [M,N]

uint LoadByte(ByteAddressBuffer buf, uint byteOff)
{
    uint word = buf.Load(byteOff & ~3u);
    uint shift = (byteOff & 3u) * 8u;
    return (word >> shift) & 0xFFu;
}

uint Nib4(ByteAddressBuffer buf, uint rowByteOff, uint idx)
{
    uint b = LoadByte(buf, rowByteOff + (idx >> 1));
    return (b >> ((idx & 1u) * 4u)) & 0xFu;
}

[numthreads(16, 16, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint m = tid.y, n = tid.x;
    if (m >= M || n >= N) return;

    uint nBlk = K / BlockSize;
    uint rowBytes = nBlk * (BlockSize / 2);
    uint zpRowBytes = (nBlk + 1) / 2;
    uint rowOff = n * rowBytes;
    uint zRowOff = n * zpRowBytes;
    uint aRowOff = m * K * 4;

    float acc = 0.0f;
    for (uint b = 0; b < nBlk; b++)
    {
        float s = Scales.Load<float>((n * nBlk + b) * 4);
        uint zp = HasZp != 0 ? Nib4(Zp, zRowOff, b) : 8u;
        uint k0 = b * BlockSize;
        float aq = 0.0f, asum = 0.0f;
        for (uint i = 0; i < BlockSize; i++)
        {
            uint k = k0 + i;
            uint q = Nib4(Bq, rowOff, k);
            float av = A.Load<float>(aRowOff + k * 4);
            aq += av * (float)q;
            asum += av;
        }
        acc += s * (aq - (float)zp * asum);
    }
    C.Store<float>((m * N + n) * 4, acc);
}
