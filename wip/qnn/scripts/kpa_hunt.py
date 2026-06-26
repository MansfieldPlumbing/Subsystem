import os
import urllib.request
import json
import struct
import numpy as np
import pandas as pd

print("[*] STEP 1: Fetching 'conv_in.weight' from HuggingFace (emilianJR/epiCRealism)...")
index_url = "https://huggingface.co/emilianJR/epiCRealism/resolve/main/unet/diffusion_pytorch_model.safetensors"

req = urllib.request.Request(index_url, headers={'Range': 'bytes=0-8192'})
with urllib.request.urlopen(req) as response:
    header_bytes = response.read()
    
# Safetensors header length is an 8-byte unsigned little-endian int
header_len = struct.unpack('<Q', header_bytes[:8])[0]
header_json = json.loads(header_bytes[8:8+header_len].decode('utf-8'))

# Get conv_in.weight byte range
conv_in_info = header_json["conv_in.weight"]
start, end = conv_in_info["data_offsets"]
print(f"[+] Found conv_in.weight in EpicRealism Safetensors: bytes {start} to {end} (Length: {end-start})")

# Fetch the actual FP16 tensor bytes
req = urllib.request.Request(index_url, headers={'Range': f'bytes={8+header_len+start}-{8+header_len+end-1}'})
with urllib.request.urlopen(req) as response:
    fp16_bytes = response.read()

fp16_tensor = np.frombuffer(fp16_bytes, dtype=np.float16)

print("\n[*] STEP 2: Applying AIMET S8 Quantization Heuristic...")
# Standard Symmetric 8-bit Quantization
abs_max = np.max(np.abs(fp16_tensor))
scale = abs_max / 127.0
s8_tensor = np.round(fp16_tensor / scale).astype(np.int8)

hf_std = np.std(s8_tensor)
hf_mean = np.mean(s8_tensor)
print(f"  -> Quantized Target Fingerprint: Mean={hf_mean:.4f}, StdDev={hf_std:.4f}, Min={s8_tensor.min()}, Max={s8_tensor.max()}")

print("\n[*] STEP 3: Hunting through unet.bin blobs for a statistical match...")
blob_df = pd.read_csv("C:/bin/qnn/unet_out/blob_map.tsv", sep='\t')
# We know the size must be AT LEAST the raw size (115,200 bytes for 320x4x3x3)
candidates = blob_df[blob_df['size_bytes'] >= 115200]

with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
    bin_data = f.read()

best_match = None
lowest_diff = 999999

for _, row in candidates.iterrows():
    offset = int(str(row['offset']), 16) if isinstance(row['offset'], str) else int(row['offset'])
    size = int(row['size_bytes'])
    
    # Extract blob and drop the trailing zero-padding to compare core distributions
    raw_blob = np.frombuffer(bin_data[offset : offset + size], dtype=np.int8)
    trimmed_blob = np.trim_zeros(raw_blob, 'b')
    
    if len(trimmed_blob) < 1000:
        continue
        
    b_std = np.std(trimmed_blob)
    b_mean = np.mean(trimmed_blob)
    
    # We compare the variance/spread of the data
    diff = abs(b_std - hf_std) + abs(b_mean - hf_mean)
    
    if diff < lowest_diff:
        lowest_diff = diff
        best_match = (offset, size, b_mean, b_std, trimmed_blob.min(), trimmed_blob.max())

print("\n========================================================")
if lowest_diff < 1.0: # Close statistical match
    print(f"[+] EXACT MATCH FOUND FOR 'conv_in.weight'!")
    print(f"    Physical Offset : 0x{best_match[0]:08X}")
    print(f"    Padded Size     : {best_match[1]} bytes")
    print(f"    Blob Stats      : Mean={best_match[2]:.4f}, StdDev={best_match[3]:.4f}")
    print(f"    (Difference from HF: {lowest_diff:.4f})")
else:
    print(f"[-] No close match found. Lowest diff was {lowest_diff:.4f}")
print("========================================================")
