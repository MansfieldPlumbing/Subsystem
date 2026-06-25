void umulExtended(uint a, uint b, out uint hi, out uint lo) {
    uint64_t r = (uint64_t)a * (uint64_t)b;
    hi = (uint)(r >> 32);
    lo = (uint)(r & 0xFFFFFFFF);
}
struct block_q4_1
{
    half d;
    half m;
    ??? qs[16];
};

static const uint3 gl_WorkGroupSize = uint3(256u, 1u, 1u);

ByteAddressBuffer _77 : register(t0, space0);
RWByteAddressBuffer _104 : register(u1, space0);
cbuffer parameter
{
    uint p_M : packoffset(c0);
    uint p_K : packoffset(c0.y);
    uint p_stride_a : packoffset(c0.z);
    uint p_stride_b : packoffset(c0.w);
    uint p_nel : packoffset(c1);
};


static uint3 gl_WorkGroupID;
static uint3 gl_LocalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_WorkGroupID : SV_GroupID;
    uint3 gl_LocalInvocationID : SV_GroupThreadID;
};

void comp_main()
{
    uint i = (gl_WorkGroupID.x * 4u) + (gl_LocalInvocationID.x / 64u);
    uint tid = gl_LocalInvocationID.x % 64u;
    uint il = tid / 32u;
    uint ir = tid % 32u;
    uint ib = (32u * i) + ir;
    if (ib >= (p_nel / 32u))
    {
        return;
    }
    uint b_idx = ((1024u * i) + (32u * ir)) + (8u * il);
    float d = float(_77.Load<half>(ib * 20 + 0));
    float m = float(_77.Load<half>(ib * 20 + 2));
    uint q_idx = 8u * il;
    [unroll]
    for (uint l = 0u; l < 8u; l++)
    {
        _104.Store<half>(((b_idx + l) + 0u) * 2 + 0, half((d * float(int(uint(_77.Load<???>(ib * 20 + (q_idx + l) * 1 + 4))) & 15)) + m));
        _104.Store<half>(((b_idx + l) + 16u) * 2 + 0, half((d * float(_77.Load<???>(ib * 20 + (q_idx + l) * 1 + 4) >> 4)) + m));
    }
}

[numthreads(256, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}
