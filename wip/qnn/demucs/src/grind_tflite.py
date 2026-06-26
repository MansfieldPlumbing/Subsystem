#!/usr/bin/env python3
"""
grind_tflite.py — the relentless Demucs-v4 ONNX -> TFLite converter.

It sits there and tries. And tries. And tries. Every conversion backend, every
flag permutation, and — when the in-graph STFT blocks delegation — the
FFT-externalized split fallback. Each candidate is VALIDATED (load in the TFLite
interpreter, run a dummy forward, check the [1,6,2,T] stem shape) and scored.
Nothing stops the loop except a clean win (or --forever to collect ALL winners
for the shader tournament).

WHY THIS IS HARD (read once):
    Your export internalizes _spec() (STFT) in the graph. TensorRT fuses the
    FFT; TFLite/QNN/XNNPACK have NO FFT/STFT/DFT op they can delegate. So the
    whole-graph tflite either fails to convert or silently dumps the spectral
    subgraph onto CPU custom ops. The fix is the SPLIT: re-export an FFT-free
    core (waveform + external spectrogram as two inputs), convert THAT to tflite
    (fully delegatable), and do STFT/iSTFT in host code or a compute shader.
    The grinder tries whole-graph first (in case a future opset lands STFT),
    then auto-falls-back to the split.

ENV (run on the workstation / WSL2 — NOT the phone):
    micromamba create -n demucs-tflite python=3.11 -c conda-forge -y
    micromamba activate demucs-tflite
    pip install onnx onnxruntime onnxsim numpy tensorflow ai-edge-litert
    pip install onnx2tf ai-edge-torch          # the two converters
    pip install torch torchaudio demucs        # only needed for the split re-export

USAGE:
    python grind_tflite.py --onnx ../models/demucsv4.onnx --out ./tflite_out
    python grind_tflite.py --onnx ../models/demucsv4.onnx --forever   # collect all passers
"""
from __future__ import annotations
import argparse, itertools, json, os, shutil, subprocess, sys, time, traceback
from pathlib import Path

SEGMENT = 343980            # fixed chunk @ 44.1 kHz (from the export script)
STEM_OUT = (1, 6, 2, SEGMENT)

def log(msg: str):
    ts = time.strftime("%H:%M:%S")
    line = f"[{ts}] {msg}"
    print(line, flush=True)
    with open(RESULTS_DIR / "grind.log", "a", encoding="utf-8") as f:
        f.write(line + "\n")

def record(entry: dict):
    with open(RESULTS_DIR / "results.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")

def run(cmd: list[str], timeout: int, cwd: str | None = None) -> tuple[bool, str]:
    """Run a strategy in its own process so a hard native crash can't take us down."""
    try:
        p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout)
        out = (p.stdout or "") + "\n" + (p.stderr or "")
        return p.returncode == 0, out[-8000:]
    except subprocess.TimeoutExpired:
        return False, f"TIMEOUT after {timeout}s"
    except Exception as e:
        return False, f"EXC {e}\n{traceback.format_exc()[-2000:]}"

# --- ONNX inspection: is the STFT/DFT in the graph? (decides whole-graph vs split) ---
def onnx_op_summary(onnx_path: Path) -> dict:
    import onnx
    m = onnx.load(str(onnx_path), load_external_data=False)
    ops = {}
    for n in m.graph.node:
        ops[n.op_type] = ops.get(n.op_type, 0) + 1
    ins  = [(i.name, [d.dim_value for d in i.type.tensor_type.shape.dim]) for i in m.graph.input]
    outs = [(o.name, [d.dim_value for d in o.type.tensor_type.shape.dim]) for o in m.graph.output]
    has_fft = any(k in ops for k in ("STFT", "DFT", "RFFT", "IRFFT", "FFT"))
    return {"ops": ops, "inputs": ins, "outputs": outs, "has_fft": has_fft}

