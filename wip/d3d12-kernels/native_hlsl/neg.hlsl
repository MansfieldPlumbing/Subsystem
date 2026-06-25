static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

RWByteAddressBuffer _47;
ByteAddressBuffer _52;
cbuffer parameter
{
    uint p_KX : packoffset(c0);
    uint p_KY : packoffset(c0.y);
    float p_param1 : packoffset(c0.z);
    float p_param2 : packoffset(c0.w);
    float p_param3 : packoffset(c1);
    float p_param4 : packoffset(c1.y);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

void comp_main()
{
    uint i = ((gl_GlobalInvocationID.z * 262144u) + (gl_GlobalInvocationID.y * 512u)) + gl_GlobalInvocationID.x;
    if (i >= p_KX)
    {
        return;
    }
    _47.Store<half>(i * 2 + 0, half(-float(_52.Load<half>(i * 2 + 0))));
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
