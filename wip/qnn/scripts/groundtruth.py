import numpy as np
import urllib.request
import json
import struct

def fetch_hf_tensor_stats():
    print("[*] Fetching EpicRealism Mid-Block weights from HuggingFace...")
    # EpicRealism UNet Safetensors URL
    url = "https://huggingface.co/emilianJR/epiCRealism/resolve/main/unet/diffusion_pytorch_model.safetensors"
    
    # We only need the header to find the byte offsets
    req = urllib.request.Request(url, headers={'Range': 'bytes=0-8'})
    with urllib.request.urlopen(req) as resp:
        header_size = struct.unpack('<Q', resp.read(8))[0]
        
    req = urllib.request.Request(url, headers={'Range': f'bytes=8-{8+header_size-1}'})
    with urllib.request.urlopen(req) as resp:
        header = json.loads(resp.read().decode('utf-8'))
        
    targets = [
        "mid_block.attentions.0.transformer_blocks.0.attn1.to_q.weight",
        "mid_block.attentions.0.transformer_blocks.0.attn1.to_k.weight",
        "mid_block.attentions.0.transformer_blocks.0.attn2.to_q.weight"
    ]
    
    print("\n--- GROUND TRUTH STATS (FP16) ---")
    for t_name in targets:
        if t_name in header:
            info = header[t_name]
            # Fetch just this tensor's bytes
            start, end = info['data_offsets']
            req = urllib.request.Request(url, headers={'Range': f'bytes={8+header_size+start}-{8+header_size+end-1}'})
            with urllib.request.urlopen(req) as resp:
                data = np.frombuffer(resp.read(), dtype=np.float16)
                print(f"{t_name}:")
                print(f"  Shape: {info['shape']}")
                print(f"  Min: {np.min(data):.4f}, Max: {np.max(data):.4f}, Mean: {np.mean(data):.6f}")

def local_s8_stats():
    print("\n--- LOCAL EXTRACTED STATS (S8) ---")
    raw = np.fromfile("C:/bin/llama-trace/gfx900_test/midblock_1280_s8.bin", dtype=np.int8)
    print(f"Extracted Tensor:")
    print(f"  Shape: [1280, 1280]")
    print(f"  Min: {np.min(raw)}, Max: {np.max(raw)}, Mean: {np.mean(raw):.6f}")
    
    # Simulated FP32 values using our Auto-Scale logic
    # Roughly: value * (max_fp16 / 127.0)
    print("  *If these distributions align with a specific layer above, we found our exact match.*")

if __name__ == "__main__":
    fetch_hf_tensor_stats()
    local_s8_stats()