// Native HLSL implementation of GGML Q4_0 Dequantization
// 32 FP16 elements are compressed into 16 bytes of data + 2 bytes for the FP16 scale (d)

ByteAddressBuffer input_blocks : register(t0);
RWStructuredBuffer<min16float> output_tensor : register(u0);

[numthreads(32, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID) {
    // Each thread processes one Q4_0 block (32 elements)
    uint block_idx = tid.x;
    
    // Q4_0 block size is 18 bytes (2 byte scale + 16 bytes data)
    uint block_offset = block_idx * 18;
    
    // Read the FP16 scale 'd' (first 2 bytes)
    uint d_raw = input_blocks.Load(block_offset);
    min16float d = asfloat16(uint16_t(d_raw & 0xFFFF));
    
    uint out_offset = block_idx * 32;
    
    // Unpack the 16 bytes of data (which hold 32 4-bit nibbles)
    for (uint i = 0; i < 4; i++) {
        // Read 4 bytes at a time
        uint packed_data = input_blocks.Load(block_offset + 2 + (i * 4));
        
        for (uint j = 0; j < 4; j++) {
            // Extract the 8-bit byte
            uint byte_val = (packed_data >> (j * 8)) & 0xFF;
            
            // Lower nibble (first weight)
            int v0 = (int)(byte_val & 0x0F) - 8;
            // Upper nibble (second weight)
            int v1 = (int)(byte_val >> 4) - 8;
            
            // Calculate final indices (GGML layout puts second nibble 16 elements later)
            uint out_idx_0 = out_offset + (i * 4) + j;
            uint out_idx_1 = out_offset + (i * 4) + j + 16;
            
            output_tensor[out_idx_0] = (min16float)(v0) * d;
            output_tensor[out_idx_1] = (min16float)(v1) * d;
        }
    }
}
