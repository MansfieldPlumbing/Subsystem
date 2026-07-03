# Fleet blueprints — device-loop build plans (2026-07-02, verified against live code)

Baseline to beat: razr CPU decode 2.45 tok/s. Measured GPU q4 ceiling (D3D12, this box): MLP up-proj 21.9x, qkv 2.7x.
The phone died on per-call weight re-upload churn (GpuVulkan.GemmQ4 vkMapMemory+Marshal.Copy every call); AHB deletes it.

## STATUS (2026-07-02, updated as landed)
GPU slice — LANDED + VALIDATED ON RAZR:
  - FILE 1 (AhbNative.describe/Free): done (commit ab18436)
  - FILE 2 (DpTensor.AllocBlobAhb, VOM-registered): done (ab18436)
  - FILE 3a (Tensor._ahbWeight instance field): done (5ee536e)
  - FILE 3b (FromProto case 2/3 Android AHB hook, fault-degrading): done + VALIDATED — full gemma4-e2b
    load on the razr produced ZERO fault-degrades; every q4 weight allocated as an AHB blob; clean run +
    clean reclaim. The AHB alloc/lock/map/fill/read/unlock/release lifecycle works on real Adreno.
GPU slice — REMAINING (the reap; needs Vulkan validation layers on-device):
  - FILE 3c/3d (Dp.cs: thread AhbWeight + weightKey into Gpu.dpgpu_gemm_q4 / GpuMatMulNBitsResident;
    make QueryResidentQ4 Vulkan-aware; skip bSpan.ToArray() when AHB-resident)
  - FILE 4 (GpuVulkan.cs: enable the AHB device extensions in EnsureInit; add the 4 import structs +
    vkGetAndroidHardwareBufferPropertiesANDROID via vkGetDeviceProcAddr; ImportAhbBuffer; GemmQ4 imports
    the AHB cached per weightKey instead of MkBuf+Marshal.Copy every call) — detail below, exact sTypes.
  - Then re-land presence-flow (the reverted bd7ae76 pattern). Reaps the 21.9x MLP win, zero copy churn.
NPU + scrcpy slices below: DESIGN-only, unstarted.

## DESIGN plan for making the NPU (Cylinder 3) a GRAPH PEER for the gemma4-e2b embedder face. Verified against the real code: DpxQnn.Project (src/runspace/Dpx/DpxQnn.cs) today only PREPARES+EMITS a one-MatMul context binary â€” there is NO from-binary mount, NO graphRetrieve, NO execute-against-a-mounted-graph, and NO fence/overlap wiring in DecodeLoopSplit (src/runspace/Dpx/DpxDecoder.cs:448). The plan is four seams: (1) grow DpxQnn op coverage from one MatMul to the embedder op set; (2) add a from-binary MOUNT verb + a QueryEmbedder execute path so the emitted .bin becomes a live, VOM-owned guest graph; (3) refactor DecodeLoopSplit into three Vom.Spawn workers (embed t+1 / decode t / sample t-1) coordinated by CpuFence WaitAll/WaitN; (4) a presence-not-permission degradation ladder so an absent/late NPU rung falls to the CPU _embedInterp path without a mid-turn mode switch. Every verb used (Project/Mount/Query/Signal/Wait/Spawn/Terminate/Alloc/Close) is already in the approved bucket of src/analyzers/SystemCatalog.json; Dpx already dependsOn Rb+Vom; no new component edge and no async/await needed (SS018-clean â€” the overlap is real threads + Fence, never Task).

