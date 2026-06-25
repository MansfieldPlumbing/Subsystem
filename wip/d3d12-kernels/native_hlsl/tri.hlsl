void umulExtended(uint a, uint b, out uint hi, out uint lo) { uint64_t r = (uint64_t)a * (uint64_t)b; hi = (uint)(r >> 32); lo = (uint)(r & 0xFFFFFFFF); }

struct ResType
{
    uint _m0;
    uint _m1;
};

static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

ByteAddressBuffer _401;
RWByteAddressBuffer _414;
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

uint fastdiv(uint n, uint mp, uint L)
{
    ResType _73;
    umulExtended(n, mp, _73._m1, _73._m0);
    uint lsbs = _73._m0;
    uint msbs = _73._m1;
    return (msbs + n) >> L;
}

uint get_aoffset()
{
    return p_misalign_offsets >> uint(16);
}

uint src0_idx(uint idx)
{
    uint param = idx;
    uint param_1 = p_ne0_012mp;
    uint param_2 = p_ne0_012L;
    uint i03 = fastdiv(param, param_1, param_2);
    uint i03_offset = ((i03 * p_ne02) * p_ne01) * p_ne00;
    uint param_3 = idx - i03_offset;
    uint param_4 = p_ne0_01mp;
    uint param_5 = p_ne0_01L;
    uint i02 = fastdiv(param_3, param_4, param_5);
    uint i02_offset = (i02 * p_ne01) * p_ne00;
    uint param_6 = (idx - i03_offset) - i02_offset;
    uint param_7 = p_ne0_0mp;
    uint param_8 = p_ne0_0L;
    uint i01 = fastdiv(param_6, param_7, param_8);
    uint i00 = ((idx - i03_offset) - i02_offset) - (i01 * p_ne00);
    return (((i03 * p_nb03) + (i02 * p_nb02)) + (i01 * p_nb01)) + (i00 * p_nb00);
}

uint get_doffset()
{
    return p_misalign_offsets & 65535u;
}

uint dst_idx(uint idx)
{
    uint param = idx;
    uint param_1 = p_ne1_012mp;
    uint param_2 = p_ne1_012L;
    uint i13 = fastdiv(param, param_1, param_2);
    uint i13_offset = ((i13 * p_ne12) * p_ne11) * p_ne10;
    uint param_3 = idx - i13_offset;
    uint param_4 = p_ne1_01mp;
    uint param_5 = p_ne1_01L;
    uint i12 = fastdiv(param_3, param_4, param_5);
    uint i12_offset = (i12 * p_ne11) * p_ne10;
    uint param_6 = (idx - i13_offset) - i12_offset;
    uint param_7 = p_ne1_0mp;
    uint param_8 = p_ne1_0L;
    uint i11 = fastdiv(param_6, param_7, param_8);
    uint i10 = ((idx - i13_offset) - i12_offset) - (i11 * p_ne10);
    return (((i13 * p_nb13) + (i12 * p_nb12)) + (i11 * p_nb11)) + (i10 * p_nb10);
}

void comp_main()
{
    uint idx = get_idx();
    if (idx >= p_ne)
    {
        return;
    }
    uint param = idx;
    uint param_1 = p_ne0_012mp;
    uint param_2 = p_ne0_012L;
    uint i03 = fastdiv(param, param_1, param_2);
    uint i03_offset = ((i03 * p_ne02) * p_ne01) * p_ne00;
    uint param_3 = idx - i03_offset;
    uint param_4 = p_ne0_01mp;
    uint param_5 = p_ne0_01L;
    uint i02 = fastdiv(param_3, param_4, param_5);
    uint i02_offset = (i02 * p_ne01) * p_ne00;
    uint param_6 = (idx - i03_offset) - i02_offset;
    uint param_7 = p_ne0_0mp;
    uint param_8 = p_ne0_0L;
    uint i01 = fastdiv(param_6, param_7, param_8);
    uint i00 = ((idx - i03_offset) - i02_offset) - (i01 * p_ne00);
    int param_9 = asint(p_param1);
    bool _pass = false;
    switch (param_9)
    {
        case 0:
        {
            _pass = i00 >= i01;
            break;
        }
        case 1:
        {
            _pass = i00 > i01;
            break;
        }
        case 2:
        {
            _pass = i00 <= i01;
            break;
        }
        case 3:
        {
            _pass = i00 < i01;
            break;
        }
    }
    if (_pass)
    {
        uint param_10 = idx;
        float val = float(_401.Load<half>((get_aoffset() + src0_idx(param_10)) * 2 + 0));
        uint param_11 = idx;
        _414.Store<half>((get_doffset() + dst_idx(param_11)) * 2 + 0, half(val));
    }
    else
    {
        uint param_12 = idx;
        _414.Store<half>((get_doffset() + dst_idx(param_12)) * 2 + 0, half(0.0f));
    }
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}

