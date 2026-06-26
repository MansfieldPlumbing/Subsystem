#!/usr/bin/env python3
"""
reexport_demucs_segment.py <seconds> <out.onnx>

Re-export htdemucs_6s at a SMALLER segment so a single time-encoder conv's
activation fits Hexagon V73 VTCM. The 7.8s default (343,980 samp) needs ~44MB
for /tencoder.0/dconv/.../Conv_2d, but V73 VTCM is 4-8MB. A shorter chunk scales
that down linearly (host does overlap-add across chunks).

Mirrors Demucs_v4_TRT's WaveformOnlyWrapper (STFT internalized in-graph) but
exports STATIC (no dynamic_axes) at the chosen length, opset 17. Same weights,
smaller input -> smaller graph; no retraining.
"""
import os
import sys
import warnings

import torch
from demucs.pretrained import get_model

warnings.filterwarnings("ignore")

seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 2.0
out = sys.argv[2] if len(sys.argv) > 2 else f"demucsv4_seg{int(seconds * 1000)}ms.onnx"

model = get_model("htdemucs_6s").models[0]
model.cpu().eval()
sr = int(getattr(model, "samplerate", 44100))
length = model.valid_length(int(seconds * sr))
print(f"segment={seconds}s  samplerate={sr}  valid_length={length}", flush=True)


class WaveformOnlyWrapper(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.model = m

    def forward(self, x):
        z = self.model._spec(x)          # STFT inside the graph (TRT/HTP fusion)
        return self.model(x, z)


w = WaveformOnlyWrapper(model)
dummy = torch.randn(1, 2, length)
try:
    torch.onnx.export(w, dummy, out, opset_version=17, dynamo=False,
                      input_names=["input"], output_names=["output"],
                      do_constant_folding=True, export_params=True)
except Exception as e:                    # demucs version may want forward(mix) only
    print(f"[wrapper(x,z) failed: {e}] retrying with plain forward(x)", flush=True)
    torch.onnx.export(model, dummy, out, opset_version=17, dynamo=False,
                      input_names=["input"], output_names=["output"],
                      do_constant_folding=True, export_params=True)

print(f"wrote {out} ({os.path.getsize(out) / 1e6:.0f} MB)  length={length}")
