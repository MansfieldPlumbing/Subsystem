void umulExtended(uint a, uint b, out uint hi, out uint lo) { uint64_t r = (uint64_t)a * (uint64_t)b; hi = (uint)(r >> 32); lo = (uint)(r & 0xFFFFFFFF); }

#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 32u
#endif
static const uint BLOCK_SIZE = SPIRV_CROSS_CONSTANT_ID_0;
static const uint _162 = (BLOCK_SIZE / 2u);
static const uint _236 = (BLOCK_SIZE / 2u);
#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 1u
#endif
static const uint _425 = SPIRV_CROSS_CONSTANT_ID_0;
static const uint3 gl_WorkGroupSize = uint3(_425, 1u, 1u);

ByteAddressBuffer _136;
ByteAddressBuffer _273;
RWByteAddressBuffer _405;
cbuffer parameter
{
    uint p_D : packoffset(c0);
    uint p_ne1 : packoffset(c0.y);
    uint p_ne2 : packoffset(c0.z);
    uint p_ne3 : packoffset(c0.w);
    uint p_k_num : packoffset(c1);
    uint p_sinks : packoffset(c1.y);
};


static uint3 gl_WorkGroupID;
static uint3 gl_LocalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_WorkGroupID : SV_GroupID;
    uint3 gl_LocalInvocationID : SV_GroupThreadID;
};

groupshared float tmpsh[BLOCK_SIZE];

void comp_main()
{
    uint n = gl_WorkGroupID.x;
    uint tid = gl_LocalInvocationID.x;
    uint i2 = gl_WorkGroupID.z % p_ne2;
    uint i3 = gl_WorkGroupID.z / p_ne2;
    uint D = p_D;
    uint k_num = p_k_num;
    uint l_offset = (((((D * p_ne1) * p_ne2) * p_ne3) * k_num) + ((p_ne1 * 2u) * (0u + (p_k_num * (i2 + (p_ne2 * i3)))))) + n;
    uint m_offset = ((((((D * p_ne1) * p_ne2) * p_ne3) * k_num) + ((p_ne1 * 2u) * (0u + (p_k_num * (i2 + (p_ne2 * i3)))))) + p_ne1) + n;
    uint lm_stride = p_ne1 * 2u;
    float m_max = asfloat(0xff800000u /* -inf */);
    for (uint k = 0u; (k + tid) < k_num; k += BLOCK_SIZE)
    {
        float m = _136.Load<float>((m_offset + ((k + tid) * lm_stride)) * 4 + 0);
        m_max = max(m_max, m);
    }
    tmpsh[tid] = m_max;
    GroupMemoryBarrierWithGroupSync();
    [loop]
    for (uint s = _162; s > 0u; s = s >> uint(1))
    {
        if (tid < s)
        {
            m_max = max(m_max, tmpsh[tid + s]);
            tmpsh[tid] = m_max;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    m_max = tmpsh[0];
    GroupMemoryBarrierWithGroupSync();
    float L = 0.0f;
    for (uint k_1 = 0u; (k_1 + tid) < k_num; k_1 += BLOCK_SIZE)
    {
        float l = _136.Load<float>((l_offset + ((k_1 + tid) * lm_stride)) * 4 + 0);
        float m_1 = _136.Load<float>((m_offset + ((k_1 + tid) * lm_stride)) * 4 + 0);
        L += (exp(m_1 - m_max) * l);
    }
    tmpsh[tid] = L;
    GroupMemoryBarrierWithGroupSync();
    [loop]
    for (uint s_1 = _236; s_1 > 0u; s_1 = s_1 >> uint(1))
    {
        if (tid < s_1)
        {
            L += tmpsh[tid + s_1];
            tmpsh[tid] = L;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    L = tmpsh[0];
    float sink;
    if (p_sinks != 0u)
    {
        sink = _273.Load<float>(n * 4 + 0);
        float ms = 1.0f;
        float vs = 1.0f;
        if (sink > m_max)
        {
            ms = exp(m_max - sink);
        }
        else
        {
            vs = exp(sink - m_max);
        }
        L = (L * ms) + vs;
    }
    float _301;
    if (L == 0.0f)
    {
        _301 = 0.0f;
    }
    else
    {
        _301 = 1.0f / L;
    }
    L = _301;
    uint d = tid + (gl_WorkGroupID.y * BLOCK_SIZE);
    if (d < D)
    {
        float O = 0.0f;
        [loop]
        for (uint k_2 = 0u; k_2 < k_num; k_2++)
        {
            uint o_offset = (((D * p_ne1) * (k_2 + (p_k_num * (i2 + (p_ne2 * i3))))) + (D * n)) + d;
            float m_2 = _136.Load<float>((m_offset + (k_2 * lm_stride)) * 4 + 0);
            O += (exp(m_2 - m_max) * _136.Load<float>(o_offset * 4 + 0));
        }
        if (p_sinks != 0u)
        {
            if (sink > m_max)
            {
                float ms_1 = 1.0f;
                ms_1 = exp(m_max - sink);
                O *= ms_1;
            }
        }
        O *= L;
        float FLT_MAX = asfloat(2139095039u);
        O = clamp(O, -FLT_MAX, FLT_MAX);
        _405.Store<float>(((((((i3 * p_ne2) + i2) * p_ne1) * D) + (D * n)) + d) * 4 + 0, O);
    }
}

[numthreads(SPIRV_CROSS_CONSTANT_ID_0, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    comp_main();
}

