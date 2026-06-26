with open("C:/bin/qnn/test-harness-s23-v73/unet.bin", "rb") as f:
    header = f.read(0x081000)

# We are searching for ID 0x1303EEB5 (Little Endian: B5 EE 03 13)
target_id = b'\xb5\xee\x03\x13'
hits = []

for i in range(len(header) - 4):
    if header[i:i+4] == target_id:
        hits.append(i)

print("\n--- HUNTING FOR TENSOR ID 0x1303EEB5 IN HEADER ---")
print(f"[*] Found {len(hits)} occurrences of Tensor ID.")

for h in hits:
    print(f"\n[+] Hit at header offset: 0x{h:08X}")
    # Print a window around the hit to see if a physical offset (e.g., 0x001A...) is sitting next to it
    start = max(0, h - 16)
    end = min(len(header), h + 32)
    
    dump = " ".join([f"{b:02X}" for b in header[start:end]])
    print(f"Context: {dump}")
