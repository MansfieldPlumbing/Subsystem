void umulExtended(uint a, uint b, out uint hi, out uint lo) {
    uint64_t r = (uint64_t)a * (uint64_t)b;
    hi = (uint)(r >> 32);
    lo = (uint)(r & 0xFFFFFFFF);
}
#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 32u
#endif
static const uint GROUP_SIZE = SPIRV_CROSS_CONSTANT_ID_0;
static const uint blocks_per_group = (GROUP_SIZE / 8u);

struct block_q8_1_packed32
{
    half2 ds;
    int qs[8];
};

#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 1u
#endif
static const uint _244 = SPIRV_CROSS_CONSTANT_ID_0;
static const uint3 gl_WorkGroupSize = uint3(_244, 1u, 1u);

ByteAddressBuffer _61 : register(t0, space0);
RWByteAddressBuffer _155 : register(u1, space0);
cbuffer SPIRV_Cross_NumWorkgroups
{
    uint3 SPIRV_Cross_NumWorkgroups_1_count : packoffset(c0);
};

cbuffer parameter
{
    uint p_ne : packoffset(c0);
    uint p_num_blocks : packoffset(c0.y);
};


static uint3 gl_WorkGroupID;
static uint3 gl_LocalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_WorkGroupID : SV_GroupID;
    uint3 gl_LocalInvocationID : SV_GroupThreadID;
};

groupshared float shmem[GROUP_SIZE];

void quantize(uint wgid)
{
    uint tid = gl_LocalInvocationID.x;
    uint block_in_wg = tid / 8u;
    uint ib = (wgid * blocks_per_group) + block_in_wg;
    uint iqs = tid % 8u;
    uint a_idx = (ib * 8u) + iqs;
    float4 _55;
    if (a_idx < (p_ne / 4u))
    {
        _55 = _61.Load<float4>(a_idx * 16 + 0);
    }
    else
    {
        _55 = 0.0f.xxxx;
    }
    float4 vals = _55;
    float4 abs_vals = abs(vals);
    float thread_max = max(max(abs_vals.x, abs_vals.y), max(abs_vals.z, abs_vals.w));
    shmem[tid] = thread_max;
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint s = 4u; s > 0u; s = s >> uint(1))
    {
        if (iqs < s)
        {
            shmem[tid] = max(shmem[tid], shmem[tid + s]);
        }
        GroupMemoryBarrierWithGroupSync();
    }
    float amax = shmem[block_in_wg * 8u];
    float d = amax / 127.0f;
    float _136;
    if (d != 0.0f)
    {
        _136 = 1.0f / d;
    }
    else
    {
        _136 = 0.0f;
    }
    float d_inv = _136;
    vals = round(vals * d_inv);
    _155.Store<int>(ib * 36 + iqs * 4 + 4, (???(round(vals))));
    GroupMemoryBarrierWithGroupSync();
    float thread_sum = ((vals.x + vals.y) + vals.z) + vals.w;
    shmem[tid] = thread_sum;
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint s_1 = 4u; s_1 > 0u; s_1 = s_1 >> uint(1))
    {
        if (iqs < s_1)
        {
            shmem[tid] += shmem[tid + s_1];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (iqs == 0u)
    {
        float sum = shmem[tid];
        _155.Store<half2>(ib * 36 + 0, half2(float2(d, sum * d)));
    }
}

void comp_main()
{
    uint wgid = gl_WorkGroupID.x;
    while (wgid < p_num_blocks)
    {
        quantize(wgid);
        wgid += SPIRV_Cross_NumWorkgroups_1_count.x;
    }
}

[numthreads(SPIRV_CROSS_CONSTANT_ID_0, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}
