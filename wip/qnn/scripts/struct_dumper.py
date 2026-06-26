import struct

with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
    f.seek(0x00080E5C)
    desc1 = f.read(52)
    f.seek(0x00080E28)
    desc2 = f.read(52)

def print_desc(name, data):
    ints = struct.unpack('<13I', data)
    print(f"\n{name}")
    for i, val in enumerate(ints):
        label = ""
        if i == 0: label = " <- Sentinel"
        elif i == 1: label = " <- Size"
        elif i == 2: label = " <- Virtual Addr / ID"
        elif i == 3: label = " <- Flags"
        
        # Highlight values that look like valid physical offsets into the unet.bin file
        if 0x00081000 <= val <= 0x34700000:
            label += "  <=== POSSIBLE PHYSICAL OFFSET?"
            
        print(f"Word {i:02d} (offset +{i*4:02d}): {val:10d} | 0x{val:08X}{label}")

print_desc("--- Descriptor 1 (8.62 MB Tensor) ---", desc1)
print_desc("--- Descriptor 2 (8.56 MB Tensor) ---", desc2)