# --- VALIDATION: a tflite is only a "win" if it loads, runs, and yields [1,6,2,T] ---
def validate_tflite(tflite_path: Path) -> tuple[bool, str, float]:
    try:
        try:
            from ai_edge_litert.interpreter import Interpreter   # new name
        except ImportError:
            from tensorflow.lite import Interpreter              # fallback
        import numpy as np
        it = Interpreter(model_path=str(tflite_path))
        it.allocate_tensors()
        ins, outs = it.get_input_details(), it.get_output_details()
        for d in ins:
            shp = [s if s > 0 else (SEGMENT if s == -1 and d is ins[0] else 1) for s in d["shape"]]
            it.set_tensor(d["index"], np.zeros(d["shape"] if all(s > 0 for s in d["shape"]) else shp,
                                               dtype=d["dtype"]))
        t0 = time.time()
        it.invoke()
        dt = (time.time() - t0) * 1000
        oshape = tuple(int(x) for x in outs[0]["shape"])
        ok = (len(oshape) == 4 and oshape[1] == 6) or oshape == STEM_OUT
        return ok, f"out={oshape} in={[d['shape'].tolist() for d in ins]} {dt:.0f}ms", dt
    except Exception as e:
        return False, f"validate EXC {e}", 0.0

# --- STRATEGY GENERATORS ---------------------------------------------------------
def onnx2tf_variants(onnx_path: Path, outdir: Path):
    """onnx2tf is the workhorse. Permute the flags that matter for delegation."""
    flagsets = [
        [],                                         # plain
        ["-b", "1"],                                # static batch
        ["-ois", f"input:1,2,{SEGMENT}"],           # fully static input -> best for GPU/NPU delegate
        ["-b", "1", "-kat", "input"],               # keep input as-is
        ["-b", "1", "-nuo", "--non_verbose"],       # no opset upgrade
        ["-osd"],                                    # output signaturedefs
        ["-b", "1", "-dgc"],                         # disable group conv (Adreno-friendly)
    ]
    for i, extra in enumerate(flagsets):
        sub = outdir / f"onnx2tf_{i}"
        yield (f"onnx2tf[{i}] {' '.join(extra) or 'plain'}",
               ["onnx2tf", "-i", str(onnx_path), "-o", str(sub), *extra],
               sub, 1800)

def onnxsim_then_onnx2tf(onnx_path: Path, outdir: Path):
    simp = outdir / "simplified.onnx"
    def make():
        ok, out = run(["onnxsim", str(onnx_path), str(simp)], 900)
        if not ok:
            return None
        return list(onnx2tf_variants(simp, outdir / "post_sim"))
    return ("onnxsim->onnx2tf", make)

def write_split_export(repo_python: Path) -> Path:
    """Generate an FFT-free re-export: model(waveform, spec) as TWO inputs, no _spec
    in the graph. This is the form that converts cleanly (STFT done host/shader-side)."""
    p = repo_python / "_export_demucs_split.py"
    p.write_text('''import torch, warnings
from demucs.pretrained import get_model
warnings.filterwarnings("ignore")
SEG = 343980
m = get_model("htdemucs_6s").models[0]; m.cpu().eval()
class Core(torch.nn.Module):
    """FFT-free: caller passes the precomputed spectrogram z; no _spec() in-graph."""
    def __init__(s, mdl): super().__init__(); s.m = mdl
    def forward(s, wav, z): return s.m(wav, z)
wav = torch.randn(1, 2, SEG)
z = m._spec(wav)                     # shape it once to trace
torch.onnx.export(Core(m), (wav, z), "demucsv4_core_fftfree.onnx",
    opset_version=17, input_names=["input","spec"], output_names=["output"],
    dynamic_axes={"input":{0:"b",2:"t"}, "output":{0:"b",3:"t"}},
    do_constant_folding=True)
print("wrote demucsv4_core_fftfree.onnx")
''', encoding="utf-8")
    return p

