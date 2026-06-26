#!/usr/bin/env python3
"""stft_info.py <model.onnx> — dump every STFT node's params (frame_step, window,
frame_length, onesided) + signal/output shapes + consumers, to build the Conv basis."""
import sys
import numpy as np
import onnx
from onnx import numpy_helper, shape_inference

m = onnx.load(sys.argv[1])
g = m.graph
inits = {i.name: numpy_helper.to_array(i) for i in g.initializer}
const_nodes = {}
for n in g.node:
    if n.op_type == "Constant":
        for a in n.attribute:
            if a.name == "value":
                const_nodes[n.output[0]] = numpy_helper.to_array(a.t)


def val(name):
    return inits.get(name, const_nodes.get(name))


mm = onnx.load(sys.argv[1])
mm.graph.ClearField("value_info")
mm = shape_inference.infer_shapes(mm)
vi = {v.name: [d.dim_value for d in v.type.tensor_type.shape.dim]
      for v in list(mm.graph.value_info) + list(mm.graph.input) + list(mm.graph.output)}

for n in g.node:
    if n.op_type != "STFT":
        continue
    print("STFT", n.name, "attrs", [(a.name, onnx.helper.get_attribute_value(a)) for a in n.attribute])
    nm = ["signal", "frame_step", "window", "frame_length"]
    for idx, i in enumerate(n.input):
        v = val(i)
        if v is not None:
            flat = np.asarray(v).flatten()
            show = v if flat.size <= 6 else f"[{flat[:6]} ...] len={flat.size}"
            print(f"  in[{idx}] {nm[idx] if idx < 4 else idx}: {i} = const {np.asarray(v).shape} {np.asarray(v).dtype} {show}")
        else:
            print(f"  in[{idx}] {nm[idx] if idx < 4 else idx}: {i}  shape={vi.get(i)} (computed)")
    print("  signal shape:", vi.get(n.input[0]))
    print("  STFT out shape:", vi.get(n.output[0]))
    cons = [(x.op_type, x.name) for x in g.node for i2 in x.input if i2 == n.output[0]]
    print("  consumed by:", cons)
