#!/usr/bin/env python3
"""check_rank.py <model.onnx> — clear baked value_info, re-infer, report rank>=6 tensors."""
import sys
import onnx
from onnx import shape_inference, numpy_helper

m = onnx.load(sys.argv[1])
g = m.graph
for it in g.initializer:
    if it.name.endswith("Concat_8_output_0"):
        print("Reshape_9 target =", numpy_helper.to_array(it))
g.ClearField("value_info")
m2 = shape_inference.infer_shapes(m)
six = [(v.name, [d.dim_value for d in v.type.tensor_type.shape.dim])
       for v in m2.graph.value_info
       if len(v.type.tensor_type.shape.dim) >= 6]
print("TRUE rank>=6 tensors:", len(six))
for x in six[:8]:
    print("  ", x)
