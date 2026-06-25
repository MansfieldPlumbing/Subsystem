#ifndef SPIRV_CROSS_CONSTANT_ID_0
#define SPIRV_CROSS_CONSTANT_ID_0 0u
#endif
static const uint scale_mode = SPIRV_CROSS_CONSTANT_ID_0;
static const uint3 gl_WorkGroupSize = uint3(512u, 1u, 1u);

ByteAddressBuffer _105;
RWByteAddressBuffer _1248;
cbuffer parameter
{
    uint p_ne : packoffset(c0);
    uint p_a_offset : packoffset(c0.y);
    uint p_d_offset : packoffset(c0.z);
    uint p_ne00 : packoffset(c0.w);
    uint p_ne01 : packoffset(c1);
    uint p_nb00 : packoffset(c1.y);
    uint p_nb01 : packoffset(c1.z);
    uint p_nb02 : packoffset(c1.w);
    uint p_nb03 : packoffset(c2);
    uint p_ne10 : packoffset(c2.y);
    uint p_ne11 : packoffset(c2.z);
    uint p_ne12 : packoffset(c2.w);
    uint p_ne13 : packoffset(c3);
    float p_sf0 : packoffset(c3.y);
    float p_sf1 : packoffset(c3.z);
    float p_sf2 : packoffset(c3.w);
    float p_sf3 : packoffset(c4);
    float p_pixel_offset : packoffset(c4.y);
};


static uint3 gl_GlobalInvocationID;
struct SPIRV_Cross_Input
{
    uint3 gl_GlobalInvocationID : SV_DispatchThreadID;
};

float fetch_nearest(uint i10, uint i11, uint i12, uint i13)
{
    uint i00 = uint(float(i10) / p_sf0);
    uint i01 = uint(float(i11) / p_sf1);
    uint i02 = uint(float(i12) / p_sf2);
    uint i03 = uint(float(i13) / p_sf3);
    return float(_105.Load<half>(((((p_a_offset + (i03 * p_nb03)) + (i02 * p_nb02)) + (i01 * p_nb01)) + (i00 * p_nb00)) * 2 + 0));
}

float fetch_bilinear(int2 c0, int2 c1, float2 d, uint i12, uint i13)
{
    uint i02 = uint(float(i12) / p_sf2);
    uint i03 = uint(float(i13) / p_sf3);
    uint base = (p_a_offset + (i03 * p_nb03)) + (i02 * p_nb02);
    float v00 = float(_105.Load<half>(((base + (uint(c0.y) * p_nb01)) + (uint(c0.x) * p_nb00)) * 2 + 0));
    float v01 = float(_105.Load<half>(((base + (uint(c0.y) * p_nb01)) + (uint(c1.x) * p_nb00)) * 2 + 0));
    float v10 = float(_105.Load<half>(((base + (uint(c1.y) * p_nb01)) + (uint(c0.x) * p_nb00)) * 2 + 0));
    float v11 = float(_105.Load<half>(((base + (uint(c1.y) * p_nb01)) + (uint(c1.x) * p_nb00)) * 2 + 0));
    return ((((v00 * (1.0f - d.x)) * (1.0f - d.y)) + ((v01 * d.x) * (1.0f - d.y))) + ((v10 * (1.0f - d.x)) * d.y)) + ((v11 * d.x) * d.y);
}

float interpolate_bilinear(uint i10, uint i11, uint i12, uint i13)
{
    int2 ne0 = int2(int(p_ne00), int(p_ne01));
    float2 c = ((float2(float(i10), float(i11)) + p_pixel_offset.xx) / float2(p_sf0, p_sf1)) - p_pixel_offset.xx;
    float2 c0f = floor(c);
    float2 d = c - c0f;
    int2 c0 = max(int2(c0f), int2(0, 0));
    int2 c1 = min(int2(c0f + 1.0f.xx), (ne0 - int2(1, 1)));
    int2 param = c0;
    int2 param_1 = c1;
    float2 param_2 = d;
    uint param_3 = i12;
    uint param_4 = i13;
    return fetch_bilinear(param, param_1, param_2, param_3, param_4);
}

