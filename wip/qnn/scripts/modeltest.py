import numpy as np
import pandas as pd
from safetensors.numpy import save_file
import os

def strip_mine():
    print("[*] Loading QNN Contract...")
    df = pd.read_csv("C:/bin/qnn/unet_out/master_contract.tsv", sep='\t')
    
    # Load the black-box binary
    with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
        full_data = f.read()

    tensors = {}
    seen_pointers = set()
    
    # Filter for S8 Matrix Weights (Flags ending in 13)
    # Convert to string first just in case pandas read them as ints
    df['Flags'] = df['Flags'].astype(str)
    weights_only = df[df['Flags'].str.endswith('13')]
    
    print(f"[*] Analyzing {len(weights_only)} candidate views...")
    
    for i, row in weights_only.iterrows():
        ptr_hex = row['Pointer']
        # Handle formats like 0x... or plain hex
        ptr = int(ptr_hex, 16) if isinstance(ptr_hex, str) and ptr_hex.startswith('0x') else int(str(ptr_hex), 16)
        sz = int(row['Size'])
        
        # Deduplicate: The compiler stores ~3 views (VTCM, Main, Shard) per tensor
        # We only want the physical data once.
        if ptr in seen_pointers:
            continue
            
        # Reality bounds (1KB to 50MB max per tensor)
        if sz < 1024 or sz > 50_000_000:
            continue

        # Extract the raw S8 data
        raw = np.frombuffer(full_data[ptr : ptr+sz], dtype=np.int8)
        
        # Tag it by its physical address in the binary
        tensors[f"tensor_{ptr_hex}"] = raw
        seen_pointers.add(ptr)
            
    print(f"[+] Found {len(seen_pointers)} UNIQUE S8 weight tensors in the Gold Heap.")
    
    out_path = "C:/bin/qnn/unet_out/liberated_qnn_weights.safetensors"
    if tensors:
        # --- INJECTED BY POWERSHELL ---
        total_gb = sum(v.nbytes for v in tensors.values()) / (1024**3)
        print(f"[*] Total Tensor Volume in RAM: {total_gb:.2f} GB")
    
        # Filter out obvious garbage pointers (any single S8 layer > 50MB is likely corrupt)
        clean_tensors = {k: v for k, v in tensors.items() if v.nbytes < 50_000_000}
        print(f"[*] Tensors remaining after size filter: {len(clean_tensors)}")
    
        # Save to Safetensors in chunks to avoid MemoryError
        import gc
        keys = list(clean_tensors.keys())
        chunk_size = 1000
    
        for i in range(0, len(keys), chunk_size):
            chunk = {k: clean_tensors[k] for k in keys[i:i+chunk_size]}
            chunk_path = out_path.replace('.safetensors', f'_part{i//chunk_size}.safetensors')
            print(f"[*] Saving chunk {chunk_path}...")
            save_file(chunk, chunk_path)
        
            # Free memory before the next chunk
            del chunk
            gc.collect()
            size_mb = os.path.getsize(out_path) // (1024*1024)
            print(f"[+] Strip-mine complete. Saved {size_mb}MB to {out_path}")
        else:
            print("[-] No tensors matched criteria. Check TSV formatting.")

    if __name__ == "__main__":
        strip_mine()

