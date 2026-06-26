import numpy as np

def deswizzle_hmx(src_path, out_path, out_ch=1280, in_ch=1280):
    raw = np.fromfile(src_path, dtype=np.int8)
    weights = np.zeros((out_ch, in_ch), dtype=np.int8)
    
    TILE_SIZE = 32
    idx = 0
    
    for oc_tile in range(out_ch // TILE_SIZE):
        for ic_tile in range(in_ch // TILE_SIZE):
            
            # Grab the 1024 byte chunk
            raw_chunk = raw[idx : idx + 1024]
            
            # HVX Vector Unpacking
            # 8 groups of (32 Output Channels x 4 Input Channels)
            vector_blocks = raw_chunk.reshape(8, 32, 4)
            
            # Transpose to (OC, IC_outer, IC_inner) and flatten the IC dimension
            tile = vector_blocks.transpose(1, 0, 2).reshape(32, 32)
            
            start_oc = oc_tile * TILE_SIZE
            start_ic = ic_tile * TILE_SIZE
            weights[start_oc : start_oc + TILE_SIZE, start_ic : start_ic + TILE_SIZE] = tile
            
            idx += 1024
            
    weights.tofile(out_path)
    print(f"[+] De-swizzled {src_path} -> {out_path} (with HVX Vector unpacking)")

if __name__ == "__main__":
    deswizzle_hmx(
        "C:/bin/llama-trace/gfx900_test/midblock_1280_s8.bin",
        "C:/bin/llama-trace/gfx900_test/midblock_1280_LINEAR.bin"
    )