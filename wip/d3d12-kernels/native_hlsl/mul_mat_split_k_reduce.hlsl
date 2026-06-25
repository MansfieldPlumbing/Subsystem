void umulExtended(uint a, uint b, out uint hi, out uint lo) { uint64_t r = (uint64_t)a * (uint64_t)b; hi = (uint)(r >> 32); lo = (uint)(r & 0xFFFFFFFF); }

static const uint3 gl_WorkGroupSize = uint3(256u, 1u, 1u);

ByteAddressBuffer _67;
RWByteAddressBuffer _85;
ByteAddressBuffer _122;
RWByteAddressBuffer _141;
cbuffer parameter
{
    uint p_ne : packoffset(c0);
    uint p_k_num : packoffset(c0.y);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

void comp_main()
{
    uint idx = gl_GlobalInvocationID.x * 4u;
    if (idx >= p_ne)
    {
        return;
    }
    bool _37 = (idx + 3u) < p_ne;
    bool _44;
    if (_37)
    {
        _44 = (p_ne % 4u) == 0u;
    }
    else
    {
        _44 = _37;
    }
    if (_44)
    {
        float4 result = 0.0f.xxxx;
        [loop]
        for (uint i = 0u; i < p_k_num; i++)
        {
            result += _67.Load<float4>((((i * p_ne) + idx) / 4u) * 16 + 0);
        }
        _85.Store<float4>((idx / 4u) * 16 + 0, result);
    }
    else
    {
        [loop]
        for (uint j = 0u; j < 4u; j++)
        {
            if ((idx + j) < p_ne)
            {
                float result_1 = 0.0f;
                [loop]
                for (uint i_1 = 0u; i_1 < p_k_num; i_1++)
                {
                    result_1 += _122.Load<float>((((i_1 * p_ne) + idx) + j) * 4 + 0);
                }
                _141.Store<float>((idx + j) * 4 + 0, result_1);
            }
        }
    }
}

[numthreads(256, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}

