// =============================================================================
// d3d12_dispatch_reference.h
// Extracted from ggml-vulkan.cpp — D3D12/DirectPort backend dispatch guide
// All push constants map directly to SetComputeRoot32BitConstants(0, N, &pc, 0)
// =============================================================================

// -----------------------------------------------------------------------------
// DISPATCH FORMULA (universal for all unary/binary/activation ops)
// From ggml-vulkan.cpp:6537 — wg_denoms[0] = 512 for all standard element ops
//   Dispatch( CEIL_DIV(ne, 512), 1, 1 )
// where ne = ggml_nelements(dst)
// Your HLSL shaders are compiled with [numthreads(512, 1, 1)]
// -----------------------------------------------------------------------------
#define CEIL_DIV(a, b) (((a) + (b) - 1) / (b))

inline uint32_t dispatch_x(const ggml_tensor* dst) {
    return CEIL_DIV((uint32_t)ggml_nelements(dst), 512u);
}

// For norm/rms_norm/group_norm: one workgroup per row
inline uint32_t dispatch_rows(const ggml_tensor* src0) {
    return (uint32_t)(ggml_nelements(src0) / src0->ne[0]);
}


// =============================================================================
// PUSH CONSTANT STRUCTS (verbatim from ggml-vulkan.cpp, renamed for D3D12)
// Use: cmdList->SetComputeRoot32BitConstants(0, sizeof(pc)/4, &pc, 0)
// =============================================================================

// --- Simple ops: SCALE, SQR, SQRT, SIN, COS, CLAMP, DIAG_MASK_INF -----------
struct PushConst_Simple {
    uint32_t KX;     // = (uint32_t)ggml_nelements(dst)
    uint32_t KY;     // = 0
    float    param1; // op-specific: scale factor, clamp min, n_past (cast), etc.
    float    param2; // op-specific: clamp max, etc.
    float    param3; // = 0
    float    param4; // = 0
};

// --- Unary ops: SILU, GELU, RELU, TANH, SIGMOID, ELU, EXP, LOG, NEG, etc. --
// Also: NORM, RMS_NORM, GROUP_NORM (per-row normalization ops)
// From ggml-vulkan.cpp:1127
struct PushConst_Unary {
    uint32_t ne;                                // total elements
    uint32_t ne00, ne01, ne02, ne03;            // src0 shape
    uint32_t nb00, nb01, nb02, nb03;            // src0 byte strides
    uint32_t ne10, ne11, ne12, ne13;            // dst shape
    uint32_t nb10, nb11, nb12, nb13;            // dst byte strides
    uint32_t misalign_offsets;                  // = 0 if buffers are aligned
    float    param1;                            // e.g. eps for norm ops
    float    param2;                            // = 0
    // fastdiv fields (can zero-initialize if not using fastdiv path)
    uint32_t ne0_012mp, ne0_012L;
    uint32_t ne0_01mp,  ne0_01L;
    uint32_t ne0_0mp,   ne0_0L;
    uint32_t ne1_012mp, ne1_012L;
    uint32_t ne1_01mp,  ne1_01L;
    uint32_t ne1_0mp,   ne1_0L;
};
// IMPORTANT: zero-initialize this struct, then fill ne/ne0x/ne1x fields.
// fastdiv fields are an optimization — zero them to use the fallback path.

// Helper to populate the basic fields (fastdiv left zeroed):
inline PushConst_Unary make_unary_pc(const ggml_tensor* src0, const ggml_tensor* dst, float param1 = 0.0f, float param2 = 0.0f) {
    PushConst_Unary pc = {};
    pc.ne   = (uint32_t)ggml_nelements(dst);
    pc.ne00 = (uint32_t)src0->ne[0]; pc.ne01 = (uint32_t)src0->ne[1];
    pc.ne02 = (uint32_t)src0->ne[2]; pc.ne03 = (uint32_t)src0->ne[3];
    pc.nb00 = (uint32_t)src0->nb[0]; pc.nb01 = (uint32_t)src0->nb[1];
    pc.nb02 = (uint32_t)src0->nb[2]; pc.nb03 = (uint32_t)src0->nb[3];
    pc.ne10 = (uint32_t)dst->ne[0];  pc.ne11 = (uint32_t)dst->ne[1];
    pc.ne12 = (uint32_t)dst->ne[2];  pc.ne13 = (uint32_t)dst->ne[3];
    pc.nb10 = (uint32_t)dst->nb[0];  pc.nb11 = (uint32_t)dst->nb[1];
    pc.nb12 = (uint32_t)dst->nb[2];  pc.nb13 = (uint32_t)dst->nb[3];
    pc.param1 = param1;
    pc.param2 = param2;
    return pc;
}

