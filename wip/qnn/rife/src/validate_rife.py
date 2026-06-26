#!/usr/bin/env python3
"""validate_rife.py prep|compare — prove the RIFE DLC == the onnx (QnnCpu vs onnxruntime).
prep: gen seeded inputs (NCHW), run onnxruntime ref, write raws + qnn input_list.
compare: load qnn-net-run output, diff against the onnx ref (allclose)."""
import glob
import os
import sys

import numpy as np

o = r"S:\qnn\rife\out"
ref_npy = os.path.join(o, "ref_out.npy")
mode = sys.argv[1]

if mode == "prep":
    import onnxruntime as ort
    np.random.seed(0)
    img0 = np.random.rand(1, 3, 256, 256).astype(np.float32)
    img1 = np.random.rand(1, 3, 256, 256).astype(np.float32)
    ts = np.array([0.5], dtype=np.float32)
    for n, a in (("img0", img0), ("img1", img1), ("timestep", ts)):
        a.tofile(os.path.join(o, n + ".raw"))
    sess = ort.InferenceSession(os.path.join(o, "rife_sim.onnx"), providers=["CPUExecutionProvider"])
    ref = sess.run(None, {"img0": img0, "img1": img1, "timestep": ts})[0]
    np.save(ref_npy, ref)
    with open(os.path.join(o, "input_list.txt"), "w") as f:
        f.write(f"img0:={o}\\img0.raw img1:={o}\\img1.raw timestep:={o}\\timestep.raw\n")
    print("prep done. onnx ref shape:", ref.shape, "range", float(ref.min()), float(ref.max()))

elif mode == "compare":
    ref = np.load(ref_npy)
    cands = sorted(glob.glob(os.path.join(o, "qnn_out", "**", "*.raw"), recursive=True))
    print("qnn output files:", cands)
    q = np.fromfile(cands[0], dtype=np.float32)
    print("ref elems", ref.size, "qnn elems", q.size)
    direct = q.reshape(ref.shape)
    md = float(np.max(np.abs(ref - direct)))
    ok = bool(np.allclose(ref, direct, rtol=1e-3, atol=1e-3))
    print(f"[NCHW direct] max|diff|={md:.3e}  allclose(1e-3)={ok}")
    if not ok and ref.ndim == 4:  # fallback: maybe output came back NHWC
        nhwc = q.reshape(ref.shape[0], ref.shape[2], ref.shape[3], ref.shape[1]).transpose(0, 3, 1, 2)
        md2 = float(np.max(np.abs(ref - nhwc)))
        print(f"[NHWC->NCHW]  max|diff|={md2:.3e}  allclose(1e-3)={bool(np.allclose(ref, nhwc, rtol=1e-3, atol=1e-3))}")
