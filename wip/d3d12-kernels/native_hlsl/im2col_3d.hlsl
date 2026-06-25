#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 32u
#endif
static const uint BLOCK_SIZE = SPIRV_CROSS_CONSTANT_ID_0;
#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 1u
#endif
static const uint _341 = SPIRV_CROSS_CONSTANT_ID_0;
static const uint3 gl_WorkGroupSize = uint3(_341, 1u, 1u);

RWByteAddressBuffer _311;
ByteAddressBuffer _328;
cbuffer SPIRV_Cross_NumWorkgroups
{
    uint3 SPIRV_Cross_NumWorkgroups_1_count : packoffset(c0);
};

cbuffer parameter
{
    uint2 p_dst_addr : packoffset(c0);
    uint p_nb10 : packoffset(c0.z);
    uint p_nb11 : packoffset(c0.w);
    uint p_nb12 : packoffset(c1);
    uint p_nb13 : packoffset(c1.y);
    uint p_s0 : packoffset(c1.z);
    uint p_s1 : packoffset(c1.w);
    uint p_s2 : packoffset(c2);
    uint p_p0 : packoffset(c2.y);
    uint p_p1 : packoffset(c2.z);
    uint p_p2 : packoffset(c2.w);
    uint p_d0 : packoffset(c3);
    uint p_d1 : packoffset(c3.y);
    uint p_d2 : packoffset(c3.z);
    uint p_IW : packoffset(c3.w);
    uint p_IH : packoffset(c4);
    uint p_ID : packoffset(c4.y);
    uint p_IC : packoffset(c4.z);
    uint p_KW : packoffset(c4.w);
    uint p_OH : packoffset(c5);
    uint p_KD_KH_KW : packoffset(c5.y);
    uint p_KH_KW : packoffset(c5.z);
    uint p_IC_KD_KH_KW : packoffset(c5.w);
    uint p_N_OD_OH : packoffset(c6);
    uint p_OD_OH : packoffset(c6.y);
    uint p_OD_OH_OW_IC_KD_KH_KW : packoffset(c6.z);
    uint p_OH_OW_IC_KD_KH_KW : packoffset(c6.w);
    uint p_OW_IC_KD_KH_KW : packoffset(c7);
    uint p_misalign_offsets : packoffset(c7.y);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

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
    uint i = gl_GlobalInvocationID.x;
    uint nb10 = p_nb10;
    uint nb11 = p_nb11;
    uint nb12 = p_nb12;
    uint nb13 = p_nb13;
    uint s0 = p_s0;
    uint s1 = p_s1;
    uint s2 = p_s2;
    uint p0 = p_p0;
    uint p1 = p_p1;
    uint p2 = p_p2;
    uint d0 = p_d0;
    uint d1 = p_d1;
    uint d2 = p_d2;
    uint IW = p_IW;
    uint IH = p_IH;
    uint ID = p_ID;
    uint IC = p_IC;
    uint KW = p_KW;
    uint OH = p_OH;
    uint KD_KH_KW = p_KD_KH_KW;
    uint KH_KW = p_KH_KW;
    uint IC_KD_KH_KW = p_IC_KD_KH_KW;
    uint N_OD_OH = p_N_OD_OH;
    uint OD_OH = p_OD_OH;
    uint OD_OH_OW_IC_KD_KH_KW = p_OD_OH_OW_IC_KD_KH_KW;
    uint OH_OW_IC_KD_KH_KW = p_OH_OW_IC_KD_KH_KW;
    uint OW_IC_KD_KH_KW = p_OW_IC_KD_KH_KW;
    if (i >= IC_KD_KH_KW)
    {
        return;
    }
    uint iic = i / KD_KH_KW;
    uint ikd = (i - (iic * KD_KH_KW)) / KH_KW;
    uint ikh = ((i - (iic * KD_KH_KW)) - (ikd * KH_KW)) / KW;
    uint ikw = i % KW;
    uint iow = gl_GlobalInvocationID.y;
    for (uint iz = gl_GlobalInvocationID.z; iz < N_OD_OH; iz += SPIRV_Cross_NumWorkgroups_1_count.z)
    {
        uint in_ = iz / OD_OH;
        uint iod = (iz - (in_ * OD_OH)) / OH;
        uint ioh = iz % OH;
        uint iiw = ((iow * s0) + (ikw * d0)) - p0;
        uint iih = ((ioh * s1) + (ikh * d1)) - p1;
        uint iid = ((iod * s2) + (ikd * d2)) - p2;
        uint offset_dst = (((((((in_ * OD_OH_OW_IC_KD_KH_KW) + (iod * OH_OW_IC_KD_KH_KW)) + (ioh * OW_IC_KD_KH_KW)) + (iow * IC_KD_KH_KW)) + (iic * KD_KH_KW)) + (ikd * KH_KW)) + (ikh * KW)) + ikw;
        uint offset_src = (((((in_ * IC) + iic) * nb13) + (iid * nb12)) + (iih * nb11)) + (iiw * nb10);
        if (((iih >= IH) || (iiw >= IW)) || (iid >= ID))
        {
            _311.Store<half>((offset_dst + get_doffset()) * 2 + 0, half(0.0f));
        }
        else
        {
            _311.Store<half>((offset_dst + get_doffset()) * 2 + 0, _328.Load<half>((offset_src + get_aoffset()) * 2 + 0));
        }
    }
}

[numthreads(SPIRV_CROSS_CONSTANT_ID_0, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
