#!/usr/bin/env python3
"""
fold_static.py <in.onnx> <out.onnx> <tensorA,tensorB,...>

Bake shape-derived "dynamic" tensors that are actually CONSTANT at static input
shape into Constant initializers. qairt-converter rejects Range/NonZero/etc whose
inputs it can't see as constant; if the value doesn't depend on input *values*
(only shapes), ORT-evaluating it once and freezing it is mathematically exact.

We feed zeros at the (already static) input shapes, evaluate the target tensors,
remove their producer node, and add an initializer of the same name. Verify
numeric equivalence afterwards (the op must be correct at the end).
"""
import sys
import numpy as np
import onnx
from onnx import helper, numpy_helper
import onnxruntime as ort

inp, out, names = sys.argv[1], sys.argv[2], [t for t in sys.argv[3].split(",") if t]

tmp = onnx.load(inp)
have = {o.name for o in tmp.graph.output}
for t in names:
    if t not in have:
        tmp.graph.output.append(helper.make_empty_tensor_value_info(t))

so = ort.SessionOptions()
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
sess = ort.InferenceSession(tmp.SerializeToString(), so, providers=["CPUExecutionProvider"])
_dt = {"tensor(int64)": np.int64, "tensor(int32)": np.int32,
       "tensor(float)": np.float32, "tensor(float16)": np.float16, "tensor(bool)": bool}
feeds = {}
for i in sess.get_inputs():
    shp = [d if isinstance(d, int) and d > 0 else 1 for d in i.shape]
    dt = _dt.get(i.type, np.float32)
    # valid (non-degenerate) input: zeros make encoder reshapes collapse to {0,..}
    feeds[i.name] = np.ones(shp, dtype=dt) if dt in (np.int64, np.int32) else np.full(shp, 0.1, dtype=dt)
vals = sess.run(names, feeds)

m = onnx.load(inp)
g = m.graph
prod = {o: n for n in g.node for o in n.output}
for t, v in zip(names, vals):
    pn = prod.get(t)
    if pn is not None:
        g.node.remove(pn)
    g.initializer.append(numpy_helper.from_array(np.asarray(v), t))
g.ClearField("value_info")
onnx.save(m, out)
print("folded:", [(t, tuple(np.asarray(v).shape), str(np.asarray(v).dtype)) for t, v in zip(names, vals)])
