"""
export_gemma4_e2b_decomposed.py — Gemma 4 E2B → DECOMPOSED ONNX for dp-onnx.

Goal: a single decode-step graph (past_kv in / present_kv out) exported WITHOUT the
fused attention ops (GroupQueryAttention / RotaryEmbedding / RMSNormalization). Decomposed
ops land on dp-onnx's existing ~70-op set (MatMul, Gelu, Pow/ReduceMean/Sqrt/Div/Mul = RMSNorm,
Sin/Cos/Slice/Neg/Concat = RoPE, Softmax, Gather, Tanh = softcap), so NO new dp-onnx handlers
are needed for the proof. This is the CORRECTNESS proof, not the product — see PHASE1-STATE.md:
the LLM is the host-side decode loop, not this one-shot graph.

PREREQS (the current blocker):
  1. pip install transformers   (into the demucs-export env)
  2. HF auth + accept the Gemma-4 license:  huggingface-cli login   (gated, ~10 GB E2B)
  Run with:  S:\qnn-project\_disposable-python\mamba\envs\demucs-export\python.exe export_gemma4_e2b_decomposed.py

dynamo=False forces the legacy tracer (emits decomposed ops, not the Dynamo fused graph).
opset 17. external-data keeps the graph proto < 2 GB (weights in a sidecar) — the QNN-LOG protobuf trick.
"""
import os
os.environ.setdefault("HF_HUB_CACHE", r"W:\hf-cache\hub")  # 10 GB bf16 download → W: (556 GB), NOT S:/C:.
# NOTE: leave HF_HOME alone so the saved token (C:\bin\xdg\cache\huggingface\token) still resolves for auth.
import torch, onnx

# torch.onnx opset-17 has no symbolic for aten::diff (Gemma4 masking calls it as a Tensor METHOD .diff(),
# so a Python monkeypatch on torch.diff is bypassed). Register the ONNX symbolic at the LOWERING layer:
# diff(x, n=1, dim) = x[1:] - x[:-1] = Slice + Sub (both native in dp-onnx).
from torch.onnx import register_custom_op_symbolic
def _diff_sym(g, self, n, dim, prepend, append):
    INT_MAX = 9223372036854775807
    try:
        from torch.onnx import symbolic_helper as _sh
        d = _sh._maybe_get_const(dim, "i"); d = int(d) if isinstance(d, int) else -1
    except Exception:
        d = -1
    c  = lambda v: g.op("Constant", value_t=torch.tensor([v], dtype=torch.long))
    sl = lambda x, s, e: g.op("Slice", x, c(s), c(e), c(d))
    return g.op("Sub", sl(self, 1, INT_MAX), sl(self, 0, -1))
register_custom_op_symbolic("aten::diff", _diff_sym, 17)

from transformers import AutoModelForCausalLM, AutoConfig

MODEL = "google/gemma-4-E2B"            # gated; 5.1B params total (~2.8B is offloadable PLE+vocab tables)
OUT   = r"W:\gemma4\onnx\e2b_step.onnx"
os.makedirs(os.path.dirname(OUT), exist_ok=True)

# Load NATIVE bf16 (~10 GB), not fp32 (~20 GB). dp-onnx kernels are fp32: either (a) cast weights with
# .float() before export → fp32 ONNX ~20 GB on W: (needs ~20 GB RAM), or (b) keep bf16 ONNX (~10 GB) and
# add a bf16→fp32 cast-on-load in dp-onnx. Decide at the export/probe step; default here = native bf16.
print(f"loading {MODEL} (native bf16, HF_HUB_CACHE={os.environ.get('HF_HUB_CACHE','default')})...")
model = AutoModelForCausalLM.from_pretrained(MODEL, torch_dtype="auto", attn_implementation="eager")
model.eval()                            # eager attn = decomposed (NOT sdpa/flash), the whole point
cfg = AutoConfig.from_pretrained(MODEL)
tcfg = getattr(cfg, "text_config", cfg)   # Gemma4Config is multimodal — text decoder params live under text_config
vocab = getattr(tcfg, "vocab_size", None) or getattr(cfg, "vocab_size", None)
print(f"layers={tcfg.num_hidden_layers} hidden={tcfg.hidden_size} kv_heads={getattr(tcfg,'num_key_value_heads','?')} "
      f"head_dim={getattr(tcfg,'head_dim','?')} vocab={vocab} sw={getattr(tcfg,'sliding_window','?')}")

# PROOF = a no-cache PREFILL forward — cleanest export, validates op coverage + lets us diff logits
# vs llama.cpp on a prompt. The KV-cache single-step graph (for the decode loop) is the NEXT iteration.
model.config.use_cache = False
B, seq = 1, 8
input_ids = torch.randint(0, vocab, (B, seq), dtype=torch.long)
torch.onnx.export(
    model,
    (input_ids,),
    OUT,
    input_names=["input_ids"],
    output_names=["logits"],
    dynamic_axes={"input_ids": {0: "batch", 1: "seq"}},
    opset_version=17,
    do_constant_folding=True,
    dynamo=False,                       # legacy tracer → decomposed ops (no fused GQA/RoPE)
)
m = onnx.load(OUT, load_external_data=False)
hist = {}
for n in m.graph.node:
    hist[n.op_type] = hist.get(n.op_type, 0) + 1
print(f"\nexported {OUT}  ({len(m.graph.node)} nodes)")
print("op histogram:", dict(sorted(hist.items(), key=lambda kv: -kv[1])))
print("\nNEXT:  dp-onnx probe <onnx>   then   dp-onnx run <onnx> --inputs <dir>   then diff logits vs llama.cpp")
