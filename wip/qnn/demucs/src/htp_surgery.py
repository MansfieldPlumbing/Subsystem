#!/usr/bin/env python3
"""
htp_surgery.py <in.onnx> <out.onnx> [--passes p1,p2,...]

ONNX graph surgery to get models past qairt-converter / onto Hexagon HTP.
Each pass is a discrete, named transform. These are the python prototypes of
what onnx-surgeon.exe will do natively (the python-exit endgame) — proven here
first so the native port copies a known-good recipe.

Passes:
  tile_repeats_int64    Cast every Tile 'repeats' input to int64 (converter folds
                        float-mixed Concat repeats to float64 -> np.tile dies).
  squeeze_rank6_leading HTP max tensor rank is 5. Demucs' local-attention windowing
                        makes a rank-6 cluster Reshape(->6D, leading dim==1) ->
                        Transpose -> Pad -> Pad -> Reshape. The leading dim is size 1,
                        so squeeze it through the chain (drop from entry Reshape target,
                        shift Transpose perm / Pad axes down 1). Exit Reshape untouched
                        (squeezing a size-1 dim preserves element count).
                        Run AFTER onnxsim --overwrite-input-shape (needs static shapes
                        + constant Pad amounts).
"""
import argparse
import numpy as np
import onnx
from onnx import helper, numpy_helper, shape_inference, TensorProto


def tile_repeats_int64(m):
    g = m.graph
    tiles = [(i, n) for i, n in enumerate(g.node)
             if n.op_type == "Tile" and len(n.input) >= 2]
    for i, n in reversed(tiles):
        rep = n.input[1]
        cast_out = rep + "_i64"
        cast = helper.make_node("Cast", [rep], [cast_out], to=TensorProto.INT64,
                                name=(n.name or f"tile{i}") + "_repcast")
        n.input[1] = cast_out
        g.node.insert(i, cast)
    return len(tiles)


def squeeze_rank6_leading(m):
    g = m.graph
    si = shape_inference.infer_shapes(m).graph
    vi = {v.name: [d.dim_value for d in v.type.tensor_type.shape.dim]
          for v in list(si.value_info) + list(si.input) + list(si.output)}
    inits = {i.name: i for i in g.initializer}
    cons = {}
    for n in g.node:
        for i in n.input:
            cons.setdefault(i, []).append(n)

    def set_init(name, arr):
        for k, it in enumerate(g.initializer):
            if it.name == name:
                g.initializer[k].CopyFrom(numpy_helper.from_array(arr.astype(np.int64), name))
                return True
        return False

    touched = 0
    for entry in list(g.node):
        if entry.op_type != "Reshape":
            continue
        out = entry.output[0]
        if vi.get(out, []) [:1] != [1] or len(vi.get(out, [])) != 6:
            continue
        tname = entry.input[1]
        if tname not in inits:
            continue
        tgt = numpy_helper.to_array(inits[tname]).copy()
        if len(tgt) != 6:
            continue
        set_init(tname, tgt[1:])                       # drop leading dim from target

        cur = out
        while True:
            cs = cons.get(cur, [])
            if len(cs) != 1:
                break
            nx = cs[0]
            if nx.op_type == "Transpose":
                perm = next(a for a in nx.attribute if a.name == "perm")
                p = [x - 1 for x in perm.ints if x != 0]    # drop axis 0, shift down
                del perm.ints[:]
                perm.ints.extend(p)
                cur = nx.output[0]
            elif nx.op_type == "Pad":
                pname = nx.input[1]
                if pname in inits:
                    pa = numpy_helper.to_array(inits[pname]).copy()
                    r = len(pa) // 2
                    new = np.array(list(pa[:r])[1:] + list(pa[r:])[1:], dtype=np.int64)
                    set_init(pname, new)
                cur = nx.output[0]
            else:
                break                                   # exit Reshape — leave it
        touched += 1
    return touched


PASSES = {
    "tile_repeats_int64": tile_repeats_int64,
    "squeeze_rank6_leading": squeeze_rank6_leading,
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("inp")
    ap.add_argument("out")
    ap.add_argument("--passes", default=",".join(PASSES))
    args = ap.parse_args()

    m = onnx.load(args.inp)
    for name in [p.strip() for p in args.passes.split(",") if p.strip()]:
        if name not in PASSES:
            raise SystemExit(f"unknown pass: {name}")
        print(f"  pass {name}: touched {PASSES[name](m)} node(s)")
    m.graph.ClearField("value_info")   # shapes changed; stale value_info breaks ORT
    onnx.save(m, args.out)
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