# --- MAIN GRIND ------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--out", default="./tflite_out")
    ap.add_argument("--forever", action="store_true", help="collect ALL passers for the tournament, don't stop on first win")
    args = ap.parse_args()

    global RESULTS_DIR
    RESULTS_DIR = Path(args.out).resolve()
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    onnx_path = Path(args.onnx).resolve()
    repo_python = Path(__file__).resolve().parent

    log(f"=== GRIND START — {onnx_path.name} -> tflite ===")
    try:
        summ = onnx_op_summary(onnx_path)
        log(f"onnx ops={len(summ['ops'])} inputs={summ['inputs']} outputs={summ['outputs']} has_fft={summ['has_fft']}")
        record({"event": "inspect", **summ})
        if summ["has_fft"]:
            log("!! in-graph FFT detected — whole-graph tflite will likely fail; split fallback is armed.")
    except Exception as e:
        log(f"onnx inspect failed (continuing blind): {e}")
        summ = {"has_fft": True}

    winners = []
    attempt = 0

    def try_candidate(name, sub: Path):
        nonlocal attempt
        # find the produced .tflite (onnx2tf writes <name>_float32.tflite etc.)
        cands = sorted(sub.rglob("*.tflite"), key=lambda p: p.stat().st_size, reverse=True) if sub.exists() else []
        if not cands:
            log(f"  [{name}] produced NO tflite"); record({"attempt": attempt, "name": name, "ok": False, "reason": "no_tflite"}); return False
        for c in cands:
            ok, note, ms = validate_tflite(c)
            log(f"  [{name}] {c.name}: {'WIN' if ok else 'fail'} — {note}")
            record({"attempt": attempt, "name": name, "tflite": str(c), "ok": ok, "note": note, "ms": ms})
            if ok:
                keep = RESULTS_DIR / f"demucs_v4{'_core' if 'fftfree' in c.name or 'core' in name else ''}.tflite"
                shutil.copy2(c, keep)
                winners.append({"name": name, "path": str(keep), "ms": ms, "note": note})
                log(f"  >>> kept {keep.name}")
                return True
        return False

    # Build the strategy queue: whole-graph first, then the FFT-free split.
    def strategy_round(target_onnx: Path, tag: str):
        nonlocal attempt
        won = False
        for name, cmd, sub, to in onnx2tf_variants(target_onnx, RESULTS_DIR / f"{tag}"):
            attempt += 1
            log(f"[try #{attempt}] {tag}/{name}")
            ok, out = run(cmd, to)
            (RESULTS_DIR / f"{tag}_{attempt}.log").write_text(out, encoding="utf-8")
            if try_candidate(f"{tag}/{name}", sub):
                won = True
                if not args.forever: return True
        # ai-edge-torch direct (separate process, optional)
        return won

    outer = 0
    while True:
        outer += 1
        log(f"--- round {outer} ---")
        won = strategy_round(onnx_path, "whole")
        if won and not args.forever:
            break

        # FFT-free split: re-export then convert the core
        log("arming FFT-free split re-export…")
        exp = write_split_export(repo_python)
        ok, out = run([sys.executable, str(exp)], 1800, cwd=str(repo_python))
        (RESULTS_DIR / f"split_export_{outer}.log").write_text(out, encoding="utf-8")
        core = repo_python / "demucsv4_core_fftfree.onnx"
        if ok and core.exists():
            log("split core exported — converting FFT-free graph")
            won2 = strategy_round(core, "split")
            if won2 and not args.forever:
                break
        else:
            log(f"split re-export failed (need torch+demucs in env): see split_export_{outer}.log")

        if winners and not args.forever:
            break
        log(f"round {outer} produced {len(winners)} winner(s); looping…")
        time.sleep(2)
        if outer >= 3 and not args.forever:
            log("3 rounds, no clean whole/split win — leaving the rest to --forever mode.")
            break

    log(f"=== GRIND DONE — {len(winners)} winner(s) ===")
    for w in winners:
        log(f"  WIN {w['name']}: {Path(w['path']).name} ({w['ms']:.0f}ms dummy) — {w['note']}")
    record({"event": "done", "winners": winners})
    if not winners:
        log("No winner yet. Read results.jsonl — most failures are op-coverage (the FFT) or dynamic shapes.")
        sys.exit(1)

if __name__ == "__main__":
    main()
