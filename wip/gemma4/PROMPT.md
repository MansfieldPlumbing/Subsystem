NOTE (2026-06-23): the synthesis below was DRAFTED and grounded by the prep session — see
`SYNTHESIS.md` (the full DELIVER 1–4 + concrete first move, architecture confirmed against the live
Gemma-4 model card) and `DEMUCS-BACKFILL.md`. Your job is NOT to re-derive it cold: read those two,
re-ground the cited claims against the binary + model card (cite-or-refuse), then EXECUTE the concrete
first move in SYNTHESIS.md §6 (export E4B's single-step LLM graph → run one step on dp-onnx → diff
logits vs ORT). The prompt below is the original charter, kept for intent.

────────────────────────────────────────────────────────────────────────────────

ROLE: Fresh session. A sibling session built a home-rolled, ORT-free .NET ONNX interpreter
(dp-onnx) that runs Kokoro TTS end-to-end, and worked out a set of hardware-sympathy "wins" on
AMD Radeon/D3D12 + Qualcomm Hexagon. READ ./research FIRST (SESSION-COMMS.md, QNN-LOG.md,
VEGA_GFX900_ANALYSIS.md, bench.rb.graphruntime-kokoro-parity.result.md, the Roadmap, and
demucs_v4_trt-casestudy/CASE-STUDY.md — Scott's OC: the wu-wei worked example you are to apply).

MISSION: synthesize how to run Gemma 4 E2B/E4B on this stack and federate its experts/submodels
across heterogeneous devices (CPU + Radeon GPU + Hexagon NPU + phone) over DirectPort — a UDP-like
best-effort, one-to-many, fenced, latest-wins transport.

ARCHITECTURE (confirm vs HF cards google/gemma-4-E2B / E4B):
- MatFormer (Matryoshka nested): E2B is a full sub-model inside E4B. Raw 5B/8B, ~2/3 GB footprint.
- Mix-n-Match: custom sizes by slicing per-layer FFN width (8192↔16384) + skipping layers.
- Per-Layer Embeddings (PLE): each decoder layer has its own small per-token embedding, built to be OFFLOADED.
- Multimodal trends encoder-free (Gemma 4 12B is "encoder-free, unified"); 3n used USM + MobileNet-V5
  encoders → one token stream. CONFIRM which applies to E2B/E4B.

DELIVER (a synthesis doc + a concrete first move):
1. ONNX-FIRST: export path E2B/E4B → ONNX (KV-cache, dynamic shapes, MatFormer/PLE/Mix-n-Match,
   any multimodal front-end). Python allowed for EXPORT; runtime is .NET/pwsh only.
2. FEDERATION MAP: which units federate over DirectPort using the model's OWN seams — MatFormer slices,
   PLE tables (stream per-layer = the natural offload), Mix-n-Match layer distribution. Route activations
   device-to-device best-effort/one-to-many; hold per-branch KV as VOM handles.
3. SINGLE-ENCODER: resolve whether E2B/E4B fold all modalities into one token stream / are encoder-free,
   and how a unified stream simplifies BOTH ONNX export and federation.
4. APPLY THE WINS: blob-the-weights/park-the-instructions; W8A16 "sub-pixel" quant (int8 W / A16 acts /
   int32 accumulate / per-channel scale); speculative/parallel decoding expressed as optimistic-concurrency-
   with-rollback (= slot-rollback + DirectPort latest-wins + Fence.WaitN quorum); add-don't-replace (keep
   the working LiteRT LLM backend; dp-onnx is a new GraphRuntime rung BENEATH the LLM turn-contract).

CONSTRAINTS: study recipes, reimplement, do not import. .NET/PowerShell at runtime, not Python.

================================================================================
GROUNDING — added by the onboarding session, 2026-06-23. AUTHORITY: the binary + the hive.
Per docs/CONTRACT.md Rule 10 (cite-or-refuse): this prompt's prose is a map. Before you build on
any claim below, re-ground it at the cited handle — the binary wins on facts, the overseen registry
wins on intent. These are the seams the mission leans on, with where to verify them:

- DirectPort ("best-effort, one-to-many, fenced, latest-wins") IS the one transport spine, not a
  metaphor. Verify: ss_contextualize synchronousCore note ("real threads + Fence handoff — push,
  best-effort, copy-then-share"); CRQ117 (consolidate the canonical DirectPort reference as the one
  transport spine; producers/consumers mount as VOM Broadcast handles); CRQ118 (wireless leg + mDNS);
  CRQ113 (Android AHardwareBuffer backend). Source: src/runspace/windows/DirectPortNative.cs,
  src/runspace/Device/DirectPortVk.cs, src/runspace/windows/DirectPortBench.cs.

- Fence.WaitN quorum is REAL and is the exact primitive for speculative-decode rollback. Source:
  src/runspace/Vom/Fence.cs:38 — WaitN(fences, targets, n), the threshold between WaitAny (n=1) and
  WaitAll (n=M), futex-parked, "2-of-3 sensor consensus." WaitAll = the data barrier (tensors lock at
  phase N); WaitAny = the control switchboard. slot-rollback → src/runspace/Vom/Slot.cs (CRQ135).

- "GraphRuntime rung BENEATH the LLM turn-contract" is the load-bearing framing — confirmed by the
  sibling session in research/SESSION-COMMS.md: "Rb.Runtime is LLM-turn-shaped (prompt→AgentDelta
  tokens), so kokoro is a cmdlet over a GraphRuntime substrate, not an Rb.Runtime leaf." This is an
  OPEN generalization: CRQ109 (generalize the Runtime contract past its LLM shape into
  ConversationalRuntime + GraphRuntime so Rb brokers LiteRT / no-ORT ONNX / QNN / GGML behind one
  contract). Rb's contract note matches. So Gemma-4 on dp-onnx lands the SAME way kokoro did: a
  GraphRuntime substrate, brokered by Rb, NOT a new Rb.Runtime leaf.

- Kokoro state is REAL, with the perf lever already root-caused (read the bench before re-profiling):
  dp-onnx runs all 2463 nodes / 49-of-49 op-types; parity is blocked on an atan2 phase singularity at
  ~zero-magnitude bins (NOT STFT — STFT vs ORT corr 0.999999), and perf is 0.27× realtime with the
  lever being GPU Conv (63% of time is Conv+ConvTranspose), NOT GPU MatMul. Source:
  research/bench.rb.graphruntime-kokoro-parity.result.md. CRQ121 = the ORT-based Kokoro mount; dp-onnx
  is the ORT-FREE reimpl that is the GraphRuntime rung.

- "add-don't-replace" is on-thesis: keep the working LiteRT LLM backend; dp-onnx is a new rung, the
  CRQ109 shape. Do NOT fork or replace Rb.Runtime.

- The floor (from S:\subsystem-project\CLAUDE.md): PowerShell only, no bash/grep/sed — query the live
  system/compiler, never text-scan. Dogfood ss.exe. Your PATH is intentionally naive (raw tools off
  PATH by design); reach for ss. Python is allowed for ONNX EXPORT only; runtime stays .NET/pwsh.

- Onboard FIRST: from S:\subsystem-project\subsystem-main run `ss onboard`, then
  `ss contextualize --map`, then `ss Get-Request` for the live hive. Note INC131/INC137: ss onboard
  over the MCP seam degrades the disk-doc sections and the MCP runspace lacks ss-refs/ss-check — run
  the CLI from the repo for full output.
