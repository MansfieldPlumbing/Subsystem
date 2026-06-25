void umulExtended(uint a, uint b, out uint hi, out uint lo) { uint64_t r = (uint64_t)a * (uint64_t)b; hi = (uint)(r >> 32); lo = (uint)(r & 0xFFFFFFFF); }

static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

ByteAddressBuffer _66;
RWByteAddressBuffer _165;
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

groupshared float2 sum[512];

void comp_main()
{
    uint row = ((gl_WorkGroupID.z * 262144u) + (gl_WorkGroupID.y * 512u)) + gl_WorkGroupID.x;
    uint tid = gl_LocalInvocationID.x;
    sum[tid] = 0.0f.xx;
    [loop]
    for (uint col = tid; col < p_KX; col += 512u)
    {
        float xi = float(_66.Load<half>(((row * p_KX) + col) * 2 + 0));
        sum[tid].x += xi;
        sum[tid].y += (xi * xi);
    }
    GroupMemoryBarrierWithGroupSync();
    [loop]
    for (int s = 256; s > 0; s = s >> 1)
    {
        if (tid < uint(s))
        {
            sum[tid] += sum[tid + uint(s)];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    float mean = sum[0].x / float(p_KX);
    float var = (sum[0].y / float(p_KX)) - (mean * mean);
    float inv_std = rsqrt(var + p_param1);
    [loop]
    for (uint col_1 = tid; col_1 < p_KX; col_1 += 512u)
    {
        _165.Store<half>(((row * p_KX) + col_1) * 2 + 0, half((float(_66.Load<half>(((row * p_KX) + col_1) * 2 + 0)) - mean) * inv_std));
    }
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}