float4 powers(float x)
{
    return float4((x * x) * x, x * x, x, 1.0f);
}

float bicubic(float p0, float p1, float p2, float p3, float x)
{
    float param = x + 1.0f;
    float param_1 = x;
    float param_2 = 1.0f - x;
    float param_3 = 2.0f - x;
    return (((p0 * dot(float4(-0.75f, 3.75f, -6.0f, 3.0f), powers(param))) + (p1 * dot(float4(1.25f, -2.25f, 0.0f, 1.0f), powers(param_1)))) + (p2 * dot(float4(1.25f, -2.25f, 0.0f, 1.0f), powers(param_2)))) + (p3 * dot(float4(-0.75f, 3.75f, -6.0f, 3.0f), powers(param_3)));
}

float interpolate_bicubic(uint i10, uint i11, uint i12, uint i13)
{
    int2 res = int2(int(p_ne00 - 1u), int(p_ne01 - 1u));
    float2 coord = ((float2(float(i10), float(i11)) + p_pixel_offset.xx) / float2(p_sf0, p_sf1)) - p_pixel_offset.xx;
    float2 d = frac(coord);
    int2 i = int2(floor(coord));
    uint i02 = uint(float(i12) / p_sf2);
    uint i03 = uint(float(i13) / p_sf3);
    uint base = (p_a_offset + (i03 * p_nb03)) + (i02 * p_nb02);
    float param = float(_105.Load<half>(((base + (uint(clamp(i.x + (-1), 0, res.x)) * p_nb00)) + (uint(clamp(i.y + (-1), 0, res.y)) * p_nb01)) * 2 + 0));
    float param_1 = float(_105.Load<half>(((base + (uint(clamp(i.x + 0, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + (-1), 0, res.y)) * p_nb01)) * 2 + 0));
    float param_2 = float(_105.Load<half>(((base + (uint(clamp(i.x + 1, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + (-1), 0, res.y)) * p_nb01)) * 2 + 0));
    float param_3 = float(_105.Load<half>(((base + (uint(clamp(i.x + 2, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + (-1), 0, res.y)) * p_nb01)) * 2 + 0));
    float param_4 = d.x;
    float param_5 = float(_105.Load<half>(((base + (uint(clamp(i.x + (-1), 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 0, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_6 = float(_105.Load<half>(((base + (uint(clamp(i.x + 0, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 0, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_7 = float(_105.Load<half>(((base + (uint(clamp(i.x + 1, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 0, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_8 = float(_105.Load<half>(((base + (uint(clamp(i.x + 2, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 0, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_9 = d.x;
    float param_10 = float(_105.Load<half>(((base + (uint(clamp(i.x + (-1), 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 1, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_11 = float(_105.Load<half>(((base + (uint(clamp(i.x + 0, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 1, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_12 = float(_105.Load<half>(((base + (uint(clamp(i.x + 1, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 1, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_13 = float(_105.Load<half>(((base + (uint(clamp(i.x + 2, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 1, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_14 = d.x;
    float param_15 = float(_105.Load<half>(((base + (uint(clamp(i.x + (-1), 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 2, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_16 = float(_105.Load<half>(((base + (uint(clamp(i.x + 0, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 2, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_17 = float(_105.Load<half>(((base + (uint(clamp(i.x + 1, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 2, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_18 = float(_105.Load<half>(((base + (uint(clamp(i.x + 2, 0, res.x)) * p_nb00)) + (uint(clamp(i.y + 2, 0, res.y)) * p_nb01)) * 2 + 0));
    float param_19 = d.x;
    float param_20 = bicubic(param, param_1, param_2, param_3, param_4);
    float param_21 = bicubic(param_5, param_6, param_7, param_8, param_9);
    float param_22 = bicubic(param_10, param_11, param_12, param_13, param_14);
    float param_23 = bicubic(param_15, param_16, param_17, param_18, param_19);
    float param_24 = d.y;
    return bicubic(param_20, param_21, param_22, param_23, param_24);
}

float triangle_filter(float x)
{
    return max(1.0f - abs(x), 0.0f);
}

float interpolate_bilinear_antialias(uint i10, uint i11, uint i12, uint i13)
{
    float support1 = max(1.0f, 1.0f / p_sf1);
    float invscale1 = 1.0f / support1;
    float support0 = max(1.0f, 1.0f / p_sf0);
    float invscale0 = 1.0f / support0;
    uint i02 = uint(float(i12) / p_sf2);
    uint i03 = uint(float(i13) / p_sf3);
    float y = (float(i11) + p_pixel_offset) / p_sf1;
    float x = (float(i10) + p_pixel_offset) / p_sf0;
    int x_min = max(int((x - support0) + p_pixel_offset), 0);
    int x_max = min(int((x + support0) + p_pixel_offset), int(p_ne00));
    int y_min = max(int((y - support1) + p_pixel_offset), 0);
    int y_max = min(int((y + support1) + p_pixel_offset), int(p_ne01));
    float val = 0.0f;
    float total_weight = 0.0f;
    for (int sy = y_min; sy < y_max; sy++)
    {
        float param = ((float(sy) - y) + p_pixel_offset) * invscale1;
        float weight_y = triangle_filter(param);
        for (int sx = x_min; sx < x_max; sx++)
        {
            float param_1 = ((float(sx) - x) + p_pixel_offset) * invscale0;
            float weight_x = triangle_filter(param_1);
            float weight = weight_x * weight_y;
            if (weight <= 0.0f)
            {
                continue;
            }
            float pixel = float(_105.Load<half>(((((p_a_offset + (i03 * p_nb03)) + (i02 * p_nb02)) + (uint(sy) * p_nb01)) + (uint(sx) * p_nb00)) * 2 + 0));
            val += (pixel * weight);
            total_weight += weight;
        }
    }
    if (total_weight > 0.0f)
    {
        val /= total_weight;
    }
    return val;
}

void comp_main()
{
    uint idx = ((gl_GlobalInvocationID.z * 262144u) + (gl_GlobalInvocationID.y * 512u)) + gl_GlobalInvocationID.x;
    if (idx >= p_ne)
    {
        return;
    }
    uint i10 = idx % p_ne10;
    uint i11 = (idx / p_ne10) % p_ne11;
    uint i12 = (idx / (p_ne10 * p_ne11)) % p_ne12;
    uint i13 = (idx / ((p_ne10 * p_ne11) * p_ne12)) % p_ne13;
    float result;
    switch (scale_mode)
    {
        case 0u:
        {
            uint param = i10;
            uint param_1 = i11;
            uint param_2 = i12;
            uint param_3 = i13;
            result = fetch_nearest(param, param_1, param_2, param_3);
            break;
        }
        case 1u:
        {
            uint param_4 = i10;
            uint param_5 = i11;
            uint param_6 = i12;
            uint param_7 = i13;
            result = interpolate_bilinear(param_4, param_5, param_6, param_7);
            break;
        }
        case 2u:
        {
            uint param_8 = i10;
            uint param_9 = i11;
            uint param_10 = i12;
            uint param_11 = i13;
            result = interpolate_bicubic(param_8, param_9, param_10, param_11);
            break;
        }
        case 513u:
        {
            uint param_12 = i10;
            uint param_13 = i11;
            uint param_14 = i12;
            uint param_15 = i13;
            result = interpolate_bilinear_antialias(param_12, param_13, param_14, param_15);
            break;
        }
    }
    _1248.Store<half>((p_d_offset + idx) * 2 + 0, half(result));
}

[numthreads(512, 1, 1)]
void main(SPIRV_Cross_Input stage_input)
{
    gl_GlobalInvocationID = stage_input.gl_GlobalInvocationID;
    comp_main();
}
