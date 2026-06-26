import urllib.request
import json
import struct
import numpy as np
import os
from safetensors import safe_open
from tqdm import tqdm

print("[*] Initializing Vega GFX900 Pro-Quantizer...")
url = "https://huggingface.co/emilianJR/epiCRealism/resolve/main/unet/diffusion_pytorch_model.safetensors"
out_dir = "C:/bin/qnn/epicrealism_quantized_for_vega"
os.makedirs(out_dir, exist_ok=True)

final_tensors = {}

# We use safe_open to stream the tensors directly without loading the whole 1.7GB file into RAM
with safe_open(url, framework="numpy", device="cpu") as f:
    # We only care about weight tensors (Convolutions, Linear/GEMM layers)
    # Norms (gamma/beta) are usually kept in FP16/FP32
    weight_keys = [k for k in f.keys() if k.endswith('.weight')]
    
    print(f"[+] Found {len(weight_keys)} weight tensors to quantize...")

    for key in tqdm(weight_keys, desc="Quantizing UNet for GFX900"):
        fp16_tensor = f.get_tensor(key)

        # Per-Tensor Symmetric Quantization (best for dense compute like GPUs)
        abs_max = np.max(np.abs(fp16_tensor))
        scale = abs_max / 127.0
        
        if scale == 0: scale = 1.0 # Handle zeroed-out tensors
            
        s8_tensor = np.round(fp16_tensor / scale).astype(np.int8)

        # Store the clean S8 tensor and its FP32 scale
        final_tensors[key] = s8_tensor
        final_tensors[f"{key}.scale"] = np.array([scale], dtype=np.float32)

# Now, we serialize this into a NEW safetensors file, perfectly clean for Vega
out_path = os.path.join(out_dir, "epicrealism_s8_for_gfx900.safetensors")
from safetensors.numpy import save_file
save_file(final_tensors, out_path)

size_mb = os.path.getsize(out_path) / (1024*1024)
print(f"\n[+] SUCCESS! Saved full S8 UNet ({size_mb:.2f} MB) to {out_path}")
print("    This file is ready for direct memory mapping to the GFX900 GPU.")
