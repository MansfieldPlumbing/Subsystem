static const uint3 gl_WorkGroupSize = uint3(256u, 1u, 1u);

ByteAddressBuffer _59;
RWByteAddressBuffer _130;
cbuffer parameter
{
    uint p_ne00 : packoffset(c0);
    uint p_ne01 : packoffset(c0.y);
    uint p_nb00 : packoffset(c0.z);
    uint p_nb01 : packoffset(c0.w);
    uint p_a_offset : packoffset(c1);
};


static uint3 gl_WorkGroupID;
static uint3 gl_LocalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_WorkGroupID : SV_GroupID;
    uint3 gl_LocalInvocationID : SV_GroupThreadID;
};

groupshared uint vals[256];

void comp_main()
{
    uint expert_id = gl_WorkGroupID.x;
    uint num_elements = p_ne00 * p_ne01;
    uint tid = gl_LocalInvocationID.x;
    uint count = 0u;
    for (uint idx = tid; idx < num_elements; idx += 256u)
    {
        uint i01 = idx / p_ne00;
        uint i00 = idx % p_ne00;
        uint a = _59.Load<uint>(((p_a_offset + (i01 * p_nb01)) + (i00 * p_nb00)) * 4 + 0);
        count += uint(a == expert_id);
    }
    vals[tid] = count;
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint s = 128u; s > 0u; s = s >> uint(1))
    {
        if (tid < s)
        {
            vals[tid] += vals[tid + s];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (tid == 0u)
    {
        _130.Store<uint>(expert_id * 4 + 0, vals[0]);
    }
}

[numthreads(256, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}
