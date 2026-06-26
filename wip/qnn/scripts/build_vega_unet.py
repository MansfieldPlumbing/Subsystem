# C:\bin\qnn\build_vega_unet.py
import os
import torch
import numpy as np
from diffusers import UNet2DConditionModel
from safetensors.numpy import save_file

print("[*] Downloading/Loading SD 1.5 UNet (FP16)...")
# This will download the standard SD1.5 UNet directly
unet = UNet2DConditionModel.from_pretrained(
    "runwayml/stable-diffusion-v1-5", 
    subfolder="unet", 
    torch_dtype=torch.float16
)

print("[*] AOT Quantizing and Shaping for GFX900 Wave64...")
vega_tensors = {}

for name, param in unet.named_parameters():
    tensor = param.detach().cpu().float()
    
    # Target Math Ops: Convs and Linear layers
    if "weight" in name and tensor.dim() in [2, 4]:
        out_channels = tensor.shape[0]
        
        # 1. Symmetric Per-Channel Quantization
        # Find absolute max per output channel
        if tensor.dim() == 4: # Conv2D (C_out, C_in, H, W)
            abs_max = tensor.abs().amax(dim=(1,2,3), keepdim=True)
        else: # Linear (C_out, C_in)
            abs_max = tensor.abs().amax(dim=1, keepdim=True)
            
        abs_max = abs_max.clamp(min=1e-8)
        scale = abs_max / 127.0
        
        # Quantize to INT8
        q_tensor = torch.round(tensor / scale).clamp(-127, 127).to(torch.int8)
        
        # 2. Wave64 Padding (Ensure C_out is a multiple of 64 for perfect lane saturation)
        pad_out = (64 - (out_channels % 64)) % 64
        if pad_out > 0:
            if tensor.dim() == 4:
                q_tensor = torch.nn.functional.pad(q_tensor, (0,0, 0,0, 0,0, 0,pad_out))
                scale = torch.nn.functional.pad(scale, (0,0, 0,0, 0,0, 0,pad_out), value=1.0)
            else:
                q_tensor = torch.nn.functional.pad(q_tensor, (0,0, 0,pad_out))
                scale = torch.nn.functional.pad(scale, (0,0, 0,pad_out), value=1.0)

        # Save weights and extracted FP32 scales
        vega_tensors[name] = q_tensor.numpy()
        vega_tensors[name + ".scale"] = scale.flatten().numpy().astype(np.float32)
        
    else:
        # Norms, Biases, Embeddings stay FP16
        vega_tensors[name] = param.detach().cpu().numpy().astype(np.float16)

out_path = "C:/bin/qnn/unet_out/vega_unet.safetensors"
save_file(vega_tensors, out_path)
print(f"[+] Saved Vega-Optimized UNet to {out_path} ({os.path.getsize(out_path)/1024/1024:.2f} MB)")