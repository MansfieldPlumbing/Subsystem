#!/usr/bin/env python3
"""verify_atom.py <orig.onnx> <atom.onnx> [length] — prove atomization is math-preserving."""
import sys
import numpy as np
import onnxruntime as ort

length = int(sys.argv[3]) if len(sys.argv) > 3 else 343980
x = np.random.randn(1, 2, length).astype(np.float32)
so = ort.SessionOptions()
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL  # compare raw graphs
o1 = ort.InferenceSession(sys.argv[1], so, providers=["CPUExecutionProvider"]).run(None, {"input": x})[0]
o2 = ort.InferenceSession(sys.argv[2], so, providers=["CPUExecutionProvider"]).run(None, {"input": x})[0]
print("shapes:", o1.shape, o2.shape)
print("max abs diff:", float(np.max(np.abs(o1 - o2))))
print("allclose(1e-3):", bool(np.allclose(o1, o2, rtol=1e-3, atol=1e-3)))
