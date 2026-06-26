ByteAddressBuffer   WeightsBuf  : register(t0);
ByteAddressBuffer   ActsInBuf   : register(t1);
ByteAddressBuffer   ScalesBuf   : register(t2);
RWByteAddressBuffer ActsOutBuf  : register(u0);

struct OpConstants {
    uint op_type;       // 0 = MatMul, 1 = Conv2D 3x3, etc.
    uint weight_offset; 
    uint scale_offset;
    uint in_offset;
    uint out_offset;
    uint K_dim;         // Inner reduction dimension (Bytes per row)
    uint out_channels;
};
ConstantBuffer<OpConstants> cb : register(b0);

[numthreads(64, 1, 1)]
void main(uint3 DTid : SV_DispatchThreadID) {
    uint oc = DTid.x; // Output channel (Wave64 aligned)
    
    if (oc >= cb.out_channels) return;

    int32_t acc = 0;

    if (cb.op_type == 0) {
        // MATMUL PATH
        for (uint k = 0; k < cb.K_dim; k += 4) {
            uint w_dword = WeightsBuf.Load(cb.weight_offset + (oc * cb.K_dim) + k);
            uint a_dword = ActsInBuf.Load(cb.in_offset + k); 

            // Sign-extend 8-bit packed values by shifting to top of 32-bit int and arithmetic shifting down
            int16_t w0 = (int16_t)( asint(w_dword << 24) >> 24 );
            int16_t w1 = (int16_t)( asint(w_dword << 16) >> 24 );
            int16_t w2 = (int16_t)( asint(w_dword << 8)  >> 24 );
            int16_t w3 = (int16_t)( asint(w_dword)       >> 24 );
            int16_t2 w_pair0 = int16_t2(w0, w1);
            int16_t2 w_pair1 = int16_t2(w2, w3);

            int16_t a0 = (int16_t)( asint(a_dword << 24) >> 24 );
            int16_t a1 = (int16_t)( asint(a_dword << 16) >> 24 );
            int16_t a2 = (int16_t)( asint(a_dword << 8)  >> 24 );
            int16_t a3 = (int16_t)( asint(a_dword)       >> 24 );
            int16_t2 a_pair0 = int16_t2(a0, a1);
            int16_t2 a_pair1 = int16_t2(a2, a3);

            // Hardware Packed Math (DXC maps this to V_PK_MAD_I16)
            int32_t2 temp0 = mad(w_pair0, a_pair0, int32_t2(0,0));
            int32_t2 temp1 = mad(w_pair1, a_pair1, int32_t2(0,0));
            
            acc += temp0.x + temp0.y + temp1.x + temp1.y;
        }
    }

    // EPILOGUE: Fetch channel scale, apply, store as FP32
    float scale = asfloat(ScalesBuf.Load(cb.scale_offset + (oc * 4)));
    float fp32_out = (float)acc * scale;
    ActsOutBuf.Store(cb.out_offset + (oc * 4), asuint(fp32_out));
}
