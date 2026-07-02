# Latent-reasoning + executive-gating hook spec (DPX decode)

Muse-authored spec for the sprint session (owns `Dp.cs`/`DpxDecoder.cs`). CRQ176 rung 3.
Two modes, one static-toggle seam modeled exactly on `DpxExperiments` MVP-2. No new verbs, no
new files beyond `DpxExperiments` additions. The hook point already exists — `DecodeLoopSplit`'s
`onNode` callback (`DpxDecoder.cs:417`) hands every node its `(node, outs, env)`, which is where the
pre-`lm_head` hidden state is readable without surgery.

## Mode A — latent-plan (no-KV-discretization excursion)

Run the decoder for K steps feeding the last hidden state back as the next `inputs_embeds`, skipping
vocab projection / argmax / detokenize, then resume normal decode.

**Change 1 — `DpxExperiments.cs` (additive, mirror `CaptureLogits`/`RecordLogits`):**
```
public static bool  LatentMode = false;     // active during a latent excursion
public static int   LatentSteps = 0;        // K steps to run latent before resuming
public static float[] CapturedHidden;       // [1536] copy of the last-position hidden feeding lm_head
public static void RecordHidden(NodeProto n, IReadOnlyDictionary<string,Tensor> env) {
    if (!LatentMode || n.Output.Count == 0 || !n.Output[0].Contains("lm_head")) return;
    var h = env[n.Input[0]];                 // lm_head's input = final hidden state [1,S,1536]
    int hid = h.Shape[^1]; long rows = h.Count / hid; var src = h.AsF();
    int off = (int)((rows - 1) * hid);       // last position only
    CapturedHidden = new float[hid];
    for (int i = 0; i < hid; i++) CapturedHidden[i] = src[off + i];
}
```
Reset in `ResetRun()` alongside the existing counters.

**Change 2 — `DpxDecoder.DecodeLoopSplit` (~line 417, the `onNode` callback):** one line —
`DpxExperiments.RecordHidden(node, env);` inside the existing callback. `env` is already the value
dict passed to `onNode`; no new plumbing.

**Change 3 — `DpxDecoder.DecodeLoopSplit` top of loop (~line 384-399):** when
`DpxExperiments.LatentMode && DpxExperiments.CapturedHidden != null`, substitute the embed step:
skip `_embedInterp.Run(...)`, set `feed["inputs_embeds"] = Tensor.F(CapturedHidden, 1, 1, 1536)`,
skip the argmax block (`434-444`), skip `seq.Add` and `Detokenize`/`writer`. After K latent steps,
clear `LatentMode` and resume normal decode from the current KV state.

### KV correctness constraint (load-bearing)
Latent steps are REAL forward passes at REAL positions — let them grow KV normally (the
`present`->`past` carry at `459-484` is unchanged) and resume decode after. The chain stays
monotonic and uncorrupted; the latent "thoughts" simply occupy KV positions the way tokens do. Do
NOT overwrite or reorder existing KV. If thoughts must be ephemeral, snapshot the `kvCacheHandles`
regions before the excursion and `Close`+restore after — heavier, only if a receipt shows persisted
thoughts hurt. Default: KV-grows.

### Open decision (state it, do not silently pick)
`per_layer_inputs` (gemma PLE) is a per-TOKEN lookup; in latent mode there is no token id. Options:
(a) reuse the prior step's `per_layer_inputs` region, (b) zero it. (a) is the safer default — the PLE
contribution stays in-distribution — but this needs a France->Paris coherence receipt either way.

## Mode B — executive/gating (single pass, no KV append)

One forward, read logits at 2-3 candidate token ids, no argmax-over-262144, no KV persist.

**`DpxExperiments.cs`:** `public static bool GatingMode; public static int[] GatingCandidates;
public static float[] GatingLogits;` — after the graph runs and `logits` is in hand
(`DpxDecoder.cs:430`), when `GatingMode` copy `logits[lastTokenIndex*vocab + c]` for each candidate
`c` into `GatingLogits`, then `return` (skip argmax, skip the `459-484` KV-persist block entirely).
This is the stage-2-of-competence self-assessment: cost = one forward, zero KV growth.

## Verdict framing for these modes
Neither mode changes a single kernel — they are decode-loop control changes behind static toggles,
exactly the DpxExperiments shape. Ship them ONLY behind the toggle (default false) so normal decode
is byte-identical. The receipt that matters: does latent mode change the emitted token vs plain
decode on France->Paris, and does it ever help a real ss-operator task — measure before believing.
