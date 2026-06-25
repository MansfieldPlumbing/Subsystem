void umulExtended(uint a, uint b, out uint hi, out uint lo) {
    uint64_t r = (uint64_t)a * (uint64_t)b;
    hi = (uint)(r >> 32);
    lo = (uint)(r & 0xFFFFFFFF);
}
struct block_q1_0
{
    half d;
    ??? qs[16];
};

static const uint3 gl_WorkGroupSize = uint3(256u, 1u, 1u);

ByteAddressBuffer _77 : register(t0, space0);
RWByteAddressBuffer _103 : register(u1, space0);
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
    uint il = tid / 4u;
    uint ir = tid % 4u;
    uint ib = (4u * i) + ir;
    if (ib >= (p_nel / 128u))
    {
        return;
    }
    uint b_idx = ((512u * i) + (128u * ir)) + (8u * il);
    float d = float(_77.Load<half>(ib * 18 + 0));
    uint bits = uint(_77.Load<???>(ib * 18 + il * 1 + 2));
    float _113;
    [unroll]
    for (uint l = 0u; l < 8u; l++)
    {
        if ((bits & (1u << l)) != 0u)
        {
            _113 = d;
        }
        else
        {
            _113 = -d;
        }
        _103.Store<half>((b_idx + l) * 2 + 0, half(_113));
    }
}

[numthreads(256, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}
