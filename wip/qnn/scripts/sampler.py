import pandas as pd
import numpy as np

print("[*] Loading QNN Contract...")
df = pd.read_csv("C:/bin/qnn/unet_out/master_contract.tsv", sep='\t')
df['Flags'] = df['Flags'].astype(str)
weights_only = df[df['Flags'].str.endswith('13')]

with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
    full_data = f.read()

# Take 5 random samples
samples = weights_only.sample(5)

for i, row in samples.iterrows():
    ptr_hex = row['Pointer']
    ptr = int(ptr_hex, 16) if isinstance(ptr_hex, str) and ptr_hex.startswith('0x') else int(str(ptr_hex), 16)
    sz = int(row['Size'])

    print(f"\n--- Candidate {i} ---")
    print(f"Pointer: 0x{ptr:X}, Size: {sz} bytes ({sz / 1024 / 1024:.2f} MB)")
    
    if ptr + sz > len(full_data):
        print("  [!] Out of bounds!")
        continue
        
    raw = np.frombuffer(full_data[ptr : ptr+32], dtype=np.uint8)
    hex_dump = " ".join([f"{b:02X}" for b in raw])
    print(f"First 32 bytes: {hex_dump}")
    
    # Check distribution treating it as signed INT8
    full_raw = np.frombuffer(full_data[ptr : ptr+sz], dtype=np.int8)
    print(f"Min: {full_raw.min()}, Max: {full_raw.max()}, Mean: {full_raw.mean():.4f}")
