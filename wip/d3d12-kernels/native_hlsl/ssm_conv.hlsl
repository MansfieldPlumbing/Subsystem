void umulExtended(uint a, uint b, out uint hi, out uint lo) { uint64_t r = (uint64_t)a * (uint64_t)b; hi = (uint)(r >> 32); lo = (uint)(r & 0xFFFFFFFF); }

#ifndef SPIRV_CROSS_CONSTANT_ID_1
#define SPIRV_CROSS_CONSTANT_ID_1 16u
#endif
static const uint TOKENS_PER_WG = SPIRV_CROSS_CONSTANT_ID_1;
#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 32u
#endif
static const uint BLOCK_SIZE = SPIRV_CROSS_CONSTANT_ID_0;
#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 1u
#endif
static const uint _191 = SPIRV_CROSS_CONSTANT_ID_0;
#ifndef SPIRV_CROSS_CONSTANT_ID_1
#define SPIRV_CROSS_CONSTANT_ID_1 1u
#endif
static const uint _192 = SPIRV_CROSS_CONSTANT_ID_1;
static const uint3 gl_WorkGroupSize = uint3(_191, _192, 1u);

ByteAddressBuffer _100;
ByteAddressBuffer _123;
RWByteAddressBuffer _186;
cbuffer PushConstants
{
    uint _35_nb01 : packoffset(c0);
    uint _35_nb02 : packoffset(c0.y);
    uint _35_nb11 : packoffset(c0.z);
    uint _35_dst_nb0 : packoffset(c0.w);
    uint _35_dst_nb1 : packoffset(c1);
    uint _35_dst_nb2 : packoffset(c1.y);
    uint _35_nc : packoffset(c1.z);
    uint _35_ncs : packoffset(c1.w);
    uint _35_nr : packoffset(c2);
    uint _35_n_t : packoffset(c2.y);
    uint _35_n_s : packoffset(c2.z);
};


static uint3 gl_WorkGroupID;
static uint3 gl_LocalInvocationID;
static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_WorkGroupID : SV_GroupID;
    uint3 gl_LocalInvocationID : SV_GroupThreadID;
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

void comp_main()
{
    uint i1 = gl_GlobalInvocationID.x;
    uint i2 = (gl_WorkGroupID.y * TOKENS_PER_WG) + gl_LocalInvocationID.y;
    uint i3 = gl_WorkGroupID.z;
    bool _41 = i1 >= _35_nr;
    bool _50;
    if (!_41)
    {
        _50 = i2 >= _35_n_t;
    }
    else
    {
        _50 = _41;
    }
    bool _59;
    if (!_50)
    {
        _59 = i3 >= _35_n_s;
    }
    else
    {
        _59 = _50;
    }
    if (_59)
    {
        return;
    }
    uint src0_base = ((i3 * (_35_nb02 / 4u)) + i2) + (i1 * (_35_nb01 / 4u));
    uint src1_base = i1 * (_35_nb11 / 4u);
    float sum = 0.0f;
    if (_35_nc == 4u)
    {
        sum = dot(float4(_100.Load<float>(src0_base * 4 + 0), _100.Load<float>((src0_base + 1u) * 4 + 0), _100.Load<float>((src0_base + 2u) * 4 + 0), _100.Load<float>((src0_base + 3u) * 4 + 0)), float4(_123.Load<float>(src1_base * 4 + 0), _123.Load<float>((src1_base + 1u) * 4 + 0), _123.Load<float>((src1_base + 2u) * 4 + 0), _123.Load<float>((src1_base + 3u) * 4 + 0)));
    }
    else
    {
        [loop]
        for (uint i0 = 0u; i0 < _35_nc; i0++)
        {
            sum += (_100.Load<float>((src0_base + i0) * 4 + 0) * _123.Load<float>((src1_base + i0) * 4 + 0));
        }
    }
    uint dst_idx = ((i3 * (_35_dst_nb2 / 4u)) + (i2 * (_35_dst_nb1 / 4u))) + i1;
    _186.Store<float>(dst_idx * 4 + 0, sum);
}

[numthreads(SPIRV_CROSS_CONSTANT_ID_0, SPIRV_CROSS_CONSTANT_ID_1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_WorkGroupID = stage_input.gl_WorkGroupID;
    gl_LocalInvocationID = stage_input.gl_LocalInvocationID;
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}

