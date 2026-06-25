static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

ByteAddressBuffer _49;
RWByteAddressBuffer _106;
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
    float a = float(_49.Load<half>(i * 2 + 0));
    float a_div_sqr2 = a * 0.707106769084930419921875f;
    float sign_x = sign(a_div_sqr2);
    float x = abs(a_div_sqr2);
    float t = 1.0f / (1.0f + (0.3275910913944244384765625f * x));
    float y = 1.0f - ((((((((((1.0614054203033447265625f * t) + (-1.45315206050872802734375f)) * t) + 1.42141377925872802734375f) * t) + (-0.284496724605560302734375f)) * t) + 0.254829585552215576171875f) * t) * exp((-x) * x));
    float erf_approx = sign_x * y;
    _106.Store<half>(i * 2 + 0, half((0.5f * a) * (1.0f + erf_approx)));
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
