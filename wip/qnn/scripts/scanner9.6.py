import os
import re
import glob
import numpy as np
from safetensors.numpy import save_file

print("[*] Initializing Scanner 9.6: Safetensors Extractor...")

# We embed your pasted terminal output as a fallback in case the C# tool didn't save to a file
fallback_text = """
0x00081001 | 58424      | Quantized_Weight_Tensor   | 4.9%         | _down_blocks_0_attentions_0_transformer_blocks_0_attn2_to_k_convs_0_Conv
0x0087CD54 | 1794       | Quantized_Weight_Tensor   | 0.0%         | _up_blocks_3_attentions_0_transformer_blocks_0_norm3_LayerNormalization
0x0087F05A | 1303       | Quantized_Weight_Tensor   | 0.0%         | _up_blocks_3_resnets_1_norm1_Reshape_GroupNorm
0x008CD45A | 1661       | Quantized_Weight_Tensor   | 0.0%         | _up_blocks_3_resnets_1_norm2_Reshape_GroupNorm
0x008CDE58 | 159744     | Quantized_Weight_Tensor   | 0.0%         | _down_blocks_1_attentions_0_transformer_blocks_0_attn2_to_k_convs_0_Conv
0x00940058 | 2112       | Quantized_Weight_Tensor   | 0.4%         | _down_blocks_0_resnets_0_norm1_Reshape_GroupNorm
0x00940C58 | 2112       | Quantized_Weight_Tensor   | 0.0%         | _down_blocks_0_resnets_0_norm2_Reshape_GroupNorm
0x00941858 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_0_attentions_0_norm_Reshape_GroupNorm
0x00942458 | 2112       | Quantized_Weight_Tensor   | 0.4%         | _down_blocks_0_attentions_0_transformer_blocks_0_norm1_LayerNormalization
0x00943058 | 2112       | Quantized_Weight_Tensor   | 0.0%         | _down_blocks_0_attentions_0_transformer_blocks_0_norm2_LayerNormalization
0x00943C58 | 2112       | Quantized_Weight_Tensor   | 0.4%         | _down_blocks_0_attentions_0_transformer_blocks_0_norm3_LayerNormalization
0x00944858 | 2112       | Quantized_Weight_Tensor   | 0.4%         | _down_blocks_0_resnets_1_norm1_Reshape_GroupNorm
0x00945458 | 2112       | Quantized_Weight_Tensor   | 0.0%         | _down_blocks_0_resnets_1_norm2_Reshape_GroupNorm
0x00946058 | 2112       | Quantized_Weight_Tensor   | 0.4%         | _down_blocks_0_attentions_1_norm_Reshape_GroupNorm
0x00946C58 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_0_attentions_1_transformer_blocks_0_norm1_LayerNormalization
0x00947858 | 2112       | Quantized_Weight_Tensor   | 0.6%         | _down_blocks_0_attentions_1_transformer_blocks_0_norm2_LayerNormalization
0x00948458 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_0_attentions_1_transformer_blocks_0_norm3_LayerNormalization
0x00949059 | 2111       | Quantized_Weight_Tensor   | 0.6%         | _down_blocks_1_resnets_0_norm1_Reshape_GroupNorm
0x00949C58 | 2112       | Quantized_Weight_Tensor   | 0.4%         | _down_blocks_1_resnets_0_norm2_Reshape_GroupNorm
0x0094A858 | 2112       | Quantized_Weight_Tensor   | 0.0%         | _down_blocks_1_attentions_0_norm_Reshape_GroupNorm
0x0094B458 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_1_attentions_0_transformer_blocks_0_norm1_LayerNormalization
0x0094C058 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_1_attentions_0_transformer_blocks_0_norm2_LayerNormalization
0x0094CC58 | 2112       | Quantized_Weight_Tensor   | 0.9%         | _down_blocks_1_attentions_0_transformer_blocks_0_norm3_LayerNormalization
0x0094D858 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_1_resnets_1_norm1_Reshape_GroupNorm
0x0094E458 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_1_resnets_1_norm2_Reshape_GroupNorm
0x0094F058 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_1_attentions_1_norm_Reshape_GroupNorm
0x0094FC58 | 2112       | Quantized_Weight_Tensor   | 0.2%         | _down_blocks_1_attentions_1_transformer_blocks_0_norm1_LayerNormalization
0x00950858 | 2112       | Quantized_Weight_Tensor   | 0.0%         | _down_blocks_1_attentions_1_transformer_blocks_0_norm2_LayerNormalization
0x00951458 | 2112       | Quantized_Weight_Tensor   | 0.4%         | _down_blocks_1_attentions_1_transformer_blocks_0_norm3_LayerNormalization
"""

content = ""
# Hunt for the full log file in case your C# script saved the full 700+ rows
search_paths = glob.glob("C:/bin/qnn/**/*.txt", recursive=True) + glob.glob("C:/bin/qnn/**/*.log", recursive=True) + glob.glob("C:/bin/qnn/**/*.tsv", recursive=True)
for path in search_paths:
    try:
        with open(path, 'r', encoding='utf-8', errors='ignore') as f:
            temp_content = f.read()
            if 'GROUND TRUTH GRAPH VALIDATION' in temp_content:
                content = temp_content
                print(f"[+] Loaded full mapping from: {path}")
                break
    except:
        pass

if not content:
    print("[!] Could not find saved Ground Truth file. Using the terminal clipboard fallback...")
    content = fallback_text

# Regex specifically targets pure Quantized_Weight_Tensors to avoid pulling in Mixed Scale/Sparsity garbage
pattern = re.compile(r'(0x[0-9A-Fa-f]+)\s*\|\s*(\d+)\s*\|\s*Quantized_Weight_Tensor.*?\|\s*[\d\.]+%?\s*\|\s*(\S+)')
matches = pattern.findall(content)

if not matches:
    print("[-] No valid Quantized_Weight_Tensor rows found.")
    exit(1)
    
print(f"[*] Parsed {len(matches)} exact physical weight tensor locations.")

print("[*] Slicing unet.bin at validated coordinates...")
with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
    bin_data = f.read()

tensors = {}
for offset_hex, size_str, op_name in matches:
    offset = int(offset_hex, 16)
    size = int(size_str)
    
    # Format name for safetensors/diffusers standard (e.g. down_blocks.1.resnets.0.conv1.weight)
    clean_name = op_name.strip('_').replace('_', '.')
    if not clean_name.endswith('.weight'):
        clean_name += '.weight'
        
    raw_bytes = bin_data[offset : offset + size]
    tensor_data = np.frombuffer(raw_bytes, dtype=np.int8)
    
    # Pack it linearly for now (we can reshape the safetensors on the GPU side later)
    tensors[clean_name] = tensor_data

out_path = "C:/bin/qnn/unet_out/liberated_unet.safetensors"
save_file(tensors, out_path)

size_mb = os.path.getsize(out_path) / (1024*1024)
print(f"[+] SUCCESS! Saved {len(tensors)} pure S8 tensors ({size_mb:.2f} MB) to {out_path}.")
