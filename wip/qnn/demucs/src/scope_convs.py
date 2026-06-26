#!/usr/bin/env python3
"""scope_convs.py <model.onnx> — list Conv/ConvTranspose ops whose activation
(max of input/output, fp16 bytes) exceeds VTCM, to scope time-tiling surgery."""
import sys
import numpy as np
import onnx
from onnx import shape_inference

m = onnx.load(sys.argv[1])
m.graph.ClearField("value_info")
m2 = shape_inference.infer_shapes(m)
vi = {v.name: [d.dim_value for d in v.type.tensor_type.shape.dim]
      for v in list(m2.graph.value_info) + list(m2.graph.input) + list(m2.graph.output)}


def sz(name):
    s = vi.get(name, [])
    return int(np.prod([x for x in s if x > 0])) * 2 if s else 0  # fp16 bytes


big = []
for n in m2.graph.node:
    if n.op_type in ("Conv", "ConvTranspose"):
        ins = max((sz(i) for i in n.input), default=0)
        outs = max((sz(o) for o in n.output), default=0)
        w = max(ins, outs)
        if w > 6 * 1024 * 1024:
            big.append((w / 1e6, n.name, vi.get(n.output[0])))

big.sort(reverse=True)
print(f"Conv/ConvT with >6MB activation (fp16): {len(big)}")
for w, name, o in big[:40]:
    print(f"  {w:7.1f}MB  out={o}  {name}")
