#!/usr/bin/env python3
"""grammar.py <bin> — characterize the QNN context-binary SERIALIZATION grammar.
Is it flatbuffers? a struct-with-section-table? how are names/tensors encoded?
Tells us how hard a zero-Qualcomm-code emitter (Route B) would be."""
import sys, struct, re
data = open(sys.argv[1], "rb").read(); N = len(data)
print(f"=== {sys.argv[1]}  {N:,} bytes ===\n")

# 1) header: try BE u32 and LE u64; flag values that look like in-file offsets/sizes
print("-- header (first 128 bytes) --")
print("  BE u32:", [hex(x) for x in struct.unpack_from(">16I", data, 0)])
print("  LE u32:", [x for x in struct.unpack_from("<16I", data, 0)])
print("  LE u64:", [x for x in struct.unpack_from("<8Q", data, 0)])
offs = [x for x in struct.unpack_from("<16Q", data, 0) if 0 < x <= N]
print("  LE u64 values that fit in file (candidate section offsets/sizes):", offs)

# 2) flatbuffers? root offset at [0], file-identifier ascii at [4:8], vtable sanity
fid = data[4:8]
root = struct.unpack_from("<I", data, 0)[0]
print("\n-- flatbuffer probe --")
print(f"  bytes[4:8] as ascii: {fid!r}   root_off(LE u32 @0)={root} (fits={root< N})")
print("  -> flatbuffers usually shows a 4-char file-id at [4:8] and a small root offset; "
      f"{'PLAUSIBLE' if (root<N and all(32<=b<127 for b in fid)) else 'unlikely -> custom struct'}")

# 3) string table: where do the op-name strings live, and how are they delimited?
m = re.search(rb"_(quant_conv|encoder|decoder|down_block|up_block)", data)
if m:
    s0 = m.start()
    print(f"\n-- string region starts near 0x{s0:08X} ({s0/N*100:.1f}% into file) --")
    around = data[max(0,s0-8):s0+80]
    print("  context:", " ".join(f"{b:02X}" for b in around))
    # delimiter: are names null-terminated, or length-prefixed (u16/u32 before)?
    pre = data[s0-4:s0]
    print(f"  4 bytes before first name: {pre.hex()}  (LE u32={struct.unpack('<I',pre)[0]}, LE u16={struct.unpack_from('<H',pre,2)[0]})")
    seg = data[s0:s0+4000]
    nter = seg.count(b"\x00");
    names = re.findall(rb"[\x20-\x7e]{4,}", seg)
    print(f"  in first 4KB of region: {len(names)} strings, {nter} null bytes -> "
          f"{'null-terminated' if nter>len(names)*0.5 else 'length-prefixed?'}")
    # check for length-prefix: u16 right before a known name == its length?
    for nm in names[:6]:
        i = data.find(nm, s0)
        l16 = struct.unpack_from("<H", data, i-2)[0] if i>=2 else -1
        l32 = struct.unpack_from("<I", data, i-4)[0] if i>=4 else -1
        print(f"    name len={len(nm):3} u16before={l16:6} u32before={l32:10}  '{nm[:40].decode(errors='replace')}'  "
              f"{'<-LEN-PREFIX(u16)' if l16==len(nm) else '<-LEN-PREFIX(u32)' if l32==len(nm) else ''}")

# 4) section layout: metadata (structured/low-entropy) vs const-weight bulk (int8)
import math
from collections import Counter
def ent(b):
    if not b: return 0.0
    c=Counter(b); n=len(b); return -sum((v/n)*math.log2(v/n) for v in c.values())
print("\n-- coarse section map (entropy per 1/32) --")
step=N//32; row=[ent(data[i*step:(i+1)*step]) for i in range(32)]
print("  "+" ".join(f"{e:.1f}" for e in row))
# the const blob = the long run of moderate-entropy int8; metadata = the low-entropy head
head_struct = sum(1 for e in row[:8] if e<6.0)
print(f"  low-entropy (structured) chunks in first 25%: {head_struct}/8  -> metadata+graph header lives up front")

print("\n=== READ ===")
print("  If header has section offsets that fit the file + names are length-prefixed/null-term in a")
print("  contiguous table + const blob is a trailing int8 region => it's a STRUCT-WITH-SECTIONS container,")
print("  i.e. BORING. A zero-Qualcomm emitter writes: header + tensor table + node table + const blob.")
