#pragma pack_matrix(row_major)

ByteAddressBuffer WeightsAtlas : register(t0); 
ByteAddressBuffer Activations  : register(t1);
ByteAddressBuffer ScaleTable   : register(t2); // <--- NEW: Our FP32 Scales
RWByteAddressBuffer Output     : register(u0);

[numthreads(64, 1, 1)]
void main(uint3 DTid : SV_DispatchThreadID) {
    uint out_channel = DTid.x;
    int accumulator = 0;

    for (uint k = 0; k < 320; k++) {
        uint w_packed = WeightsAtlas.Load((out_channel * 1280) + (k * 4));
        uint a_packed = Activations.Load(k * 4);

        int16_t4 w;
        w.x = (int16_t)( ((int)w_packed << 24) >> 24 );
        w.y = (int16_t)( ((int)w_packed << 16) >> 24 );
        w.z = (int16_t)( ((int)w_packed << 8)  >> 24 );
        w.w = (int16_t)( ((int)w_packed)       >> 24 );

        int16_t4 a;
        a.x = (int16_t)( ((int)a_packed << 24) >> 24 );
        a.y = (int16_t)( ((int)a_packed << 16) >> 24 );
        a.z = (int16_t)( ((int)a_packed << 8)  >> 24 );
        a.w = (int16_t)( ((int)a_packed)       >> 24 );

        accumulator += (w.x * a.x) + (w.y * a.y);
        accumulator += (w.z * a.z) + (w.w * a.w);
    }

    // Epilogue: Fetch our custom FP32 scale for this specific channel
    float scale = asfloat(ScaleTable.Load(out_channel * 4));
    float fp16_out = (float)accumulator * scale; 
    
    Output.Store(out_channel * 4, asuint(fp16_out));
}