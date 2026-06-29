#!/usr/bin/env python3
"""
dissect.py <qnn_context.bin> — interrogate a QNN HTP context binary WITHOUT the SDK.

Goal: decide the CRUX — is the inner blob a serialized, named graph (home-rollable)
or opaque precompiled DSP code (not)? Evidence we extract:
  1. header / magic + size
  2. embedded string table: op/tensor NAMES (a named graph => home-rollable) + QNN/HTP markers
  3. 0xC0000001 tensor-descriptor records (size/ptr/flags) — the qnn-dissect signal
  4. per-region Shannon entropy (structured/low vs compiled-or-random/high)
  5. byte-value histogram of the bulk (int8 weights look ~gaussian; code looks flat-ish)
"""
import sys, struct, math, re
from collections import Counter

path = sys.argv[1]
data = open(path, "rb").read()
N = len(data)
print(f"=== {path}  ({N:,} bytes / {N/1048576:.2f} MB) ===\n")

# 1) header
print("-- first 64 bytes --")
print(" ".join(f"{b:02X}" for b in data[:64]))
print(" ascii:", "".join(chr(b) if 32 <= b < 127 else "." for b in data[:64]), "\n")

# 2) embedded strings — the tell. QNN serializes tensor/op names as C strings.
strings = re.findall(rb"[\x20-\x7e]{4,}", data)
print(f"-- strings: {len(strings)} printable runs >=4 chars --")
# markers that prove which world we're in
markers = [b"QNN", b"Qnn", b"HTP", b"htp", b"Htp", b"v73", b"v68", b"v75", b"vtcm", b"VTCM",
           b"hexagon", b"Hexagon", b"HVX", b"HMX", b"spill", b"SpillFill", b"context",
           b"graph", b"Graph", b"Conv", b"MatMul", b"Softmax", b"LayerNorm", b"ElementWise",
           b"Relu", b"Sigmoid", b"Reshape", b"Transpose", b"attention", b"resnet", b"down_block",
           b"up_block", b"vae", b"encoder", b"decoder", b"weight", b"bias", b"QNN_SYSTEM",
           b"BINARY_INFO", b"dlc", b"DLC", b"fp16", b"qtensor", b"Tensor", b"input", b"output"]
hits = {m.decode(): data.count(m) for m in markers if data.count(m) > 0}
print("  markers:", ", ".join(f"{k}={v}" for k, v in sorted(hits.items(), key=lambda kv: -kv[1])))
# longest / most graph-name-like strings
named = [s for s in strings if len(s) >= 10 and (b"_" in s or b"/" in s or b"." in s)]
print(f"  graph-name-like strings (>=10ch w/ _ / .): {len(named)}")
for s in named[:25]:
    print("    ", s.decode(errors="replace")[:110])

# 3) 0xC0000001 descriptor records (sig, size, ptr, flags) — qnn-dissect
print("\n-- 0xC0000001 descriptor records --")
recs = []
i = 0
while i <= N - 16:
    if struct.unpack_from("<I", data, i)[0] == 0xC0000001:
        sz, ptr, flags = struct.unpack_from("<III", data, i + 4)
        recs.append((i, sz, ptr, flags))
        i += 16
    else:
        i += 4
print(f"  found {len(recs)} records")
for (off, sz, ptr, flags) in recs[:12]:
    tgt = ""
    if 0 < ptr < N:
        tgt = " ".join(f"{b:02X}" for b in data[ptr:ptr+12])
    print(f"    @0x{off:08X}  size={sz:>10,}  ptr=0x{ptr:08X}  flags=0x{flags:08X}  [{tgt}]")
if recs:
    szs = sorted(r[1] for r in recs if 0 < r[1] < N)
    print(f"  descriptor size range: {min(szs):,} .. {max(szs):,}  (total {sum(szs):,} = {sum(szs)/1048576:.1f} MB)")

# 4) per-region Shannon entropy (16 chunks) — high (~8.0) = compiled/random/compressed; low = structured
print("\n-- entropy by region (bits/byte; ~8.0=opaque, <6=structured) --")
def ent(b):
    if not b: return 0.0
    c = Counter(b); n = len(b)
    return -sum((v/n) * math.log2(v/n) for v in c.values())
chunks = 16; step = N // chunks
row = []
for c in range(chunks):
    seg = data[c*step : (c+1)*step if c < chunks-1 else N]
    row.append(ent(seg))
print("  " + "  ".join(f"{e:.2f}" for e in row))
print(f"  whole-file entropy: {ent(data):.3f} bits/byte")

# 5) bulk byte histogram shape — int8 weights cluster near 0; flat = code/compressed
h = Counter(data)
top = h.most_common(8)
print("\n-- byte histogram (top values) --")
print("  " + ", ".join(f"0x{b:02X}:{c*100/N:.1f}%" for b, c in top))
zero_frac = h.get(0, 0) / N
print(f"  zero bytes: {zero_frac*100:.1f}%   distinct byte values: {len(h)}/256")

print("\n=== VERDICT HINTS ===")
print(f"  named-graph evidence: {len(named)} name-like strings, marker hits on op/graph terms.")
print(f"  -> if op/tensor NAMES are present in plaintext, the graph is SERIALIZED (home-rollable side).")
print(f"  -> watch for a high-entropy (~8.0) region distinct from the weight bulk = candidate compiled-DSP/HTP blob.")
