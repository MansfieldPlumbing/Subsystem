#!/usr/bin/env python3
"""
analyze_onnx.py — inspect an ONNX graph for QNN/HTP portability.

Prints: op histogram, inputs/outputs (+shapes), initializer count, and — the
point of this tool — every spectral op (STFT/DFT/RFFT/IRFFT/FFT) that Hexagon
HTP cannot delegate, with its attributes, so we know exactly what the
Conv1d/ConvTranspose1d surrogate must replace.

Usage:
    python analyze_onnx.py <model.onnx>
"""
import sys
from collections import Counter

import onnx
from onnx import shape_inference

SPECTRAL = {"STFT", "DFT", "RFFT", "IRFFT", "FFT", "Mel", "MelWeightMatrix"}
# Ops with no native HTP coverage (common offenders worth flagging early).
HTP_RISKY = SPECTRAL | {"ScatterND", "GridSample", "NonZero", "Loop", "If", "Scan"}


def dims(t):
    s = t.type.tensor_type.shape
    return [d.dim_value if d.HasField("dim_value") else (d.dim_param or "?") for d in s.dim]


def main(path):
    print(f"=== {path} ===")
    m = onnx.load(path, load_external_data=False)
    g = m.graph

    opset = {imp.domain or "ai.onnx": imp.version for imp in m.opset_import}
    print(f"opset: {opset}")
    print(f"nodes: {len(g.node)}   initializers: {len(g.initializer)}")

    print("\n-- inputs --")
    for i in g.input:
        print(f"  {i.name:24} {dims(i)}")
    print("-- outputs --")
    for o in g.output:
        print(f"  {o.name:24} {dims(o)}")

    hist = Counter(n.op_type for n in g.node)
    print(f"\n-- op histogram ({len(hist)} distinct) --")
    for op, c in hist.most_common():
        flag = "  <-- HTP-RISKY" if op in HTP_RISKY else ""
        print(f"  {op:24} {c}{flag}")

    spectral_nodes = [n for n in g.node if n.op_type in SPECTRAL]
    print(f"\n-- spectral ops (HTP cannot delegate): {len(spectral_nodes)} --")
    for n in spectral_nodes:
        print(f"  {n.op_type}  name={n.name}")
        print(f"     inputs : {list(n.input)}")
        print(f"     outputs: {list(n.output)}")
        for a in n.attribute:
            print(f"     attr {a.name} = {onnx.helper.get_attribute_value(a)}")

    risky = sum(c for op, c in hist.items() if op in HTP_RISKY)
    print(f"\nVERDICT: {risky} HTP-risky node(s). "
          + ("Needs surrogate/decomposition before qairt-converter."
             if risky else "Graph looks HTP-clean."))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)
    main(sys.argv[1])