// --- Binary ops: ADD, SUB, MUL, DIV, ACC, SET --------------------------------
// From ggml-vulkan.cpp:1258
struct PushConst_Binary {
    uint32_t ne;
    uint32_t ne00, ne01, ne02, ne03, nb00, nb01, nb02, nb03; // src0
    uint32_t ne10, ne11, ne12, ne13, nb10, nb11, nb12, nb13; // src1
    uint32_t ne20, ne21, ne22, ne23, nb20, nb21, nb22, nb23; // dst
    uint32_t misalign_offsets;
    float    param1;   // = 0 for ADD/MUL/DIV; = alpha for ACC
    float    param2;   // = 0
    int32_t  param3;   // = 0
};
// Dispatch: CEIL_DIV(ggml_nelements(dst), 512), 1, 1

// --- GLU family: SWIGLU, GEGLU, REGLU, GEGLU_ERF, GEGLU_QUICK, SWIGLU_OAI --
// From ggml-vulkan.cpp:1108
struct PushConst_GLU {
    uint32_t N;     // = (uint32_t)(dst->ne[0] * dst->ne[1] * dst->ne[2] * dst->ne[3])
    uint32_t ne00;  // src0->ne[0]
    uint32_t ne20;  // dst->ne[0]
    uint32_t mode;  // 0=default, 1=swapped, 2=split
    float    alpha; // for SWIGLU_OAI only
    float    limit; // = 0
    uint32_t nb01, nb02, nb03;
    uint32_t ne01, ne02;
    uint32_t nb11, nb12, nb13;
    uint32_t ne11, ne12;
};
// Dispatch: CEIL_DIV(N, 512), 1, 1

// --- Soft-max ----------------------------------------------------------------
// From ggml-vulkan.cpp:1341
struct PushConst_SoftMax {
    uint32_t KX;          // src0->ne[0] (sequence length)
    uint32_t KY;          // src0->ne[1] (n_heads)
    uint32_t ne00, ne01, ne02;
    uint32_t ne12, ne13;
    uint32_t nb11, nb12, nb13;
    float    scale;
    float    max_bias;
    float    m0, m1;
    uint32_t n_head_log2;
    uint32_t nrows_x;
    uint32_t has_sinks;   // = 0 usually
};
// Dispatch: 1 workgroup per row → (ne01*ne02*ne03, 1, 1) with wg_size=1
// Actually: pipeline picks wg512 variant if ne[0]>1024, else normal
// Dispatch: (CEIL_DIV(KY, 1), 1, 1) — one thread group per head

// --- RoPE --------------------------------------------------------------------
// From ggml-vulkan.cpp:1308
// Use op_params from ggml_tensor: ((int32_t*)node->op_params)
struct PushConst_RoPE {
    uint32_t rope_mode;    // op_params[2]
    uint32_t nrows;        // src0->ne[1]*src0->ne[2]*src0->ne[3]
    uint32_t n_dims;       // op_params[1]
    float    freq_scale;   // op_params as float [4]
    float    freq_base;    // op_params as float [5]  (default 10000.0)
    float    ext_factor;   // op_params as float [6]
    float    attn_factor;  // op_params as float [7]
    float    corr_dims[2]; // derived from n_dims, n_ctx_orig, freq_base
    float    theta_scale;  // = powf(freq_base, -2.0f/n_dims)
    uint32_t has_ff;       // src2 != nullptr ? 1 : 0
    int32_t  sections[4];  // op_params[12..15], used for mrope
    uint32_t is_imrope;    // = 0 unless mode==VISION
    uint32_t is_back;      // = 1 for ROPE_BACK
    uint32_t set_rows_stride; // = 0
    uint32_t ne00, ne01, ne02;
    uint32_t nb01, nb02, nb03;
    uint32_t nb11, nb12, nb13;
};
// Dispatch: (ne01*ne02*ne03 / 2, 1, 1) — each thread handles 2 elements (complex pair)
// Note: for neox variant this is (CEIL_DIV(ne01*ne02*ne03, 1), 1, 1)

// --- Diag Mask Inf -----------------------------------------------------------
// From ggml-vulkan.cpp:1302
struct PushConst_DiagMask {
    uint32_t ncols;            // src0->ne[0]
    uint32_t rows_per_channel; // src0->ne[1]
    int32_t  n_past;           // ((int32_t*)node->op_params)[0]
};
// Dispatch: CEIL_DIV(ggml_nelements(dst), 512), 1, 1

