// gemm_q4_gemv.hlsl — decode-shaped (M==1) q4 GEMV: y[N] = a[K] @ dequant(B)^T. One threadgroup covers 8
// output rows with 16 lanes reducing each row, so the dispatch divisor is 8 outputs per group in N (the caller
// passes tileN=8, tileM=1). Same SEQUENTIAL nibble contract as gemm_q4.hlsl (byte = k>>1, LOW nibble = even k —
// pinned by tests/test.dpx.q4-packing-order.ps1) and the same block math as the scalar oracle:
// acc += s * (aq - zp*asum) with i ascending inside each 32-wide block. Requires BlockSize==32 (one Load4 = one
// block's 16 weight bytes) and M==1; the naive gemm_q4.hlsl stays the rung below for every other shape.
// A is staged into groupshared in 2048-float chunks so all 8 rows read it from TGSM instead of 8x from DRAM.
cbuffer Params { uint M; uint N; uint K; uint BlockSize; uint HasZp; };
ByteAddressBuffer A;        // t0 — fp32 [1,K]
ByteAddressBuffer Bq;       // t1 — packed uint8 nibbles [N, K/32, 16]
ByteAddressBuffer Scales;   // t2 — fp32 [N, K/32]
ByteAddressBuffer Zp;       // t3 — packed uint8 nibbles [N, ceil(K/32/2)] (unused when HasZp==0)
RWByteAddressBuffer C;      // u0 — fp32 [1,N]

#define LANES 16u    // threads cooperating on one output row
#define ROWS   8u    // output rows per group (= the caller's tileN)
#define CHUNK 2048u  // A floats staged per pass (8KB of TGSM)

groupshared float ashare[CHUNK];
groupshared float rshare[ROWS][LANES];

uint LoadByte(ByteAddressBuffer buf, uint byteOff)
{
    uint word = buf.Load(byteOff & ~3u);
    return (word >> ((byteOff & 3u) * 8u)) & 0xFFu;
}

[numthreads(LANES, ROWS, 1)]
void main(uint3 gid : SV_GroupID, uint3 gtid : SV_GroupThreadID)
{
    uint lane = gtid.x, rowIx = gtid.y;
    uint n = gid.x * ROWS + rowIx;          // rows past N still stage/barrier; loads are OOB-zero, store is guarded
    uint nBlk = K / 32u;
    uint rowOff = n * nBlk * 16u;
    uint zRowOff = n * ((nBlk + 1u) / 2u);
    uint flat = rowIx * LANES + lane;

    float acc = 0.0f;
    for (uint ko = 0u; ko < K; ko += CHUNK)
    {
        uint len = min(CHUNK, K - ko);
        for (uint j = flat; j < len; j += LANES * ROWS)
            ashare[j] = asfloat(A.Load((ko + j) * 4u));
        GroupMemoryBarrierWithGroupSync();

        uint blk0 = ko / 32u, blks = len / 32u;
        for (uint bl = lane; bl < blks; bl += LANES)
        {
            uint b = blk0 + bl;
            float s = asfloat(Scales.Load((n * nBlk + b) * 4u));
            uint zbyte = LoadByte(Zp, zRowOff + (b >> 1u));
            float zp = (HasZp != 0u) ? (float)((zbyte >> ((b & 1u) * 4u)) & 0xFu) : 8.0f;
            uint4 w = Bq.Load4(rowOff + b * 16u);   // whole 32-nibble block, sequential order
            float aq = 0.0f, asum = 0.0f;
            uint kbase = bl * 32u;
            [unroll]
            for (uint j4 = 0u; j4 < 4u; j4++)
            {
                uint wj = w[j4];
                [unroll]
                for (uint t = 0u; t < 8u; t++)      // nibble t of the word = logical k (kbase + j4*8 + t)
                {
                    float av = ashare[kbase + j4 * 8u + t];
                    aq += av * (float)((wj >> (t * 4u)) & 0xFu);
                    asum += av;
                }
            }
            acc += s * (aq - zp * asum);
        }
        GroupMemoryBarrierWithGroupSync();          // ashare is rewritten by the next chunk pass
    }

    rshare[rowIx][lane] = acc;
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u && n < N)
    {
        float total = 0.0f;
        [unroll]
        for (uint l = 0u; l < LANES; l++) total += rshare[rowIx][l];
        C.Store(n * 4u, asuint(total));
    }
}
