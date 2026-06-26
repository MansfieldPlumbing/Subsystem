import urllib.request
import json
import struct
import numpy as np
import os

print("[*] Bypassing Qualcomm binary. Fetching EpicRealism directly from HF...")
url = "https://huggingface.co/emilianJR/epiCRealism/resolve/main/unet/diffusion_pytorch_model.safetensors"

# 1. Read the EXACT 8-byte length of the JSON header first
req = urllib.request.Request(url, headers={'Range': 'bytes=0-7'})
with urllib.request.urlopen(req) as resp:
    header_len = struct.unpack('<Q', resp.read())[0]

# 2. Read the full JSON header safely
req = urllib.request.Request(url, headers={'Range': f'bytes=8-{8+header_len-1}'})
with urllib.request.urlopen(req) as resp:
    header = json.loads(resp.read().decode('utf-8'))

# We will grab the 1280x1280 Mid-Block Attention "To_Q" weights
target_layer = "mid_block.attentions.0.transformer_blocks.0.attn1.to_q.weight"
start, end = header[target_layer]["data_offsets"]
shape = header[target_layer]["shape"]

print(f"[+] Found '{target_layer}': Shape {shape}, Size: {(end-start)//1024//1024} MB")

# 3. Fetch the raw FP16 PyTorch Data
req = urllib.request.Request(url, headers={'Range': f'bytes={8+header_len+start}-{8+header_len+end-1}'})
with urllib.request.urlopen(req) as resp:
    fp16_data = np.frombuffer(resp.read(), dtype=np.float16).reshape(shape)

print("[*] Quantizing to S8 (Per-Channel Symmetric) for Vega GFX900...")
out_channels = shape[0]

s8_tensor = np.zeros_like(fp16_data, dtype=np.int8)
scales = np.zeros(out_channels, dtype=np.float32)

# Quantize channel by channel, saving the scale factors
for oc in range(out_channels):
    channel_data = fp16_data[oc, :]
    abs_max = np.max(np.abs(channel_data))
    
    # Avoid divide by zero
    scale = abs_max / 127.0 if abs_max > 0 else 1.0
    
    s8_tensor[oc, :] = np.round(channel_data / scale).astype(np.int8)
    scales[oc] = scale

# 4. Save to disk cleanly for the C++ runner
out_dir = "C:/bin/llama-trace/gfx900_test"
os.makedirs(out_dir, exist_ok=True)

s8_tensor.tofile(f"{out_dir}/vega_clean_midblock_s8.bin")
scales.tofile(f"{out_dir}/vega_clean_scales_fp32.bin")

print(f"[+] SUCCESS! Saved clean S8 tensor and Scales to {out_dir}")
print(f"    S8 Array: {s8_tensor.shape}")
print(f"    Scales:   {scales.shape}")