// --- Count Experts (for MoE) -------------------------------------------------
// From ggml-vulkan.cpp:1100
struct PushConst_CountExperts {
    uint32_t ne00;
    uint32_t ne01;
    uint32_t nb00;
    uint32_t nb01;
    uint32_t a_offset; // = 0
};

// --- SSM Conv (Mamba) --------------------------------------------------------
// From ggml-vulkan.cpp:1494
struct PushConst_SSMConv {
    uint32_t d_inner;  // dst->ne[0]
    uint32_t n_seqs;   // src0->ne[2]
    uint32_t d_conv;   // src1->ne[0]
};
// Dispatch: (d_inner, n_seqs, 1)

// --- SSM Scan (Mamba) --------------------------------------------------------
// From ggml-vulkan.cpp:1487
struct PushConst_SSMScan {
    uint32_t d_state; // src1->ne[0]
    uint32_t d_inner; // src0->ne[0]
    uint32_t n_seq_tokens;
    uint32_t n_seqs;
};
// Dispatch uses wave ops — needs native HLSL rewrite with WaveActiveScan*

// --- Quantize Q8_1 -----------------------------------------------------------
// From ggml-vulkan.cpp:1594
struct PushConst_QuantQ8_1 {
    uint32_t ne;         // ggml_nelements(src0)
    uint32_t ne0_padded; // GGML_PAD(src0->ne[0], 256)
};

// --- Flash Attn Split-K Reduce -----------------------------------------------
// From ggml-vulkan.cpp:1599
struct PushConst_FAReduceK {
    uint32_t ne01;   // Q sequence length
    uint32_t ne02;   // n_heads
    uint32_t split_k;
    uint32_t k_num; // = ne11 (KV sequence length)
    uint32_t stride_q;
    uint32_t stride_kv;
};


// =============================================================================
// OP → SHADER + DISPATCH TABLE
// For your WorkerThread switch(node->op) dispatch loop
// =============================================================================

