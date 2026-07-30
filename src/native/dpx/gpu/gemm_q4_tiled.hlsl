// gemm_q4_tiled.hlsl — prefill-shaped (M>1) tiled q4 GEMM: Y[M,N] = A[M,K] @ dequant(B)^T, one 16x16 output
// tile per threadgroup (caller passes tileN=16, tileM=16 — same dispatch geometry as the naive kernel).
// BLOCK_K is pinned to 32 = exactly one q4 block, so scale/zp resolve once per (tile column, block) into
// groupshared and the per-thread block math stays EXACTLY the scalar oracle's: acc += s * (aq - zp*asum),
// i ascending inside the block, b ascending across blocks — the same FP accumulation order as gemm_q4.hlsl.
// Same SEQUENTIAL nibble contract (byte = k>>1, LOW nibble = even k — tests/test.dpx.q4-packing-order.ps1).
// Requires BlockSize==32; the naive gemm_q4.hlsl stays the rung below for every other shape.
cbuffer Params { uint M; uint N; uint K; uint BlockSize; uint HasZp; };
ByteAddressBuffer A;        // t0 — fp32 [M,K]
ByteAddressBuffer Bq;       // t1 — packed uint8 nibbles [N, K/32, 16]
ByteAddressBuffer Scales;   // t2 — fp32 [N, K/32]
ByteAddressBuffer Zp;       // t3 — packed uint8 nibbles [N, ceil(K/32/2)] (unused when HasZp==0)
RWByteAddressBuffer C;      // u0 — fp32 [M,N]

#define TM 16u   // output rows per tile
#define TN 16u   // output columns per tile
#define TK 32u   // k depth per pass = one q4 block

groupshared float ashare[TM][TK];   // A tile
groupshared float qshare[TK][TN];   // raw nibble values for the B tile (dequant deferred to the block boundary)
groupshared float sshare[TN];       // per-column scale for the current block
groupshared float zshare[TN];       // per-column zero point for the current block

uint LoadByte(ByteAddressBuffer buf, uint byteOff)
{
    uint word = buf.Load(byteOff & ~3u);
    return (word >> ((byteOff & 3u) * 8u)) & 0xFFu;
}

[numthreads(TN, TM, 1)]
void main(uint3 gid : SV_GroupID, uint3 gtid : SV_GroupThreadID)
{
    uint tx = gtid.x, ty = gtid.y;
    uint row = gid.y * TM + ty;     // m — OOB threads still stage/barrier (loads return 0), store is guarded
    uint col = gid.x * TN + tx;     // n
    uint nBlk = K / 32u;
    uint zpRowBytes = (nBlk + 1u) / 2u;
    uint rowOff = col * nBlk * 16u;

    float acc = 0.0f;
    for (uint b = 0u; b < nBlk; b++)
    {
        uint k0 = b * 32u;
        // A tile: 16x32 floats, 256 threads -> 2 loads each
        ashare[ty][tx]       = asfloat(A.Load((row * K + k0 + tx) * 4u));
        ashare[ty][tx + 16u] = asfloat(A.Load((row * K + k0 + tx + 16u) * 4u));
        // per-column block metadata: one thread row stages scale + zp
        if (ty == 0u)
        {
            sshare[tx] = asfloat(Scales.Load((col * nBlk + b) * 4u));
            uint zbyte = LoadByte(Zp, col * zpRowBytes + (b >> 1u));
            zshare[tx] = (HasZp != 0u) ? (float)((zbyte >> ((b & 1u) * 4u)) & 0xFu) : 8.0f;
        }
        // raw q tile: 32x16 nibbles, 2 per thread — column tx's nibbles i=ty and i=ty+16 of this block
        uint i2 = ty + 16u;
        qshare[ty][tx] = (float)((LoadByte(Bq, rowOff + b * 16u + (ty >> 1u)) >> ((ty & 1u) * 4u)) & 0xFu);
        qshare[i2][tx] = (float)((LoadByte(Bq, rowOff + b * 16u + (i2 >> 1u)) >> ((i2 & 1u) * 4u)) & 0xFu);
        GroupMemoryBarrierWithGroupSync();

        float aq = 0.0f, asum = 0.0f;
        [unroll]
        for (uint i = 0u; i < TK; i++)
        {
            float av = ashare[ty][i];
            aq += av * qshare[i][tx];
            asum += av;
        }
        acc += sshare[tx] * (aq - zshare[tx] * asum);
        GroupMemoryBarrierWithGroupSync();          // tile reads done before the next block's stage
    }

    if (row < M && col < N)
        C.Store((row * N + col) * 4u, asuint(acc));
}
