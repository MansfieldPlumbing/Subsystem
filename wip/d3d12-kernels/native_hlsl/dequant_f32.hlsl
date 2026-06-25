void umulExtended(uint a, uint b, out uint hi, out uint lo) {
    uint64_t r = (uint64_t)a * (uint64_t)b;
    hi = (uint)(r >> 32);
    lo = (uint)(r & 0xFFFFFFFF);
}
static const uint3 gl_WorkGroupSize = uint3(256u, 1u, 1u);

RWByteAddressBuffer _44 : register(u1, space0);
ByteAddressBuffer _53 : register(t0, space0);
cbuffer parameter
{
    uint p_M : packoffset(c0);
    uint p_K : packoffset(c0.y);
    uint p_stride_a : packoffset(c0.z);
    uint p_stride_b : packoffset(c0.w);
    uint p_nel : packoffset(c1);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

void comp_main()
{
    uint i = gl_GlobalInvocationID.x * 16u;
    if (i >= p_nel)
    {
        return;
    }
    [unroll]
    for (uint l = 0u; l < 16u; l++)
    {
        _44.Store<half>((i + l) * 2 + 0, half(_53.Load<float>((i + l) * 4 + 0)));
    }
}

[numthreads(256, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
