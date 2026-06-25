void umulExtended(uint a, uint b, out uint hi, out uint lo) {
    uint64_t r = (uint64_t)a * (uint64_t)b;
    hi = (uint)(r >> 32);
    lo = (uint)(r & 0xFFFFFFFF);
}
struct block_q8_0
{
    half d;
    ??? qs[32];
};

static const uint3 gl_WorkGroupSize = uint3(256u, 1u, 1u);

ByteAddressBuffer _76 : register(t0, space0);
RWByteAddressBuffer _97 : register(u1, space0);
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
    uint b_idx = ((1024u * i) + (32u * ir)) + (16u * il);
    float d = float(_76.Load<half>(ib * 34 + 0));
    uint q_idx = 16u * il;
    [unroll]
    for (uint l = 0u; l < 16u; l += 2u)
    {
        _97.Store<half>((b_idx + l) * 2 + 0, half(d * float(_76.Load<???>(ib * 34 + (q_idx + l) * 1 + 2))));
        _97.Store<half>(((b_idx + l) + 1u) * 2 + 0, half(d * float(_76.Load<???>(ib * 34 + ((q_idx + l) + 1u) * 1 + 2))));
    }
}

[numthreads(256, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}
