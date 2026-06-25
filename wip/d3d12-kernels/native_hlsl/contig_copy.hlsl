static const uint3 gl_WorkGroupSize = uint3(128u, 1u, 1u);

RWByteAddressBuffer _80;
ByteAddressBuffer _87;
cbuffer parameter
{
    uint p_ne : packoffset(c0);
    uint p_ne00 : packoffset(c0.y);
    uint p_ne01 : packoffset(c0.z);
    uint p_ne02 : packoffset(c0.w);
    uint p_ne03 : packoffset(c1);
    uint p_nb00 : packoffset(c1.y);
    uint p_nb01 : packoffset(c1.z);
    uint p_nb02 : packoffset(c1.w);
    uint p_nb03 : packoffset(c2);
    uint p_ne10 : packoffset(c2.y);
    uint p_ne11 : packoffset(c2.z);
    uint p_ne12 : packoffset(c2.w);
    uint p_ne13 : packoffset(c3);
    uint p_nb10 : packoffset(c3.y);
    uint p_nb11 : packoffset(c3.z);
    uint p_nb12 : packoffset(c3.w);
    uint p_nb13 : packoffset(c4);
    uint p_misalign_offsets : packoffset(c4.y);
    float p_param1 : packoffset(c4.z);
    float p_param2 : packoffset(c4.w);
    uint p_ne0_012mp : packoffset(c5);
    uint p_ne0_012L : packoffset(c5.y);
    uint p_ne0_01mp : packoffset(c5.z);
    uint p_ne0_01L : packoffset(c5.w);
    uint p_ne0_0mp : packoffset(c6);
    uint p_ne0_0L : packoffset(c6.y);
    uint p_ne1_012mp : packoffset(c6.z);
    uint p_ne1_012L : packoffset(c6.w);
    uint p_ne1_01mp : packoffset(c7);
    uint p_ne1_01L : packoffset(c7.y);
    uint p_ne1_0mp : packoffset(c7.z);
    uint p_ne1_0L : packoffset(c7.w);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

uint get_idx()
{
    return ((gl_GlobalInvocationID.z * 262144u) + (gl_GlobalInvocationID.y * 512u)) + gl_GlobalInvocationID.x;
}

uint get_doffset()
{
    return p_misalign_offsets & 65535u;
}

uint get_aoffset()
{
    return p_misalign_offsets >> uint(16);
}

void comp_main()
{
    uint idx = get_idx();
    if ((idx + 384u) < p_ne)
    {
        [unroll]
        for (uint i = 0u; i < 4u; i++)
        {
            _80.Store<half>((get_doffset() + idx) * 2 + 0, _87.Load<half>((get_aoffset() + idx) * 2 + 0));
            idx += 128u;
        }
    }
    else
    {
        [unroll]
        for (uint i_1 = 0u; i_1 < 4u; i_1++)
        {
            if (idx >= p_ne)
            {
                continue;
            }
            _80.Store<half>((get_doffset() + idx) * 2 + 0, _87.Load<half>((get_aoffset() + idx) * 2 + 0));
            idx += 128u;
        }
    }
}

[numthreads(128, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
