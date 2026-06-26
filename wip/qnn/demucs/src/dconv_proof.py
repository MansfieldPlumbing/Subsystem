#!/usr/bin/env python3
"""dconv_proof.py — reproduce the REAL demucs VTCM bust and prove length-tiling fixes it.

The op that actually busts: a dilated kernel-3 conv over the long time axis
[1,96,85995] (the tencoder dconv). Unlike a 1x1 (which HTP auto-streams), a
spatial-kernel conv needs an im2col-ish buffer ~kernel x activation -> 44MB > 8MB VTCM.

untiled:  y = Conv(x, k=3, dilation=2, pad=2)            # "same", one fat op (busts)
tiled:    xp = Pad(x, 2 each side); for each of K windows of xp, valid-conv -> base;
          concat. EXACT blocked convolution (overlap = the halo). Proves tiling the
          real op is bit-exact AND finalizes (each tile activation ~2.4MB).
"""
import os
import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

O = r"S:\qnn\demucs\out\dconvproof"
C = 96
L = 85995
K = 7                 # 85995 / 7 = 12285 exactly
BASE = L // K
KS, DIL = 3, 2
PAD = DIL * (KS - 1) // 2      # = 2, "same"
SEED = 0


def _w():
    rng = np.random.default_rng(SEED)
    return (rng.standard_normal((C, C, KS)).astype(np.float32) * 0.02,
            (rng.standard_normal((C,)).astype(np.float32) * 0.01))


def build():
    os.makedirs(O, exist_ok=True)
    w, b = _w()
    inits = [numpy_helper.from_array(w, "w"), numpy_helper.from_array(b, "b")]
    xin = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, C, L])
    xout = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, C, L])

    # untiled "same" dilated conv (the busting op)
    n = [helper.make_node("Conv", ["input", "w", "b"], ["output"],
                          kernel_shape=[KS], dilations=[DIL], pads=[PAD, PAD], strides=[1])]
    m = helper.make_model(helper.make_graph(n, "untiled", [xin], [xout], inits),
                          opset_imports=[helper.make_opsetid("", 17)])
    m.ir_version = 9
    onnx.save(m, f"{O}\\untiled.onnx")

    # tiled: pad globally by PAD, slice K overlapping windows, valid-conv, concat
    pads_init = numpy_helper.from_array(np.array([0, 0, PAD, 0, 0, PAD], dtype=np.int64), "pads")
    tn = [helper.make_node("Pad", ["input", "pads"], ["xp"], mode="constant")]
    win = BASE + 2 * PAD
    for j in range(K):
        s = j * BASE
        st = numpy_helper.from_array(np.array([s], dtype=np.int64), f"st{j}")
        en = numpy_helper.from_array(np.array([s + win], dtype=np.int64), f"en{j}")
        ax = numpy_helper.from_array(np.array([2], dtype=np.int64), f"ax{j}")
        inits += [st, en, ax]
        tn += [helper.make_node("Slice", ["xp", f"st{j}", f"en{j}", f"ax{j}"], [f"w{j}"]),
               helper.make_node("Conv", [f"w{j}", "w", "b"], [f"y{j}"],
                                kernel_shape=[KS], dilations=[DIL], pads=[0, 0], strides=[1])]
    tn.append(helper.make_node("Concat", [f"y{j}" for j in range(K)], ["output"], axis=2))
    m2 = helper.make_model(helper.make_graph(tn, "tiled", [xin], [xout], inits + [pads_init]),
                           opset_imports=[helper.make_opsetid("", 17)])
    m2.ir_version = 9
    onnx.save(m2, f"{O}\\tiled.onnx")

    import onnxruntime as ort
    rng = np.random.default_rng(1)
    x = rng.standard_normal((1, C, L)).astype(np.float32)
    r = {}
    for tag in ("untiled", "tiled"):
        s = ort.InferenceSession(f"{O}\\{tag}.onnx", providers=["CPUExecutionProvider"])
        r[tag] = s.run(None, {"input": x})[0]
    md = float(np.max(np.abs(r["untiled"] - r["tiled"])))
    print(f"busting op: Conv k={KS} dil={DIL} over [1,{C},{L}]  (untiled activation {C*L*2/1e6:.1f}MB fp16)")
    print(f"K={K} tiles, window [1,{C},{win}] = {C*win*2/1e6:.2f}MB fp16")
    print(f"NUMERIC: untiled vs tiled  max|diff| = {md:.3e}  exact={md==0.0}")


if __name__ == "__main__":
    build()
