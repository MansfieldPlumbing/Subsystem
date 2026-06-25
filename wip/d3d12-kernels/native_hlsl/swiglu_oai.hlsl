static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

RWByteAddressBuffer _206;
ByteAddressBuffer _211;
ByteAddressBuffer _267;
cbuffer parameter
{
    uint p_N : packoffset(c0);
    uint p_ne00 : packoffset(c0.y);
    uint p_ne20 : packoffset(c0.z);
    uint p_mode : packoffset(c0.w);
    float p_alpha : packoffset(c1);
    float p_limit : packoffset(c1.y);
    uint p_nb01 : packoffset(c1.z);
    uint p_nb02 : packoffset(c1.w);
    uint p_nb03 : packoffset(c2);
    uint p_ne01 : packoffset(c2.y);
    uint p_ne02 : packoffset(c2.z);
    uint p_nb11 : packoffset(c2.w);
    uint p_nb12 : packoffset(c3);
    uint p_nb13 : packoffset(c3.y);
    uint p_ne11 : packoffset(c3.z);
    uint p_ne12 : packoffset(c3.w);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

float op(float a, float b)
{
    float xi = min(a, p_limit);
    float gi = max(min(b, p_limit), -p_limit);
    float out_glu = xi / (1.0f + exp((-xi) * p_alpha));
    out_glu *= (1.0f + gi);
    return out_glu;
}

void comp_main()
{
    uint i = ((gl_GlobalInvocationID.z * 262144u) + (gl_GlobalInvocationID.y * 512u)) + gl_GlobalInvocationID.x;
    if (i >= p_N)
    {
        return;
    }
    uint row = i / p_ne20;
    uint col = i - (row * p_ne20);
    uint i3 = row / (p_ne01 * p_ne02);
    uint i2 = (row % (p_ne01 * p_ne02)) / p_ne01;
    uint i1 = row % p_ne01;
    uint src_idx = (((i3 * p_nb03) + (i2 * p_nb02)) + (i1 * p_nb01)) + col;
    uint dst_i3 = row / (p_ne11 * p_ne12);
    uint dst_i2 = (row % (p_ne11 * p_ne12)) / p_ne11;
    uint dst_i1 = row % p_ne11;
    uint dst_idx = (((dst_i3 * p_nb13) + (dst_i2 * p_nb12)) + (dst_i1 * p_nb11)) + col;
    if (p_mode == 0u)
    {
        uint offset = p_ne00 / 2u;
        uint idx = src_idx;
        float param = float(_211.Load<half>(idx * 2 + 0));
        float param_1 = float(_211.Load<half>((idx + offset) * 2 + 0));
        _206.Store<half>(dst_idx * 2 + 0, half(op(param, param_1)));
    }
    else
    {
        if (p_mode == 1u)
        {
            uint offset_1 = p_ne00 / 2u;
            uint idx_1 = src_idx;
            float param_2 = float(_211.Load<half>((idx_1 + offset_1) * 2 + 0));
            float param_3 = float(_211.Load<half>(idx_1 * 2 + 0));
            _206.Store<half>(dst_idx * 2 + 0, half(op(param_2, param_3)));
        }
        else
        {
            uint idx_2 = src_idx;
            float param_4 = float(_211.Load<half>(idx_2 * 2 + 0));
            float param_5 = float(_267.Load<half>(idx_2 * 2 + 0));
            _206.Store<half>(dst_idx * 2 + 0, half(op(param_4, param_5)));
        }
    }
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
