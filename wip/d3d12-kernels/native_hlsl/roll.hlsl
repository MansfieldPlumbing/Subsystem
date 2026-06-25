void umulExtended(uint a, uint b, out uint hi, out uint lo) { uint64_t r = (uint64_t)a * (uint64_t)b; hi = (uint)(r >> 32); lo = (uint)(r & 0xFFFFFFFF); }

struct ResType
{
    uint _m0;
    uint _m1;
};

static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

RWByteAddressBuffer _317;
ByteAddressBuffer _324;
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
    ResType _72;
    umulExtended(n, mp, _72._m1, _72._m0);
    uint lsbs = _72._m0;
    uint msbs = _72._m1;
    return (msbs + n) >> L;
}

uint wrap_idx(int i, uint ne)
{
    if (i < 0)
    {
        return uint(i) + ne;
    }
    else
    {
        if (uint(i) >= ne)
        {
            return uint(i) - ne;
        }
    }
    return uint(i);
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
    if (idx >= p_ne)
    {
        return;
    }
    uint param = idx;
    uint param_1 = p_ne1_012mp;
    uint param_2 = p_ne1_012L;
    uint i3 = fastdiv(param, param_1, param_2);
    uint i3_offset = ((i3 * p_ne12) * p_ne11) * p_ne10;
    uint param_3 = idx - i3_offset;
    uint param_4 = p_ne1_01mp;
    uint param_5 = p_ne1_01L;
    uint i2 = fastdiv(param_3, param_4, param_5);
    uint i2_offset = (i2 * p_ne11) * p_ne10;
    uint param_6 = (idx - i3_offset) - i2_offset;
    uint param_7 = p_ne1_0mp;
    uint param_8 = p_ne1_0L;
    uint i1 = fastdiv(param_6, param_7, param_8);
    uint i0 = ((idx - i3_offset) - i2_offset) - (i1 * p_ne10);
    uint p1 = asuint(p_param1);
    uint p2 = asuint(p_param2);
    int s0 = int(p1 >> uint(16)) - 32768;
    int s1 = int(p1 & 65535u) - 32768;
    int s2 = int(p2 >> uint(16)) - 32768;
    int s3 = int(p2 & 65535u) - 32768;
    int param_9 = int(i0) - s0;
    uint param_10 = p_ne10;
    uint i00 = wrap_idx(param_9, param_10);
    int param_11 = int(i1) - s1;
    uint param_12 = p_ne11;
    uint i01 = wrap_idx(param_11, param_12);
    int param_13 = int(i2) - s2;
    uint param_14 = p_ne12;
    uint i02 = wrap_idx(param_13, param_14);
    int param_15 = int(i3) - s3;
    uint param_16 = p_ne13;
    uint i03 = wrap_idx(param_15, param_16);
    uint a_idx = (((i03 * p_nb03) + (i02 * p_nb02)) + (i01 * p_nb01)) + (i00 * p_nb00);
    uint d_idx = (((i3 * p_nb13) + (i2 * p_nb12)) + (i1 * p_nb11)) + (i0 * p_nb10);
    _317.Store<half>((get_doffset() + d_idx) * 2 + 0, _324.Load<half>((get_aoffset() + a_idx) * 2 + 0));
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}

