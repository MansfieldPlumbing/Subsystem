static const uint3 gl_WorkGroupSize = uint3(1u, 512u, 1u);

RWByteAddressBuffer _58;
ByteAddressBuffer _71;
cbuffer parameter
{
    uint p_ncols : packoffset(c0);
    uint p_rows_per_channel : packoffset(c0.y);
    uint p_n_past : packoffset(c0.z);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

void comp_main()
{
    uint col = gl_GlobalInvocationID.y;
    uint row = gl_GlobalInvocationID.x;
    if (col >= p_ncols)
    {
        return;
    }
    uint i = (row * p_ncols) + col;
    if (col > (p_n_past + (row % p_rows_per_channel)))
    {
        _58.Store<half>(i * 2 + 0, half(asfloat(4286578688u)));
    }
    else
    {
        _58.Store<half>(i * 2 + 0, _71.Load<half>(i * 2 + 0));
    }
}

[numthreads(1, 512, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
