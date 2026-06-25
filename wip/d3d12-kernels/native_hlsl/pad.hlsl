static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

RWByteAddressBuffer _283;
ByteAddressBuffer _290;
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
    uint p_circular : packoffset(c4.z);
    uint p_lp0 : packoffset(c4.w);
    uint p_rp0 : packoffset(c5);
    uint p_lp1 : packoffset(c5.y);
    uint p_rp1 : packoffset(c5.z);
    uint p_lp2 : packoffset(c5.w);
    uint p_rp2 : packoffset(c6);
    uint p_lp3 : packoffset(c6.y);
    uint p_rp3 : packoffset(c6.z);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

uint wrap_around(int coord, uint size)
{
    return uint(coord + int(size)) % size;
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
    uint idx = ((gl_GlobalInvocationID.z * 262144u) + (gl_GlobalInvocationID.y * 512u)) + gl_GlobalInvocationID.x;
    if (idx >= p_ne)
    {
        return;
    }
    uint i3 = idx / ((p_ne12 * p_ne11) * p_ne10);
    uint i3_offset = ((i3 * p_ne12) * p_ne11) * p_ne10;
    uint i2 = (idx - i3_offset) / (p_ne11 * p_ne10);
    uint i2_offset = (i2 * p_ne11) * p_ne10;
    uint i1 = ((idx - i3_offset) - i2_offset) / p_ne10;
    uint i0 = ((idx - i3_offset) - i2_offset) - (i1 * p_ne10);
    uint src0_idx = ((((i3 - p_lp3) * p_nb03) + ((i2 - p_lp2) * p_nb02)) + ((i1 - p_lp1) * p_nb01)) + ((i0 - p_lp0) * p_nb00);
    uint dst_idx = (((i3 * p_nb13) + (i2 * p_nb12)) + (i1 * p_nb11)) + (i0 * p_nb10);
    if (p_circular != 0u)
    {
        int param = int(i0) - int(p_lp0);
        uint param_1 = p_ne00;
        uint ci0 = wrap_around(param, param_1);
        int param_2 = int(i1) - int(p_lp1);
        uint param_3 = p_ne01;
        uint ci1 = wrap_around(param_2, param_3);
        int param_4 = int(i2) - int(p_lp2);
        uint param_5 = p_ne02;
        uint ci2 = wrap_around(param_4, param_5);
        int param_6 = int(i3) - int(p_lp3);
        uint param_7 = p_ne03;
        uint ci3 = wrap_around(param_6, param_7);
        uint circular_src_idx = (((ci3 * p_nb03) + (ci2 * p_nb02)) + (ci1 * p_nb01)) + (ci0 * p_nb00);
        _283.Store<half>((get_doffset() + dst_idx) * 2 + 0, _290.Load<half>((get_aoffset() + circular_src_idx) * 2 + 0));
    }
    else
    {
        bool _304 = i0 >= p_lp0;
        bool _315;
        if (_304)
        {
            _315 = i0 < (p_ne10 - p_rp0);
        }
        else
        {
            _315 = _304;
        }
        bool _322;
        if (_315)
        {
            _322 = i1 >= p_lp1;
        }
        else
        {
            _322 = _315;
        }
        bool _333;
        if (_322)
        {
            _333 = i1 < (p_ne11 - p_rp1);
        }
        else
        {
            _333 = _322;
        }
        bool _340;
        if (_333)
        {
            _340 = i2 >= p_lp2;
        }
        else
        {
            _340 = _333;
        }
        bool _351;
        if (_340)
        {
            _351 = i2 < (p_ne12 - p_rp2);
        }
        else
        {
            _351 = _340;
        }
        bool _358;
        if (_351)
        {
            _358 = i3 >= p_lp3;
        }
        else
        {
            _358 = _351;
        }
        bool _370;
        if (_358)
        {
            _370 = i3 < (p_ne13 - p_rp3);
        }
        else
        {
            _370 = _358;
        }
        bool is_src0 = _370;
        float _377;
        if (is_src0)
        {
            _377 = float(_290.Load<half>((get_aoffset() + src0_idx) * 2 + 0));
        }
        else
        {
            _377 = 0.0f;
        }
        _283.Store<half>((get_doffset() + dst_idx) * 2 + 0, half(_377));
    }
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
