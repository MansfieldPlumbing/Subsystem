#!/usr/bin/env python3
"""tile_proof.py build|check — minimal proof of the "our own spills" thesis.

A 3x (1x1 conv) chain on the post-STFT spectro shape [1,4,512,336]. The middle
activation [1,96,512,336] = 33MB > 8MB VTCM (must fail finalize untiled). The
TILED graph splits time(336) into K tiles, runs the SAME convs per tile, concats
-> biggest tile activation [1,96,512,ceil(336/K)] < 8MB (must finalize). 1x1 conv
is pointwise so split->conv->concat is BIT-EXACT: proves tiling is free correctness.

build: writes untiled.onnx + tiled.onnx (shared seeded weights) + ref I/O, checks
       onnxruntime untiled==tiled.
"""
import sys
import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

O = r"S:\qnn\demucs\out\tileproof"
IN = [1, 4, 512, 336]          # post-STFT spectro (small: 1.4MB) = the safe split point
CH = [4, 48, 96, 48]           # channel chain; mid [1,96,512,336] = 33MB
K = 6                          # time tiles -> 336/6 = 56 -> [1,96,512,56] = 5.5MB
SEED = 0


def _weights():
    rng = np.random.default_rng(SEED)
    return [rng.standard_normal((CH[i + 1], CH[i], 1, 1)).astype(np.float32) * 0.05
            for i in range(3)]


def _conv(inp, out, wname):
    return helper.make_node("Conv", [inp, wname], [out], kernel_shape=[1, 1],
                            strides=[1, 1], pads=[0, 0, 0, 0])


def build():
    import os
    os.makedirs(O, exist_ok=True)
    W = _weights()
    inits = [numpy_helper.from_array(W[i], f"w{i}") for i in range(3)]
    xin = helper.make_tensor_value_info("input", TensorProto.FLOAT, IN)
    xout = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, CH[3], IN[2], IN[3]])

    # --- untiled: straight 3-conv chain (mid tensor = 33MB) ---
    nodes = [_conv("input", "h0", "w0"), _conv("h0", "h1", "w1"), _conv("h1", "output", "w2")]
    g = helper.make_graph(nodes, "untiled", [xin], [xout], inits)
    m = helper.make_model(g, opset_imports=[helper.make_opsetid("", 17)])
    m.ir_version = 9
    onnx.save(m, f"{O}\\untiled.onnx")

    # --- tiled: Split time(axis3)->K, same convs per tile, Concat ---
    base = IN[3] // K
    sizes = [base] * (K - 1) + [IN[3] - base * (K - 1)]
    split_init = numpy_helper.from_array(np.array(sizes, dtype=np.int64), "splits")
    tnodes = [helper.make_node("Split", ["input", "splits"],
                               [f"t{j}_0" for j in range(K)], axis=3)]
    for j in range(K):
        tnodes += [_conv(f"t{j}_0", f"t{j}_1", "w0"),
                   _conv(f"t{j}_1", f"t{j}_2", "w1"),
                   _conv(f"t{j}_2", f"t{j}_o", "w2")]
    tnodes.append(helper.make_node("Concat", [f"t{j}_o" for j in range(K)], ["output"], axis=3))
    g2 = helper.make_graph(tnodes, "tiled", [xin], [xout], inits + [split_init])
    m2 = helper.make_model(g2, opset_imports=[helper.make_opsetid("", 17)])
    m2.ir_version = 9
    onnx.save(m2, f"{O}\\tiled.onnx")

    # --- numeric equivalence (onnxruntime) ---
    import onnxruntime as ort
    rng = np.random.default_rng(1)
    x = rng.standard_normal(IN).astype(np.float32)
    x.tofile(f"{O}\\input.raw")
    r = {}
    for tag in ("untiled", "tiled"):
        s = ort.InferenceSession(f"{O}\\{tag}.onnx", providers=["CPUExecutionProvider"])
        r[tag] = s.run(None, {"input": x})[0]
    md = float(np.max(np.abs(r["untiled"] - r["tiled"])))
    print(f"mid tensor untiled = [1,{CH[2]},{IN[2]},{IN[3]}] = {CH[2]*IN[2]*IN[3]*2/1e6:.1f}MB (fp16)")
    print(f"max tile activation = [1,{CH[2]},{IN[2]},{sizes[0]}] = {CH[2]*IN[2]*sizes[0]*2/1e6:.1f}MB (fp16)")
    print(f"K={K} tiles, split sizes={sizes}")
    print(f"NUMERIC: untiled vs tiled  max|diff| = {md:.3e}  exact={md==0.0}")


if __name__ == "__main__":
    build()
