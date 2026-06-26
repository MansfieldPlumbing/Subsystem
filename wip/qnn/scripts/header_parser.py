import struct
import pandas as pd

with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
    # ONLY read the header region to avoid weight stream false positives
    header_data = f.read(0x081000)

records = []
for i in range(len(header_data) - 20):
    if header_data[i:i+4] == b'\x01\x00\x00\xc0':
        size, ptr, flags = struct.unpack('<III', header_data[i+4:i+16])
        
        # Real pointers must point into the weight stream (after the header)
        # And let's cap size at 50MB to be safe
        if ptr >= 0x081000 and size < 50_000_000 and size > 0:
            records.append({
                "Offset": f"0x{i:08X}",
                "Size_Bytes": size,
                "Size_MB": round(size / 1024 / 1024, 2),
                "Pointer": f"0x{ptr:08X}",
                "Flags": f"0x{flags:08X}"
            })

df = pd.DataFrame(records)
print(f"[*] Found {len(df)} valid descriptors in the Header.")

if len(df) > 0:
    print("\n--- Top 20 Largest Tensors in the Header ---")
    print(df.sort_values("Size_Bytes", ascending=False).head(20).to_string(index=False))
    
    df.to_csv("C:/bin/qnn/unet_out/header_contract.tsv", sep='\t', index=False)
    print(f"\n[+] Saved to C:/bin/qnn/unet_out/header_contract.tsv")
