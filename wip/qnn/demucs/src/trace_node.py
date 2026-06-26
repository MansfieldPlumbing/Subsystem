#!/usr/bin/env python3
"""trace_node.py <model.onnx> <node_name> [input_index]
Trace a node's input back through its producers/initializers to find dtype/source.
Used to diagnose qairt-converter failures (e.g. float repeats feeding Tile)."""
import sys
import onnx
from onnx import numpy_helper

m = onnx.load(sys.argv[1], load_external_data=False)
g = m.graph
target = sys.argv[2]
which = int(sys.argv[3]) if len(sys.argv) > 3 else 1

prod = {o: n for n in g.node for o in n.output}
inits = {i.name: i for i in g.initializer}


def desc(name, depth=0, seen=None):
    seen = seen or set()
    if name in seen or depth > 12:
        return
    seen.add(name)
    pad = "  " * depth
    if name in inits:
        arr = numpy_helper.to_array(inits[name])
        print(f"{pad}{name} = INIT dtype={arr.dtype} shape={arr.shape} vals={arr.flatten()[:8]}")
        return
    if name in prod:
        n = prod[name]
        print(f"{pad}{name} <- {n.op_type} ({n.name})")
        for a in n.attribute:
            if a.name in ("to", "value"):
                print(f"{pad}    attr {a.name} = {onnx.helper.get_attribute_value(a) if a.name=='to' else '<tensor>'}")
        for i in n.input:
            desc(i, depth + 1, seen)
    else:
        print(f"{pad}{name} = GRAPH INPUT/external")


for n in g.node:
    if n.name == target:
        print(f"NODE {n.name}  op={n.op_type}")
        for idx, i in enumerate(n.input):
            print(f"  input[{idx}] = {i}")
        print(f"--- trace input[{which}] ---")
        if len(n.input) > which:
            desc(n.input[which])
        break
else:
    print(f"node {target} not found")
