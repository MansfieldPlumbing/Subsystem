import numpy as np
import pandas as pd
from safetensors.numpy import save_file
import os

def deswizzle(raw_data, shape):
    # Standard HMX/Crouton 32x32 de-swizzle
    # This works for MatMuls. Convs require a 4D reshuffle
    if len(shape) != 2: return raw_data # Skip non-matrix for now
    
    out_ch, in_ch = shape
    weights = np.zeros((out_ch, in_ch), dtype=np.int8)
    tile_size = 32
    
    idx = 0
    for oc_tile in range(out_ch // tile_size):
        for ic_tile in range(in_ch // tile_size):
            tile = raw_data[idx:idx+1024].reshape(tile_size, tile_size)
            weights[oc_tile*32:(oc_tile+1)*32, ic_tile*32:(ic_tile+1)*32] = tile
            idx += 1024
    return weights

def filet():
    # Load your harvested contract
    df = pd.read_csv("C:/bin/qnn/unet_out/master_contract.tsv", sep='\t')
    
    # Load the bin
    with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
        full_data = f.read()

    tensors = {}
    
    # Let's filter for only "Gold" S8 Weights (Flag ends in 13)
    # And we'll only take the major ones for the demo
    weights_only = df[df['Flags'].str.endswith('13')]
    
    print(f"[*] Processing {len(weights_only)} weight tensors...")
    
    for i, row in weights_only.iterrows():
        ptr = int(row['Pointer'], 16)
        sz = int(row['Size'])
        
        # Pull raw bytes
        raw = np.frombuffer(full_data[ptr : ptr+sz], dtype=np.int8)
        
        # Heuristic: If size is 1.6MB, it's a 1280x1280
        if sz == 1638400:
            clean = deswizzle(raw, (1280, 1280))
            tensors[f"weight_{i}"] = clean
            
    # Save as Safetensors
    save_file(tensors, "C:/bin/qnn/unet_out/liberated_model.safetensors")
    print("[+] Successfully exported to liberated_model.safetensors")

if __name__ == "__main__":
    filet()