THE OP-COVERAGE LIST (embedder face, the "friendly face": fixed shapes, no KV). Confirmed against Dp.Dispatch (src/runspace/Dpx/Dp.cs:419-516) and the split loop (DpxDecoder.cs:488, which runs _embedInterp on {input_ids} -> inputs_embeds + per_layer_inputs). The embedder emits two activation tensors and uses this op subset, with the QNN op-package + node shape each transcribes to (mirroring the proven MatMul node at DpxQnn.cs:199-202: OpConfig{PackageName="qti.aisw", TypeName=<X>} + input/output Tensor arrays):
 - GatherBlockQuantized (THE dominant op â€” the q4 embedding-table lookup; Dp.cs:1250). ONNX attrs bits=4, block_size=32, gather_axis=0, quantize_axis=1. Inputs: data(packed q4 bytes, Type=STATIC), indices(input_ids, APP_WRITE), scales(STATIC), optional zero_points(STATIC). QNN: no stock qti.aisw block-quant gather â€” transcribe as DequantizeLinear(block) folded to a STATIC fp16/bf16 table + qti.aisw "Gather" (axis=0), OR keep this op CPU-side and hand QNN the already-dequantized embed matmul (see openQuestions). This is the single highest-risk transcription and gates the whole face.
 - SimplifiedLayerNormalization / RMSNorm (Dp.cs:1314): y = x/sqrt(mean(x^2)+eps)*w, attrs epsilon, axis=-1. QNN: qti.aisw "RmsNorm" (HTP-native) with epsilon param + STATIC weight.
 - Mul / Add (elementwise embed scaling, e.g. the sqrt(hidden) scale + per_layer_inputs assembly): qti.aisw "ElementWiseMultiply" / "ElementWiseAdd".
 - MatMulNBits (per-layer input projection, com.microsoft; Dp.cs:510): the SAME q4 block-weight shape as the proven MatMul proof but with block dequant â€” transcribe as STATIC dequant-to-bf16 + qti.aisw "MatMul" (the proof's exact node), OR qti.aisw "FullyConnected".
 - Reshape / Cast: qti.aisw "Reshape"; Cast is layout-preserving (Dp.cs:452) so it folds out.
 NOTE (scope-correct): RotaryEmbedding (Dp.cs:1283) and GroupQueryAttention (Dp.cs:1344) are DECODER-face ops (they need position_ids + KV) and are correctly NOT in the embedder coverage set â€” the goal's "embedder face first, no KV" is exactly this partition.

Files: S:\subsystem\src\runspace\Dpx\DpxQnn.cs, S:\subsystem\src\runspace\Dpx\DpxDecoder.cs, S:\subsystem\src\runspace\Dpx\DpxQnnGuest.cs, S:\subsystem\src\runspace\Vom\Ps.cs, S:\subsystem\src\analyzers\SystemCatalog.json

PER-FILE DIFF-PLAN (paths are worktree-relative to S:\subsystem\; all API/struct/index names verified against the files read).

=== FILE 1: src/runspace/Dpx/DpxQnn.cs (extend the Project verb; add the missing table indices + a graph-builder seam) ===
WHY: Project() today hard-builds ONE MatMul node inline (lines 189-202) and the QnnInterface index table (lines 58-60) is missing the from-binary + retrieve entries the mount path needs.
CHANGES:
 (1a) Add QnnInterface table indices next to the existing FnBackendCreate=1..FnDeviceCreate=40 block (line 58-60). Needed: FnContextCreateFromBinary (=10, the entry between contextCreate=9 and contextGetBinarySize=11 in QNN_INTERFACE_VER_TYPE ordering), FnContextFree (=13, after contextGetBinary=12), FnGraphRetrieve (=16, after graphCreate=15/before graphAddNode=18 â€” verify the exact ordinal against QnnInterface.h on device; the file already documents errorGetVerboseMessage=49 as the anchor landmark so the count is checkable). These are DECLARATIONS only; the mount path in File 3 calls through them.
 (1b) Extract the inline node build (lines 189-202) into a private `AddNode(IntPtr graph, IntPtr* fn, string typeName, Tensor[] ins, Tensor[] outs, (string,object)[] params)` helper that reproduces the proven sequence exactly: createTensor each (fn[FnTensorCreateGraphTensor], line 192), pack an `inputs`/`outputs` `Tensor*` array with `sizeof(Tensor)` stride (the 144-byte struct, line 42-43 â€” the stride that MUST stay 144 or v73 0x1775), build OpConfig{Version=1, PackageName="qti.aisw", TypeName, NumInputs/Inputs/NumOutputs/Outputs} (lines 199-201), fn[FnGraphAddNode] (line 202). Keep the existing MatMul path calling AddNode so the proven byte-identical receipt is unchanged (regression guard).
 (1c) Add param marshalling to AddNode: the embedder ops carry scalar attrs (RmsNorm epsilon, Gather axis). A Qnn_Param_t is {paramType@0=SCALAR(0), name@8, Qnn_Scalar_t{dataType, union value}} â€” lay it out by explicit x64 offset, the same discipline as CreateOfflineHtpDevice (lines 69-116). NumParams/Params on OpConfigV1 (lines 48) are already in the struct, currently 0.
 (1d) Add a graph-source overload `Project(ModelProto embedGraph, string backendLibrary, string outputBinaryPath, uint socModel, uint dspArch, uint vtcmSizeMb)` that WALKS embedGraph.Graph.Node and, for each node in the coverage list, maps ONNX op -> AddNode(qti.aisw typeName). Reuse the existing prepare/emit tail verbatim (finalize fn[FnGraphFinalize] line 203, getBinarySize/getBinary lines 208-216, File.WriteAllBytes line 215). Keep the synthetic one-MatMul Project() as the smoke path. GatherBlockQuantized/MatMulNBits get the "dequant-to-STATIC-bf16 + Gather/MatMul" transcription (see openQuestions #1); STATIC tensor bytes come from decoding the packed q4 initializer (the sequential-nibble layout the file cites at line 131, test.dpx.q4-packing-order.ps1).
 INVARIANT: Project still returns a one-line receipt string; still leaves backend/context/graph/device to the runtime for VOM-cascade teardown (lines 227-228). No async. Method name stays "Project" (approved verb).

=== FILE 2 (NEW): src/runspace/Dpx/DpxQnnGuest.cs (the MOUNT + execute-from-binary â€” the piece that does NOT exist today) ===
WHY: nothing in-tree mounts an emitted .bin as a live graph or executes against it. This is the "graph PEER, expensive-prepare/cheap-execute" half. Name it a Guest (SystemCatalog Rb note: "a foreign engine is a mounted GUEST"); no banned suffix.
SHAPE (all P/Invoke through the same fn table DpxQnn resolves at DpxQnn.cs:150-154):
 - `public sealed unsafe class DpxQnnGuest` with:
   - `public static DpxQnnGuest Mount(Owner turnOwner, string backendLibrary, string binaryPath)`: NativeLibrary.Load(backendLibrary) (as DpxQnn.cs:149); getProviders -> fn table (lines 150-154); fn[FnBackendCreate](line 157 shape); read binaryPath bytes into an rpcmem/native buffer; fn[FnContextCreateFromBinary](backend, device=0, cfg=0, binPtr, binSize, &context, profile=0); fn[FnGraphRetrieve](context, graphName="embed", &graph). REGISTER the guest as a managed Handle under turnOwner via Vom.Register(turnOwner, "DpxQnnGuest", this, onReclaim: () => fn[FnContextFree](context), subdir:"Objects", name:"embedGuest") â€” so Terminate(turnOwner) cascades the HTP context free (invariant 2: handle=authority; the .bin is a mounted Handle, refcounted, freed on zero â€” NOT a copied blob). Verified Register signature at Vom.cs:72 and onReclaim semantics at Vom.cs:79.
   - `public DpTensor QueryEmbedder(Owner owner, ReadOnlySpan<long> tokenIds, int seqLen)`: alloc a VOM-native input region + two output regions (inputs_embeds, per_layer_inputs) via DpTensor.Alloc(owner, shape, VomFormat.Float32, withFence:true, ...) (verified DpTensor.cs:56); point QNN app-write/app-read Tensor.V1.Buf.Data at Data.Resource (the zero-copy import â€” same pointer-aliasing the decode loop already does at DpxDecoder.cs:512 with Tensor.F((float*)kv.Data.Resource)); fn[FnGraphExecute] (the exact delegate shape at DpxQnn.cs:239: <IntPtr,void*,uint,void*,uint,IntPtr,IntPtr,ulong>); Signal the output DpTensor's fence (DpTensor.Signal, DpTensor.cs:157) so a waiter unparks. Returns the inputs_embeds handle (per_layer_inputs handle exposed via an out-param or a small struct).
   - Verb names: Mount/Query/Signal â€” all approved (SystemCatalog verbs line 47-50).
 INVARIANT: no async; native data plane off-GC; every result is a VOM Handle (invariant 2), not a managed float[]. On-device only (a human closes the loop) â€” the class compiles on both heads (pure P/Invoke, resolved at load, exactly as SubsystemWin.csproj:79 notes for DpxQnn).

=== FILE 3: src/runspace/Dpx/DpxDecoder.cs (fence-pipelined overlap + presence ladder in DecodeLoopSplit) ===
WHY: DecodeLoopSplit (line 448-604) runs embed (line 488 _embedInterp.Run) then decoder (line 524 _interp.Run) then sample (lines 537-551) STRICTLY SEQUENTIALLY. The overlap is absent.
CHANGES:
 (3a) At BringUp/entry to DecodeLoopSplit, add a presence probe: try DpxQnnGuest.Mount(owner, htpBackendLib, embedBinPath); on any fault set `Owner? embedGuest = null` and log â€” do NOT throw (invariant 9: absence flows to the next rung). Pin embedGuest's Handle to the TURN owner (the `owner` param already cascades on cancel via ct.Register->Terminate at DpxDecoder.cs:207-210), so the HTP context dies with the turn.
 (3b) Introduce three CpuFences: `embedFence, decodeFence, sampleFence = new CpuFence()` (verified Fence.cs:125). Refactor the `while (step < _maxTokens)` body (line 477) into a software pipeline driven by Vom.Spawn (verified Ps.cs:19) â€” three child workers under `owner`:
    - EMBED worker: for phase t, if embedGuest != null -> guest.QueryEmbedder(childOwner, {seq[t]}, S) and embedFence.Signal(t+1); ELSE _embedInterp.Run (the current line 488 path) and Signal â€” the SAME fence, so the consumer never branches on which rung produced it (presence-not-permission).
    - DECODE worker: Fence.WaitAll([embedFence],[t+1]) (verified Fence.cs:31) then _interp.Run(feed) (line 524, feed assembled from the embed outputs + KV ring exactly as lines 493-522) and decodeFence.Signal(t+1). GPU-vs-CPU q4 is ALREADY a presence choice inside _interp (Dp.cs UseGpuMatMul, line 448) â€” no mode switch here.
    - SAMPLE worker: Fence.WaitAll([decodeFence],[t+1]) then the argmax (lines 541-551), seq.Add, writer(AgentDelta) (line 566), and sampleFence.Signal(t+1) which is the doorbell the EMBED worker waits on for t+1's prefill. EOS/end-of-turn break (line 558) cancels via owner.Token.
    The main loop thread parks on Fence.WaitAll([sampleFence],[_maxTokens]) OR the EOS latch â€” the barrier that keeps async/ThreadPool jitter from tearing the pipeline (the exact property WaitPhaseLockTest proves, Ps.cs:192).
 (3c) KV ring is UNCHANGED (lines 466-472, 569-595): the decoder worker owns it single-threaded, so the ring-append stays race-free (only the decode phase writes present_kv).
 (3d) DEGRADATION LADDER (invariant 9): the loop preamble sets rung presence flags ONCE before the pipeline starts (embedGuest!=null, GPU-present). If a worker's input fence never advances within a bounded wait, the worker is Interrupt-unwound by the cascade (Ps.cs:42-49) â€” but the correct design is: EMBED worker's guest path is wrapped so a QNN execute fault demotes THAT worker to _embedInterp for the rest of the turn (a one-way demote, mirroring the KV ringLive.Remove demote at line 579), never a whole-loop mode switch. TEST hook: reuse the WaitQuorumTest/WaitPhaseLockTest pattern (Ps.cs:163-222) to prove the barrier holds when the embed rung stalls.
 INVARIANT: Vom.Spawn not Task.Run; Fence.WaitAll not await (SS018). Workers are children of the turn owner -> cascade-terminate (Vom.cs:205). GetBenchmark counters (lines 554-556) move into the SAMPLE worker.

=== FILE 4: src/runspace/Vom/Ps.cs (OPTIONAL â€” a bounded-wait quorum test for the pipeline) ===
WHY: WaitQuorumTest (line 163) and WaitPhaseLockTest (line 192) prove WaitN/WaitAll under jitter but not the 3-stage embed/decode/sample handoff with a STALLED rung.
CHANGE: add `WaitPipelineTest()` (triage verb "Wait"+noun is fine; matches the existing test-method convention) that spins three CpuFences as embed/decode/sample, stalls the embed fence, and asserts the decode worker parks (never spins, never deadlocks) and the presence-demote path releases it. Returns a JSON verdict like the siblings. Purely additive; no signature changes.

=== FILE 5: src/analyzers/SystemCatalog.json (registration note only; NO new verb, NO new component, NO new edge) ===
WHY: the Dpx component note (line 13) says "DpxQnn ... is the next build"; this thread turns it into the mounted-guest embedder peer.
CHANGE: append to the Dpx "note" that the QNN context-binary is now MOUNTED as a VOM guest (DpxQnnGuest) and the embedder face runs as a graph peer with fence-pipelined overlap. Verbs Mount/Project/Query/Signal/Wait are ALL already approved (lines 47-50); Dpx already dependsOn ["Vom","Cm","Dg","Rb"] (line 13) and the HTP backend init reaches Device which is already registered (line 15) â€” so SS014 needs NO new edge. No async added -> synchronousCore (line 26) unaffected. This edit is documentation-of-record, not a vocabulary change (keeps the ratchet honest).

BUILD GATE (for the human who closes the loop): S:\subsystem\ss.exe build -p <repo> for the Windows head (DpxQnnGuest compiles as pure P/Invoke), then ss build apk -p <repo> for the 27-analyzer fail-closed gate. On-device parity: QueryEmbedder(inputs_embeds) vs _embedInterp.Run(inputs_embeds) byte/rel-diff on the razr v73, the same PASS/FAIL discipline as DpxQnn.Project's maxRel<2e-2 verdict (DpxQnn.cs:251).

---

## Zero-copy q4 weight residency on the Adreno via AHardwareBuffer. Design-only diff-plan (device-in-the-loop; a human closes it on hardware). KEY RECON CORRECTION: the recon assumed MatMulNBits weights flow through MakeQuant/Tensor.Qb, but they do NOT. The MatMulNBits B weight is a separate ONNX UINT8 initializer read via x[1].ReadRawb(), decoded through Dp.FromProto case 2/3 â€” which ALREADY VOM-backs it via DpTensor.Alloc(VomFormat.Bytes) and Tensor.SetNativeRawb. So the AHB hook is FromProto (via a new DpTensor.AllocBlobAhb), NOT MakeQuant. MakeQuant/Qb is a different packing convention (QGemm/GatherBlockQuantized embeddings) and is out of scope for the MatMulNBits GPU seam. The VOM layer already anticipates this: Handle.Resource is documented as "NativeMemory ptr | pinned GCHandle | AHardwareBuffer" and Vom.RegisterNative(owner, type, native, reclaim, ...) exists to register a foreign native pointer with a free-at-zero Reclaim â€” the exact seam to tie the AHB's lifetime to the weight owner's refcount (invariant 3). Because the weight bytes already live in a stable native region that ReadRawb() exposes and MatMulNBitsSimd/GpuMatMulNBits already read without branching, the residency win is: (1) make that native region an AHB blob on Android, (2) tag the Tensor with the AHB handle, (3) teach GpuVulkan.GemmQ4 to import that AHB as a VkDeviceMemory/VkBuffer once (cached per weightKey) instead of MkBuf+vkMapMemory+Marshal.Copy every call, (4) make the resident seam (Gpu.QueryResidentQ4 / GpuMatMulNBitsResident) Vulkan-aware so it stops passing bSpan.ToArray() copies. Weights are UMA on the razr, so CPU fills once and GPU reads the same bytes â€” zero copy churn, which is what killed lmkd in the reverted bd7ae76. Fault/absence latches to CPU at every rung (invariant 9).

Files: src/runspace/Dpx/AhbNative.cs, src/runspace/Dpx/DpTensor.cs, src/runspace/Dpx/Dp.cs, src/runspace/Dpx/GpuVulkan.cs

EXACT FILE-LEVEL DIFF-PLAN (verified against the code that exists). Ordered load-time -> import-time -> seam.

=== FILE 1: src/runspace/Dpx/AhbNative.cs (extend; ~2 additions) ===
WHY: GpuVulkan must import the AHB as VkDeviceMemory, which needs the buffer's properties + a describe. Add the two NDK entry points AllocBlob doesn't yet expose.
CHANGE 1a: Add P/Invoke `AHardwareBuffer_describe(IntPtr buffer, out Desc desc)` (libandroid.so). Needed so the import path can read back Width (= byte length) without the caller threading it separately, and so a debug-assert can confirm Format==BLOB before import.
CHANGE 1b: (No unlock-at-alloc change.) Confirm existing AllocBlob keeps the CPU mapping live for the buffer's lifetime (it does â€” unlock is deferred to reclaim). Add an explicit `Free(IntPtr buffer, IntPtr mapped)` helper: `AHardwareBuffer_unlock(buffer, IntPtr.Zero)` then `AHardwareBuffer_release(buffer)`, to be the Reclaim action registered with Vom.RegisterNative. This keeps unlock+release paired and colocated with AllocBlob.
NOTE: AllocBlob already sets Usage = CpuWriteRarely|CpuReadRarely|GpuDataBuffer â€” GpuDataBuffer (1<<24) is exactly what makes the blob importable as a VkBuffer. No usage change needed.

=== FILE 2: src/runspace/Dpx/DpTensor.cs (add one allocation shape) ===
WHY: FromProto case 2/3 currently calls DpTensor.Alloc(owner, dims, VomFormat.Bytes) which routes to Vom.Alloc (NativeMemory, AlignedFree reclaim). On Android we instead want the backing to BE an AHB blob, registered via Vom.RegisterNative so it enumerates/refcounts/reclaims identically.
CHANGE 2a: Add `public static DpTensor AllocBlobAhb(Owner owner, int[] shape, int byteCount, out IntPtr ahb, string subdir="Weights", string? name=null)`:
  - `(IntPtr buf, IntPtr mapped) = AhbNative.AllocBlob(byteCount);`
  - `var h = VomClass.RegisterNative(owner, "DpTensor.Packed.Ahb", mapped, () => AhbNative.Free(buf, mapped), byteCount, VomFormat.Bytes, subdir, name);`  // Resource = the CPU-mapped ptr (what ReadRawb reads), Reclaim releases the AHB at refcount-zero (inv-3, handle=authority)
  - `ahb = buf;`  // the buffer handle GpuVulkan imports
  - `return new DpTensor(owner, h, shape, null, null, 0, 0);`  // private ctor already exists
  Rationale for Resource==mapped (not buf): Handle.Resource is the pointer kernels dereference; ReadRawb() must yield the CPU-mapped bytes. The AHB *handle* (buf) is separate metadata carried on the Tensor (see 3b). On UMA the mapped ptr and the GPU-imported buffer alias the same physical memory â€” that is the whole zero-copy claim.
CHANGE 2b: (No change to Alloc/AllocQuant.) Keep DpTensor immutable/readonly; the AHB handle is surfaced via the out-param, stored on the consuming Tensor, not inside DpTensor.

=== FILE 3: src/runspace/Dpx/Dp.cs (four edits) ===

EDIT 3a â€” Tensor: carry the AHB handle + weightKey (Tensor.cs region, near _nativeRawb at line 158-163).
  Add fields: `private IntPtr _ahbWeight; ` (0 = not AHB-backed) and expose `public IntPtr AhbWeight => _ahbWeight;` plus `public void SetAhbWeight(IntPtr h){ _ahbWeight = h; }`.
  WHY on the Tensor not static: SS015 forbids static mutable state; the AHB handle rides the weight Tensor whose lifetime already gates the backing region (its owner's refcount). Clone() (line 215) must copy _ahbWeight too so a resident-cache lookup survives Clone (weights aren't cloned on the hot path, but keep it consistent).

EDIT 3b â€” FromProto case 2/3 (lines 2561-2574): on Android + owner!=null, allocate the weight backing as AHB.
  Current: `var dt = DpTensor.Alloc(owner, dims, VomFormat.Bytes, ...); t.RawData.Span.CopyTo(dt.ReadBytes()); rt.SetNativeRawb((byte*)dt.Data.Resource, byteLen);`
  New (Android gate, fault-degrades â€” inv-9):
    ```
    if (owner != null) {
      DpTensor dt; IntPtr ahb = IntPtr.Zero;
      try {
        if (OperatingSystem.IsAndroid()) { dt = DpTensor.AllocBlobAhb(owner, dims, byteLen, out ahb, name: ...); }
        else                            { dt = DpTensor.Alloc(owner, dims, VomFormat.Bytes, subdir:"Weights", name: ...); }
      } catch (Exception ex) {           // AHB alloc failed (fragmentation/OOM): degrade to plain VOM region
        Subsystem.Dg.Log("dpx", $"AHB weight alloc failed, CPU rung: {ex.Message}");
        dt = DpTensor.Alloc(owner, dims, VomFormat.Bytes, subdir:"Weights", name: ...);
        ahb = IntPtr.Zero;
      }
      t.RawData.Span.CopyTo(dt.ReadBytes());              // the ONE fill (into the AHB map on UMA)
      var rt = new Tensor { Shape = dims };
      rt.SetNativeRawb((byte*)dt.Data.Resource, byteLen); // ReadRawb() path unchanged â€” CPU rung still works verbatim
      if (ahb != IntPtr.Zero) rt.SetAhbWeight(ahb);
      return rt;
    }
    ```
  NOTE: this must NOT throw SS018 (stays fully synchronous â€” no async). The catch has a body (no empty-catch SS007). Only the B weight (UINT8, case 2/3) becomes AHB; scale (x[2], fp32, case 1) and zp (x[3], UINT8) stay as-is â€” scale is ~KBs, its upload cost is negligible vs the 33MB+ Bq, matching the D3D12 ResidentQ4 rationale.

EDIT 3c â€” Gpu.dpgpu_gemm_q4 + QueryResidentQ4 (lines 2644-2658): thread the AHB handle to Vulkan and make residency Vulkan-true.
  - Change `Gpu.dpgpu_gemm_q4(...)` Vulkan branch to pass the B weight's AHB handle: it needs `Tensor bTensor` (or its AhbWeight IntPtr) + weightKey. Cleanest: add `IntPtr ahbBq` and `long weightKey` params already flow; call `GpuVulkan.GemmQ4(A, Bq, scales, zp, C, M, N, K, blockSize, hasZp, s_spvQ4, ahbBq, weightKey)`.
  - `QueryResidentQ4(long weightKey)`: currently `!s_vk && GpuD3D12...`. New: `s_vk ? GpuVulkan.QueryResidentQ4(weightKey) : GpuD3D12.QueryResidentQ4(weightKey)`.

EDIT 3d â€” GpuMatMulNBitsResident (lines 947-958) + GpuMatMulNBits (895-928): pass AHB identity down, and skip bSpan.ToArray() when the weight is AHB-resident on Vulkan.
  - GpuMatMulNBitsResident: after computing `resident`, ALSO capture `IntPtr ahbBq = x[1].AhbWeight;`. When `ahbBq != 0` (Android AHB weight), the Bq copy is never needed â€” pass `Array.Empty<byte>()` for bArr and forward ahbBq. Signature of GpuMatMulNBits gains an `IntPtr ahbBq = default` trailing param (Windows callers pass default).
  - GpuMatMulNBits (line 924): pass `ahbBq` into `Gpu.dpgpu_gemm_q4(a, bArr, scsp, zpArr, c, ..., key, tileN, tileM, ahbBq)`. The GpuWeightKey(x[1], rowBytes*N) key already exists and is stable per weight Tensor identity â€” reuse it as the Vulkan residency-cache key.
  WHY: today GpuMatMulNBitsResident (line 952) only checks D3D12 residency, so on Vulkan `resident` is always false and it always does `bSpan.ToArray()` (a 33MB churn per GEMM). With the AHB present, the copy is structurally unnecessary â€” the GPU imports the same bytes.

=== FILE 4: src/runspace/Dpx/GpuVulkan.cs (the core: AHB import + per-weightKey residency cache) ===

EDIT 4a â€” EnsureInit(): enable the required device extensions (currently DevCI.ec=0 â€” NONE enabled).
  AHB import needs, at vkCreateDevice: `VK_ANDROID_external_memory_android_hardware_buffer`, `VK_KHR_external_memory`, `VK_KHR_sampler_ycbcr_conversion` (dependency), `VK_EXT_queue_family_foreign`, `VK_KHR_bind_memory2`, `VK_KHR_get_memory_requirements2`. Mirror DirectPortVk.cs lines 128-147: `Marshal.StringToHGlobalAnsi` each name into a pinned array, set `DevCI.ec` + `DevCI.ppE`. Guard to Android only (Windows GpuVulkan path â€” DPGPU_BACKEND=vulkan parity â€” must not request Android exts; on Windows keep ec=0). Probe presence first via `vkEnumerateDeviceExtensionProperties`; if the AHB ext is absent (memory says present on the razr Adreno, but fault-close anyway), skip residency and keep the current transient path (inv-9).

EDIT 4b â€” Add the three AHB-import entry points (resolved via vkGetDeviceProcAddr â€” they are extension functions, NOT in the flat vulkan-1 export table):
  - Add `[DllImport(VK)] static extern IntPtr vkGetDeviceProcAddr(IntPtr dev, [MarshalAs(UnmanagedType.LPStr)] string name);`
  - Resolve at init (store as static delegates via Marshal.GetDelegateForFunctionPointer, same idiom as DirectPortVk.cs lines 163-174):
      `vkGetAndroidHardwareBufferPropertiesANDROID(IntPtr dev, IntPtr buffer, ref VkAndroidHardwareBufferPropertiesANDROID props)`
  - Structs to add (StructLayout Sequential), with the real sType values:
      `VkAndroidHardwareBufferPropertiesANDROID { int sType=1000129003; IntPtr pNext; ulong allocationSize; uint memoryTypeBits; }`
      `VkImportAndroidHardwareBufferInfoANDROID { int sType=1000129002; IntPtr pNext; IntPtr buffer; }`  // buffer = the AHB handle
      `VkMemoryDedicatedAllocateInfo { int sType=1000127001; IntPtr pNext; IntPtr image; IntPtr buffer; }`  // dedicated alloc REQUIRED for AHB import; buffer = the VkBuffer
      `VkExternalMemoryBufferCreateInfo { int sType=1000072004; IntPtr pNext; uint handleTypes; }`  // handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_ANDROID_HARDWARE_BUFFER_BIT_ANDROID = 0x400

EDIT 4c â€” Add `ImportAhbBuffer(IntPtr ahb, ulong byteLen, out IntPtr buf, out IntPtr mem)`:
  1. `vkGetAndroidHardwareBufferPropertiesANDROID(s_dev, ahb, ref props)` -> props.allocationSize, props.memoryTypeBits.
  2. Create VkBuffer with BufCI.pNext -> VkExternalMemoryBufferCreateInfo{handleTypes=0x400}, usage=STORAGE_BUFFER(0x20), size = byteLen. (vkCreateBuffer â€” existing import.)
  3. Allocate memory: MemAI.size = props.allocationSize, typeIdx = first bit set in props.memoryTypeBits (NOT FindMem â€” AHB dictates the memory type). MemAI.pNext must chain BOTH: VkImportAndroidHardwareBufferInfoANDROID{buffer=ahb} AND VkMemoryDedicatedAllocateInfo{buffer=<the VkBuffer from step 2>}. Chain order: MemAI.pNext -> dedicated -> import.
     PROBLEM: current MemAI struct has `pNext` (IntPtr) but MkBuf never sets it; add a MemAI2 alloc that pins the pNext chain. (The existing vkAllocateMemory import takes `ref MemAI` â€” extend MemAI to be reused, or add a parallel struct; either compiles.)
  4. `vkBindBufferMemory(s_dev, buf, mem, 0)`.
  Fault: any non-zero VkResult -> throw; caller (GemmQ4) catches -> transient upload path (today's code) -> _gpuQ4Dead latches upstream (inv-9).

EDIT 4d â€” Add the residency cache + a resident GemmQ4 path (mirror GpuD3D12.s_q4Cache shape but SS015-compliant: an INSTANCE-scoped store, not a static Dictionary keyed globally).
  SS015 tension: GpuD3D12 uses `static readonly Dictionary<long,ResidentQ4> s_q4Cache` (line 92) â€” that already exists and presumably passed the baseline, so a symmetric `static readonly Dictionary<long, ResidentBq>` in GpuVulkan is baseline-consistent (readonly field holding a mutable collection is what D3D12 ships; SS015 flags reassignable static fields, and `static readonly` container is the established pattern here). Confirm against SS-BASELINE before landing; if the analyzer flags NEW static state, fall back to tying the cache to a per-Dp/per-decoder instance. Cache entry: `struct ResidentBq { IntPtr Buf, Mem; ulong Bytes; }` keyed by weightKey.
  New signature: `GemmQ4(float[] A, byte[] Bq, float[] Scales, byte[] Zp, float[] C, uint M, uint N, uint K, uint blockSize, bool hasZp, byte[] spv, IntPtr ahbBq = default, long weightKey = -1)`.
  Body change (lines 249-256, 297-298):
    - If `ahbBq != 0 && weightKey > 0`: look up cache. Miss -> `ImportAhbBuffer(ahbBq, (ulong)Bq-geom-len, out bqBuf, out bqMem)` and store; hit -> reuse cached (bqBuf, bqMem). Either way DO NOT MkBuf+vkMapMemory+Marshal.Copy the Bq bytes (the current lines 250 `MkBuf(bqB,...)` and 254 `Marshal.Copy(Bq,...)` are SKIPPED for the AHB weight). Because Bq may arrive as Array.Empty (resident seam), derive bqB from geometry: rowBytes*N passed via... note K/N/blockSize give it: bqB = (ulong)(K/blockSize)*(blockSize/2)*N. Use that, not Bq.Length.
    - A, Scales, Zp, C keep their transient MkBuf+map every call (A/C are per-token live data; Scales/Zp are small).
    - At cleanup (lines 296-300): DO NOT destroy the AHB-imported bqBuf/bqMem (they're cached/resident); only destroy the transient A/Scales/Zp/C buffers. The imported VkBuffer+VkDeviceMemory live until the weight's AHB is released (owner Terminate -> Vom Reclaim -> AhbNative.Free); Vulkan-side teardown of cached buffers happens on a device-lost/shutdown, out of the per-call path.
  - Add `public static bool QueryResidentQ4(long weightKey) => weightKey > 0 && s_bqCache.ContainsKey(weightKey);` symmetric to GpuD3D12 line 96.
  NOTE on descriptor binding: the imported bqBuf binds at set index 1 (Bq) exactly as today (line 268 `w[1]...pBuf = Pin(ib)` with `ib.buffer = bqBuf`) â€” no shader/SPIR-V change, the gemm_q4.spv contract is identical. Bit-parity is preserved because only the SOURCE of the same bytes changed, not the layout or the kernel.

=== INVARIANT LEDGER ===
- SS018 (no async in synchronous core): all edits synchronous. PASS.
- SS015 (no static mutable state): the Vulkan residency cache mirrors GpuD3D12's existing `static readonly Dictionary`; verify no NEW baseline violation via `ss build apk` (fail-closed vs SS-BASELINE.txt). If flagged, tie cache to a per-Dp instance. FLAGGED FOR THE HUMAN.
- SS007 (no empty catch): the FromProto AHB-alloc catch logs + degrades. PASS.
- SS012 (banned suffixes): new type names ResidentBq / no Impl/Helper/Manager. PASS.
- Inv-9 (presence->absence degrades, never a mode switch): three rungs, each fault flows to the next â€” AHB alloc fail -> plain VOM region (CPU rung intact); AHB ext absent / import fail -> transient upload (today's path); any GPU fault -> _gpuQ4Dead -> CPU SIMD. No boolean "GPU mode" toggle; the seam always attempts GPU and latches down on fault. PASS.
- Inv-3 (handle=authority, free-at-zero): AHB lifetime is the Vom.RegisterNative Reclaim, fired at the weight owner's refcount-zero (Terminate cascade). The Vulkan-imported VkBuffer/VkDeviceMemory is subordinate â€” it must be destroyed before/at that reclaim; simplest correct rule: import buffers are torn down on device shutdown (weights outlive individual GEMMs and are freed with the decoder). PASS with the caveat below.
- One namespace / Cm projects: unaffected (no registry writes).

=== BIT-PARITY / CORRECTNESS ARGUMENT ===
Only the byte SOURCE changes (CPU-mapped AHB vs a fresh vkMapMemory'd staging buffer holding a Marshal.Copy of the same bytes). The q4 layout, the descriptor set (5 storage buffers), gemm_q4.spv, push constants {M,N,K,blockSize,hasZp}, and dispatch geometry are untouched. On UMA the AHB import and the CPU map alias the same physical pages, so the GPU reads exactly what the CPU wrote once. Expected: bit-for-bit identical to the current transient path (which is already parity-verified vs CPU per the ground truth).

---

## Designed the file-level diff-plan for a scrcpy-shaped screen mirror over the existing AOA/DirectPort courier. Ground-truth read of all 12 files corrected two recon assumptions: (1) DpWinUsb.ReceiveLoop is ALREADY BGRA-correct â€” it reads the same 20-byte descriptor at offset 16 and DMAs `frameBytes` straight into DirectPortProducer.Scratch; the "YUV" in its comment is stale. No parallel ReceiveLoopAoa is needed. (2) The real deviceâ†’host gap is on the PHONE: DpAoaDevice has a SendFrame API but no capture pump feeding it â€” MediaProjection currently only pipes JPEG to the WebSocket (ImageAvailableListenerâ†’BroadcastRdpFrame), never to AOA. The Windows-side gap is that DpWinUsb.Open has ZERO callers (dead-drafted) and needs an `ss` verb to drive it. Control-inject reuses the SAME 20-byte descriptor (repurposing the x/y/w/h middle 12 bytes as opcode+coords) so both directions stay byte-compatible; tap decode scales wire coords â†’ absolute screen pixels for TerminalAccessibilityService.DispatchTap (which takes absolute px, NOT normalized). Verbs "Stream"/"Capture" are already in the closed vocabulary (SystemCatalog.json triage verbs + path roots), so no analyzer waiver is needed. Device-in-the-loop: the managed pump/control/registry/verb code is all draftable now; only the on-hardware AOA enumeration + tap-latency close-out needs a human.

Files: S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\Device\Android\DpAoa.cs, S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\Device\Android\DpAoaScreenCapture.cs, S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\windows\DpWinUsb.cs, S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\windows\DirectPortProducer.cs, S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\MainActivity.cs, S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\Host\Registrar.cs, S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\Pwsh\Cmdlets\ScreenStreamCmdlets.cs, S:\subsystem\.claude\worktrees\exciting-dijkstra-0dc3c0\src\runspace\Device\Android\Actuators.cs

EXACT FILE-LEVEL DIFF-PLAN. One 20-byte descriptor carries BOTH directions; the boundary is the only variable (thesis). Latest-wins already holds on every rung read below.

=== A. WIRE CONTRACT â€” src/runspace/Device/Android/DpAoa.cs (edit the static DpAoa class) ===
The descriptor is [fence:8][x:2][y:2][w:2][h:2][size:4]=20B. For deviceâ†’host FRAMES it stays exactly as-is (WriteDescriptor already correct). For hostâ†’device CONTROL, repurpose the SAME 20 bytes so DpAoaDevice.ReceiveLoop needs no new parser:
  - ADD `public const ushort ControlMagicW = 0xC7;` written into the `w` field (offset 14) to mark a control descriptor (a real frame's w is a pixel width, never 0xC7-with-h=0 â€” but robustly: gate on size==0 too, so a control descriptor is {size(off16)=0, w(off14)=ControlMagicW}). This keeps FRAME vs CONTROL unambiguous without widening the struct.
  - ADD `public const byte ControlVersion = 1;` packed into the high byte of the `h` field (offset 16-adjacent) â€” leaves room for future opcodes (invariantRisks item 8).
  - ADD opcode constants: `public const ushort CmdTap = 0x0001;` (extensible: CmdSwipe/CmdKey later).
  - ADD `internal static void WriteControlTap(Span<byte> desc, ulong seq, ushort xNorm, ushort yNorm)`: fenceâ†seq(off0), x(off8)â†CmdTap, y(off10)â†xNorm, w(off12)... â€” REVISED PACKING to fit 20B cleanly: [fence/seq:8][cmd:2 @8][argA:2 @10][argB:2 @12][ControlMagicW:2 @14][size=0:4 @16]. Tap uses argA=xNorm, argB=yNorm in 0..65535 normalized device coords (resolution-independent â€” the host does not know the phone's pixel dims).
  - ADD `internal static bool TryReadControl(ReadOnlySpan<byte> desc, out ushort cmd, out ushort argA, out ushort argB)`: returns true iff size(off16)==0 && w(off14)==ControlMagicW; reads cmd/argA/argB. Version-guards on the ControlVersion byte, returns false + (caller logs Dg.Warn) on mismatch (degrade, never throw â€” invariant 9).
WHY normalized 0..65535: the Windows host repurposes DpWinUsb.Send for taps but only knows the surface it created (width/height it passed to Open). Sending normalized lets the phone scale to its OWN RootInActiveWindow bounds. NO async â€” pure Span/BitConverter, SS018-safe.

=== B. PHONE CAPTURE PUMP â€” NEW FILE src/runspace/Device/Android/DpAoaScreenCapture.cs ===
This is the missing deviceâ†’host source. Class `DpAoaScreenCapture : IDisposable` (no banned suffix; "Capture" is a valid path root + noun). Namespace Subsystem.Device.
  - Ctor takes the live `DpAoaDevice` (or DpAoaHost â€” both expose identical SendFrame(fence,x,y,w,h,byte[])) via an interface OR just an `Action<ulong,int,int,int,int,byte[]> sendFrame` delegate to avoid a new coupling type (keeps the SS014 DAG clean â€” Deviceâ†’Device only).
  - `Open(Context ctx, MediaProjection projection, Action<...> sendFrame, out string? error)`: build an ImageReader at DisplayMetrics WÃ—H with format RGBA_8888 (=1, matching MainActivity.SetupVirtualDisplay line 604) â€” NOT YUV (DpAoa payload is BGRA; RGBA_8888 plane â†’ swap R/B or upload as-is since DirectPortProducer stamps DXGI B8G8R8A8; verify channel order on-device, note as open question). CreateVirtualDisplay("ss-aoa-mirror", ..., AUTO_MIRROR flag 16, imageReader.Surface). Own a Vom owner `\\Device\\Usb\\Capture` and Vom.Register the projection+reader+vd as a managed handle with an onReclaim that Release()s all three (cascade reclaim â€” invariantRisks item, Vom.Register signature confirmed at Vom.cs:72).
  - SetOnImageAvailableListener â†’ on each image: AcquireLatestImage (LATEST-WINS: drop backlog, drop stale â€” same pattern as MainActivity line 915), read plane[0], pack tightly to width*4 BGRA (strip rowStride padding: rowPadding = rowStride - pixelStride*width, copy row-by-row), monotonic `fence++`, call sendFrame(fence, 0,0,w,h, bgra). If a send is still in flight, DROP (Interlocked gate like ProjectionServer._isSendingScreen line 599) â€” never queue.
  - Stop()/Dispose(): Interlocked _stopped, Vom.Terminate(owner) â†’ cascade Release. Re-entrant (matches DpAoaDevice.Stop pattern).
NO static mutable state (SS015): instance-scoped, resolved via MainActivity.Instance like the other drivers.

=== C. WINDOWS DRIVER SEAM â€” src/runspace/windows/DpWinUsb.cs (2 small edits) ===
  1. FIX STALE COMMENT ONLY on ReceiveLoop (lines 131-132): the loop is ALREADY BGRA-correct (reads size@16, DMAs frameBytes into Scratch@line152). Change "full YUV frame" â†’ "full BGRA frame" and "Latest-wins: prior frame overwritten in place" stays. NO logic change â€” this path already matches DpAoaDevice.SendFrame byte-for-byte. This is the single biggest recon correction: the buildPlan's ReceiveLoopAoa is redundant.
  2. ADD `public void SendTap(ushort xNorm, ushort yNorm)`: builds a 20-byte control descriptor via DpAoa-parity constants (mirror the Android DpAoa.WriteControlTap packing here in the Windows const block â€” the two files already duplicate the AOA request codes deliberately, lines 37-44), then calls the existing `Send(ReadOnlySpan<byte>)` (line 183, writes bulkOut). `_frame`/`_fence` seq reused as the monotonic tap seq. This is the ONLY new hostâ†’device method; Send() itself is unchanged.

=== D. PHONE CONTROL SINK â€” src/runspace/Device/Android/DpAoa.cs (DpAoaDevice.ReceiveLoop + DpAoaHost.ReceiveLoop) ===
Both ReceiveLoops today read desc(20) then a payload of `frameBytes` and fire OnFrameReceived. EDIT: after reading the descriptor, FIRST call DpAoa.TryReadControl(desc,...):
  - if it IS a control descriptor (size==0, magic set): dispatch WITHOUT reading a payload â€” for CmdTap, scale argA/argB (0..65535) to screen px using the service's current bounds, then Subsystem.Device.Input.InvokeTap(x,y) (Actuators.cs:117 â†’ TerminalAccessibilityService.DispatchTap, confirmed absolute-pixel gesture at line 147-155). Version-mismatch â†’ Dg.Warn + continue (degrade).
  - else (real frame): existing path unchanged (read payload, OnFrameReceived).
Scaling source: query TerminalAccessibilityService.Instance for RootInActiveWindow bounds OR use DisplayMetrics (add a small `Input.InvokeTapNormalized(ushort,ushort)` in Actuators.cs that does the DisplayMetrics scale in ONE place â€” cleaner than duplicating in both ReceiveLoops). See item G.
The existing `frameBytes==0 â†’ continue` guard (DpAoaDevice line 124, DpAoaHost line 274) currently DROPS a control descriptor silently â€” so the TryReadControl check MUST come BEFORE that guard.

=== E. HOST WIRING â€” src/runspace/MainActivity.cs (HandleUsbIntent, ~line 366) ===
When DpAoaDevice.Open succeeds (line 371), if screen-mirror is desired, ALSO stand up the capture pump. But MediaProjection needs the user consent Intent (StartScreenCapture line 578 â†’ OnActivityResult line 583). So: DO NOT auto-start capture on attach. Instead expose `MainActivity.StartAoaMirror()` that (a) ensures _mediaProjection via the existing consent flow, (b) constructs DpAoaScreenCapture with `_aoaDevice.SendFrame` as the delegate, (c) stores it in a new instance field `_aoaCapture` (nullable, instance â€” no static). Teardown in HandleUsbIntent's `_aoaDevice?.Stop()` path must also `_aoaCapture?.Stop()` (cascade). The reverse control path needs NO MainActivity change â€” DpAoaDevice.OnFrameReceived/TryReadControl already routes taps to Input.InvokeTap.

=== F. REGISTRY CAPABILITY GATE â€” src/runspace/Host/Registrar.cs (SeedFromAssets, add near the Consent block ~line 538) ===
Add TWO seed-if-absent records (mirror the existing Consent(...) helper shape, Integrity User, enabled=false OPT-IN):
  - `\Capability\Consent\ScreenMirror` (consentKind "capability"): "Mirror this screen to a USB-attached host and accept remote taps." Gated exactly like ScreenCapture consent (line 538). MainActivity.StartAoaMirror checks Cm.Get(this path)?.Enabled before capturing (fail-closed; Cm is the one truth â€” invariantRisks item 3).
  - `\Capability\Surface\ScreenMirror` (Type "Mount", the DirectPort surface descriptor): path/owner/integrity live in Cm, NOT hardcoded. DirectPortProducer.Create already takes a `capPath` and projects SDDL from Cm.Get(capPath).Integrity (DirectPortProducer.cs:57) â€” so DpWinUsb.Open's caller passes this capPath. Note: "Surface" is on the AOSP platformNames collision list (SystemCatalog line 85) but that guard is for TYPE DECLARATIONS, not Cm path segments or record Type="Mount" â€” a registry path string is data, not a C# symbol, so SS011 does not fire. (Confirmed: ProjectionServer already uses \\Surfaces and DirectPortProducer registers "DirectPortSurface" as a VOM leaf name today.)

=== G. CONTROL SCALE HELPER â€” src/runspace/Device/Android/Actuators.cs (Input class, add one method) ===
ADD `public static string InvokeTapNormalized(ushort xNorm, ushort yNorm)`: resolve DisplayMetrics via MainActivity.Instance (same pattern as Haptics/Torch), scale x = xNorm/65535f*WidthPixels, y = yNorm/65535f*HeightPixels, then delegate to InvokeTap(x,y). Single source of the scale math; both DpAoaDevice and DpAoaHost ReceiveLoops call THIS. Keeps SS014 clean (Deviceâ†’Device).

=== H. WINDOWS DRIVER ENTRYPOINT â€” the `ss` verb (windows head) ===
DpWinUsb.Open has NO caller today. It needs an `ss` command to drive it (device-in-the-loop start/stop). The cleanest existing seam is a Windows-head cmdlet family, but ScreenStreamCmdlets.cs is the ANDROID head (MediaCodec/ImageReader â€” won't compile on the Windows head). So the Windows driver verb belongs beside the other windows/ drivers. PLAN: add a small `Start-ScreenMirror` / `Stop-ScreenMirror` entry in the Windows command surface (the same layer that would call ScreenStreamReceiver today) that: resolves serial, calls DpWinUsb.Open(serial, w, h, "\\Capability\\Surface\\ScreenMirror", out err) (verb "Start" is in the pwsh bucket; "Stream"/"Mirror" nouns are fine). On tap from the host UI (a Windows DirectPort consumer's click, normalized to 0..65535), call the returned DpWinUsb.SendTap. Stop â†’ DpWinUsb.Stop() (cascades _producer.Stop â†’ Vom.Terminate, lines 194-201, already correct). MARK: the exact Windows cmdlet host file is the one open item (see openQuestions) â€” the recon file list did not include the windows-head cmdlet registrar; DpWinUsb.Open's signature and lifecycle are the load-bearing part and are fully specified.

=== INVARIANTS RE-CHECKED AGAINST REAL CODE ===
(1) one namespace: DpAoaScreenCapture owns \\Device\\Usb\\Capture; DpWinUsb owns \\Device\\Usb\\{serial}; DirectPortProducer owns \\Surfaces â€” no collision (Vom.CreateOwner is GetOrAdd, idempotent). (2) handle=authority: capture reader/vd/projection Register'd with onReclaim Release; DpWinUsb._producer.Stop cascades. (3) Cm projects: surface path + enable live ONLY in the two new Cm records; code reads Cm.Get before capturing/serving. (5) verbs: Start/Stop/Send (pwsh bucket), Stream/Query (triage) â€” all present in SystemCatalog.json; no new verb minted. SS018: every loop is a Vom.Spawn thread with blocking IO (WinUsb_ReadPipe / FileInputStream.Read / BulkTransfer) + Interlocked/Volatile â€” zero async in the sync path. SS014: DpWinUsbâ†’DirectPortProducer+Vom (windows+core), DpAoaScreenCaptureâ†’DpAoaDevice+Vom+Input (Device+core) â€” no Deviceâ†’Dpx edge. SS015: all new state instance-scoped. Latest-wins: AcquireLatestImage (drop backlog) + Interlocked send-gate on phone; BlitLoop fence-compare drop on host (DpWinUsb.cs:172-175) â€” both verified present.

---