/*
GGML_OP_UNARY (check ggml_get_unary_op(node)):
    GGML_UNARY_OP_SILU        → silu.dxil       PushConst_Unary  Dispatch(CEIL_DIV(ne,512), 1, 1)
    GGML_UNARY_OP_GELU        → gelu.dxil
    GGML_UNARY_OP_GELU_ERF    → gelu_erf.dxil
    GGML_UNARY_OP_GELU_QUICK  → gelu_quick.dxil
    GGML_UNARY_OP_RELU        → relu.dxil
    GGML_UNARY_OP_TANH        → tanh.dxil
    GGML_UNARY_OP_SIGMOID     → sigmoid.dxil
    GGML_UNARY_OP_HARDSIGMOID → hardsigmoid.dxil
    GGML_UNARY_OP_HARDSWISH   → hardswish.dxil
    GGML_UNARY_OP_EXP         → exp.dxil
    GGML_UNARY_OP_NEG         → neg.dxil
    GGML_UNARY_OP_ABS         → abs.dxil
    GGML_UNARY_OP_SOFTPLUS    → softplus.dxil
    GGML_UNARY_OP_STEP        → step.dxil
    GGML_UNARY_OP_ROUND       → round.dxil
    GGML_UNARY_OP_CEIL        → ceil.dxil
    GGML_UNARY_OP_FLOOR       → floor.dxil
    GGML_UNARY_OP_TRUNC       → trunc.dxil
    GGML_UNARY_OP_SGN         → sgn.dxil
    GGML_UNARY_OP_XIELU       → xielu.dxil

GGML_OP_ADD / SUB / MUL / DIV / ACC
    → binary shader           PushConst_Binary  Dispatch(CEIL_DIV(ne,512), 1, 1)

GGML_OP_GLU (check ggml_get_glu_op(node)):
    GGML_GLU_OP_SWIGLU        → swiglu.dxil     PushConst_GLU   Dispatch(CEIL_DIV(N,512), 1, 1)
    GGML_GLU_OP_SWIGLU_OAI    → swiglu_oai.dxil
    GGML_GLU_OP_GEGLU         → geglu.dxil
    GGML_GLU_OP_GEGLU_ERF     → geglu_erf.dxil
    GGML_GLU_OP_GEGLU_QUICK   → geglu_quick.dxil
    GGML_GLU_OP_REGLU         → reglu.dxil

GGML_OP_NORM       → norm.dxil       PushConst_Unary  param1=eps   Dispatch(nrows, 1, 1)
GGML_OP_GROUP_NORM → group_norm.dxil PushConst_Unary  param1=eps   Dispatch(n_groups, 1, 1)
GGML_OP_RMS_NORM   → [use DML or native HLSL]

GGML_OP_LOG         → log.dxil
GGML_OP_SUM / MEAN  → [reduce — use DirectML Reduce]

GGML_OP_DIAG_MASK_INF → diag_mask_inf.dxil  PushConst_DiagMask  Dispatch(CEIL_DIV(ne,512),1,1)

GGML_OP_SOFT_MAX    → [prefer DirectML Softmax operator]

GGML_OP_ROPE        → [native HLSL — rope.dxil needs WaveGetLaneIndex fix first]

GGML_OP_MUL_MAT     → DirectML GEMM (DML_OPERATOR_GEMM)

GGML_OP_CPY / CONT / DUP
    → copy.dxil or contig_copy.dxil   PushConst_Unary  Dispatch(CEIL_DIV(ne,512),1,1)

GGML_OP_PAD        → pad.dxil
GGML_OP_ROLL       → roll.dxil
GGML_OP_REPEAT     → repeat.dxil
GGML_OP_DIAG       → diag.dxil
GGML_OP_UPSCALE    → upscale.dxil
GGML_OP_IM2COL     → [3D variant: im2col_3d.dxil]

GGML_OP_REGLU      → reglu.dxil
GGML_OP_GEGLU      → geglu.dxil

GGML_OP_SSM_CONV   → ssm_conv.dxil   PushConst_SSMConv   Dispatch(d_inner, n_seqs, 1)
GGML_OP_SSM_SCAN   → needs native Wave rewrite (subgroup ops)

GGML_OP_COUNT_EXPERTS → count_experts.dxil  PushConst_CountExperts
GGML_OP_TOP_K      → topk.dxil (needs Wave rewrite — uses subgroup shuffle)

// DEQUANT (for fp16 inference with quantized weights):
// Map these to your native-rewritten DXIL shaders
GGML_OP_GET_ROWS with GGML_TYPE_Q4_0 → dequant_q4_0.dxil (DONE)
GGML_OP_GET_ROWS with GGML_TYPE_Q4_1 → dequant_q4_1_native.dxil (TODO)
GGML_OP_GET_ROWS with GGML_TYPE_Q8_0 → dequant_q8_0_native.dxil (TODO)
GGML_OP_GET_ROWS with GGML_TYPE_Q5_0 → dequant_q5_0_native.dxil (TODO)
GGML_OP_GET_ROWS with GGML_TYPE_Q5_1 → dequant_q5_1_native.dxil (TODO)
// All dequant: Dispatch(CEIL_DIV(ne, 32), 1, 1) — 32 threads per block (QK=32)

// MATMUL DEQUANT path (mat-vec, quantized weights × fp32 activations):
// Two-stage: dequant → DirectML GEMM
// OR: use ggml-vulkan's mul_mat path logic — for now route to dequant+GEMM
*/


// =============================================================================
// ADAPTER SELECTION (skip P2000 at index 0, target V340 dies 1-4)
// =============================================================================
/*
IDXGIFactory4* factory = nullptr;
CreateDXGIFactory1(IID_PPV_ARGS(&factory));

// Die index mapping (from your DML probe results):
// DML device 0 → P2000 (SKIP)
// DML device 1 → V340 die 0
// DML device 2 → V340 die 1
// DML device 3 → V340 die 2
// DML device 4 → V340 die 3

int target_adapters[] = {1, 2, 3, 4};  // skip 0

for (int i = 0; i < 4; i++) {
    IDXGIAdapter1* adapter = nullptr;
    factory->EnumAdapters1(target_adapters[i], &adapter);

    ID3D12Device* device = nullptr;
    D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(&device));

    // Verify DML support
    DML_CREATE_DEVICE_FLAGS dmlFlags = DML_CREATE_DEVICE_FLAG_NONE;
    IDMLDevice* dmlDevice = nullptr;
    DMLCreateDevice(device, dmlFlags, IID_PPV_ARGS(&dmlDevice));

    // Store per-die: device, dmlDevice, commandQueue, commandAllocator
    // Pin worker thread: SetThreadAffinityMask(thread_handle, 1ull << (i + 2))
    // (skip core 0=OS, core 1=llama master, cores 2-5=die workers)
}
*/
