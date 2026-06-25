void umulExtended(uint a, uint b, out uint hi, out uint lo) { uint64_t r = (uint64_t)a * (uint64_t)b; hi = (uint)(r >> 32); lo = (uint)(r & 0xFFFFFFFF); }

static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

ByteAddressBuffer _71;
RWByteAddressBuffer _143;
cbuffer parameter
{
    uint p_KX : packoffset(c0);
    uint p_KY : packoffset(c0.y);
    float p_param1 : packoffset(c0.z);
    float p_param2 : packoffset(c0.w);
    float p_param3 : packoffset(c1);
    float p_param4 : packoffset(c1.y);
};


static uint3 gl_WorkGroupID;
static uint3 gl_LocalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_WorkGroupID : SV_GroupID;
    uint3 gl_LocalInvocationID : SV_GroupThreadID;
};

groupshared float tmp[512];

void comp_main()
{
    uint group_size = p_KX;
    float eps = p_param1;
    uint tid = gl_LocalInvocationID.x;
    uint start = (gl_WorkGroupID.x * group_size) + tid;
    uint end = (gl_WorkGroupID.x + 1u) * group_size;
    tmp[tid] = 0.0f;
    [loop]
    for (uint col = start; col < end; col += 512u)
    {
        tmp[tid] += float(_71.Load<half>(col * 2 + 0));
    }
    GroupMemoryBarrierWithGroupSync();
    [loop]
    for (int s = 256; s > 0; s = s >> 1)
    {
        if (tid < uint(s))
        {
            tmp[tid] += tmp[tid + uint(s)];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    float mean = tmp[0] / float(group_size);
    GroupMemoryBarrierWithGroupSync();
    tmp[tid] = 0.0f;
    [loop]
    for (uint col_1 = start; col_1 < end; col_1 += 512u)
    {
        float xi = float(_71.Load<half>(col_1 * 2 + 0)) - mean;
        _143.Store<half>(col_1 * 2 + 0, half(xi));
        tmp[tid] += (xi * xi);
    }
    GroupMemoryBarrierWithGroupSync();
    [loop]
    for (int s_1 = 256; s_1 > 0; s_1 = s_1 >> 1)
    {
        if (tid < uint(s_1))
        {
            tmp[tid] += tmp[tid + uint(s_1)];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    float variance = tmp[0] / float(group_size);
    float scale = rsqrt(variance + eps);
    [loop]
    for (uint col_2 = start; col_2 < end; col_2 += 512u)
    {
        _143.Store<half>(col_2 * 2 + 0, _143.Load<half>(col_2 * 2 + 0) * half(scale));
    }
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}

