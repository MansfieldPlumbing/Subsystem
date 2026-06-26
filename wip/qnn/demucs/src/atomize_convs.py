#!/usr/bin/env python3
"""
atomize_convs.py <in.onnx> <out.onnx> [budget_mb]

"Make the ops banal." HTP's prepare forces a fat conv's whole activation into one
VTCM handle; a single op can need ~44MB > 8MB VTCM. We split each fat conv into
many small sub-convs that each get their own tiny activation handle, all SHARING
the original weight/bias initializer (instruction reuse). Two exact splits:

  * batch-heavy  [N>1, C, ...]      -> Split axis 0 (batch is independent; exact).
  * spatial-big  [1, C, L]/[1,C,H,W]-> tile the largest spatial axis with a halo:
        Pad(global pads) once, then per-chunk Slice -> Conv(pads=0) -> Concat.
        For output chunk [a,b): padded-input slice = [a*S, (b-1)*S + D*(K-1) + 1),
        conv(pads=0) yields exactly (b-a) outputs. Exact.

Operates on a STATIC graph (run onnxsim first) so all dims are concrete ints.
Verify numerically with onnxruntime before trusting it.
"""
import sys
import math
import numpy as np
import onnx
from onnx import helper, numpy_helper, shape_inference, TensorProto

BUDGET = (float(sys.argv[3]) if len(sys.argv) > 3 else 6.0) * 1024 * 1024  # bytes, fp16


def main():
    m = onnx.load(sys.argv[1])
    m.graph.ClearField("value_info")
    m = shape_inference.infer_shapes(m)
    g = m.graph
    vi = {v.name: [d.dim_value for d in v.type.tensor_type.shape.dim]
          for v in list(g.value_info) + list(g.input) + list(g.output)}
    inits = {i.name for i in g.initializer}

    def attrs(n):
        d = {a.name: a for a in n.attribute}
        K = list(d["kernel_shape"].ints) if "kernel_shape" in d else None
        S = list(d["strides"].ints) if "strides" in d else None
        D = list(d["dilations"].ints) if "dilations" in d else None
        P = list(d["pads"].ints) if "pads" in d else None
        G = d["group"].i if "group" in d else 1
        return K, S, D, P, G

    def const(name, arr):
        g.initializer.append(numpy_helper.from_array(arr.astype(np.int64), name))

    new_nodes, atomized = [], 0
    uid = 0
    for n in g.node:
        if n.op_type != "Conv":
            new_nodes.append(n)
            continue
        x = n.input[0]
        xs = vi.get(x)
        ys = vi.get(n.output[0])
        if not xs or not ys:
            new_nodes.append(n); continue
        act = max(np.prod([d for d in xs if d > 0]), np.prod([d for d in ys if d > 0])) * 2
        if act <= BUDGET:
            new_nodes.append(n); continue

        tiles = math.ceil(act / BUDGET)
        uid += 1
        pre = f"{(n.name or 'conv')}_atom{uid}"
        w = n.input[1]
        b = n.input[2] if len(n.input) > 2 else ""
        spatial = len(xs) - 2  # 1 (Conv1d) or 2 (Conv2d)

        # --- case 1: batch-heavy -> exact batch split ---
        if xs[0] >= tiles and xs[0] > 1:
            base = xs[0] // tiles
            sizes = [base] * tiles
            for i in range(xs[0] - base * tiles):
                sizes[i] += 1
            const(f"{pre}_split", np.array(sizes, np.int64))
            outs = [f"{pre}_xs{i}" for i in range(tiles)]
            new_nodes.append(helper.make_node("Split", [x, f"{pre}_split"], outs, axis=0, name=f"{pre}_split_n"))
            youts = []
            for i in range(tiles):
                yo = f"{pre}_ys{i}"
                ci = [outs[i], w] + ([b] if b else [])
                cn = helper.make_node("Conv", ci, [yo], name=f"{pre}_c{i}")
                cn.attribute.extend(n.attribute)
                new_nodes.append(cn); youts.append(yo)
            new_nodes.append(helper.make_node("Concat", youts, [n.output[0]], axis=0, name=f"{pre}_cat"))
            atomized += 1
            continue

        # --- case 2: spatial tile (largest spatial axis) with halo ---
        K, S, D, P, _ = attrs(n)
        if K is None:
            new_nodes.append(n); continue
        S = S or [1] * spatial
        D = D or [1] * spatial
        P = P or [0] * (2 * spatial)
        ax = 2 + int(np.argmax(xs[2:]))            # the big spatial axis (absolute)
        sidx = ax - 2                               # index within spatial dims
        L = xs[ax]
        k, st, dl = K[sidx], S[sidx], D[sidx]
        pl, pr = P[sidx], P[sidx + spatial]
        Lp = L + pl + pr
        Lout = (Lp - dl * (k - 1) - 1) // st + 1
        if Lout < tiles:
            new_nodes.append(n); continue

        # global pad once on the tiled axis (zeros), keep other axes' pads on the per-tile convs=0
        pads_begin = [0] * len(xs); pads_end = [0] * len(xs)
        pads_begin[ax] = pl; pads_end[ax] = pr
        # also apply the OTHER spatial axes' pads here so per-tile conv uses pads=0 everywhere
        for j in range(spatial):
            if j == sidx:
                continue
            pads_begin[2 + j] = P[j]; pads_end[2 + j] = P[j + spatial]
        const(f"{pre}_pads", np.array(pads_begin + pads_end, np.int64))
        xp = f"{pre}_xp"
        new_nodes.append(helper.make_node("Pad", [x, f"{pre}_pads"], [xp], mode="constant", name=f"{pre}_pad"))

        ob = [round(i * Lout / tiles) for i in range(tiles)] + [Lout]
        youts = []
        for i in range(tiles):
            a, bb = ob[i], ob[i + 1]
            s0 = a * st
            e0 = (bb - 1) * st + dl * (k - 1) + 1
            const(f"{pre}_s{i}", np.array([s0], np.int64))
            const(f"{pre}_e{i}", np.array([e0], np.int64))
            const(f"{pre}_ax{i}", np.array([ax], np.int64))
            sl = f"{pre}_sl{i}"
            new_nodes.append(helper.make_node("Slice", [xp, f"{pre}_s{i}", f"{pre}_e{i}", f"{pre}_ax{i}"], [sl], name=f"{pre}_slice{i}"))
            yo = f"{pre}_y{i}"
            cn = helper.make_node("Conv", [sl, w] + ([b] if b else []), [yo], name=f"{pre}_c{i}")
            # rebuild attrs with pads=0 (global pad already applied)
            cn.attribute.extend([a2 for a2 in n.attribute if a2.name not in ("pads",)])
            cn.attribute.append(helper.make_attribute("pads", [0] * (2 * spatial)))
            new_nodes.append(cn); youts.append(yo)
        new_nodes.append(helper.make_node("Concat", youts, [n.output[0]], axis=ax, name=f"{pre}_cat"))
        atomized += 1

    del g.node[:]
    g.node.extend(new_nodes)
    g.ClearField("value_info")
    onnx.save(m, sys.argv[2])
    print(f"atomized {atomized} conv(s) over budget {BUDGET/1e6:.0f}MB -> {sys.argv[2]}")


if __name__ == "__main__":
    main()
