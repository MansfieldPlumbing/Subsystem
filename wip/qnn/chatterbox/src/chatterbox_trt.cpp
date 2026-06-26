// chatterbox_trt.cpp
// Compiles to: chatterbox_trt.dll
// Purpose: Flattens TensorRT C++ into a simple C API for C# P/Invoke
// Input:   speech_tokens  [1, seq]        int64
// Output:  audio_waveform [1, samples]    float32  (24000 Hz)
//
// Key differences from demucs_v4_trt.cpp:
//   1. Input dtype is int64 (token ids), not float
//   2. Input shape is dynamic — set before every enqueue
//   3. Output length is dynamic — query after setting input shape
//   4. No numSources / chunkLen fixed geometry — caller owns the lengths

#include <iostream>
#include <vector>
#include <memory>
#include <cstring>
#include <NvInfer.h>
#include <cuda_runtime_api.h>

using namespace nvinfer1;

// ---------------------------------------------------------------------------
// Logger
// ---------------------------------------------------------------------------
class Logger : public ILogger {
    void log(Severity severity, const char* msg) noexcept override {
        if (severity <= Severity::kWARNING)
            std::cout << "[TRT] " << msg << std::endl;
    }
} gLogger;

// ---------------------------------------------------------------------------
// Context
// ---------------------------------------------------------------------------
struct TrtContext {
    std::unique_ptr<IRuntime>          runtime;
    std::unique_ptr<ICudaEngine>       engine;
    std::unique_ptr<IExecutionContext> context;

    void*        d_input       = nullptr;   // int64  [1, seq]
    void*        d_output      = nullptr;   // float  [1, samples]
    cudaStream_t stream        = nullptr;

    int          maxSeqLen     = 0;         // max tokens we allocated for
    int          maxOutputLen  = 0;         // max samples we allocated for
};

// ---------------------------------------------------------------------------
// Trt_Init
//
// modelPath    — path to .trt engine file
// maxSeqLen    — maximum token sequence length to pre-allocate for
//                (4096 is safe for typical utterances, tune as needed)
// sampleRate   — written back to caller (always 24000 for chatterbox)
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void* Trt_Init(const char* modelPath, int maxSeqLen, int* sampleRate)
{
    auto* ctx = new TrtContext();
    ctx->maxSeqLen = maxSeqLen;

    // 1. Runtime
    ctx->runtime.reset(createInferRuntime(gLogger));
    if (!ctx->runtime) { delete ctx; return nullptr; }

    // 2. Load .trt file
    FILE* f = fopen(modelPath, "rb");
    if (!f) {
        std::cout << "[TRT] ERROR: Cannot open engine: " << modelPath << std::endl;
        delete ctx; return nullptr;
    }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    std::vector<char> modelData(size);
    fread(modelData.data(), 1, size, f);
    fclose(f);

    // 3. Deserialize engine
    ctx->engine.reset(ctx->runtime->deserializeCudaEngine(modelData.data(), size));
    if (!ctx->engine) {
        std::cout << "[TRT] ERROR: Deserialize failed — wrong TRT version?" << std::endl;
        delete ctx; return nullptr;
    }

    // 4. Execution context
    ctx->context.reset(ctx->engine->createExecutionContext());
    if (!ctx->context) { delete ctx; return nullptr; }

    // 5. Estimate worst-case output length.
    //    Chatterbox S3Gen: each token ≈ 256 samples at 24kHz.
    //    We over-allocate by 2x to be safe.
    ctx->maxOutputLen = maxSeqLen * 1024;

    // 6. Allocate GPU buffers
    //    Input:  int64  [1, maxSeqLen]
    //    Output: float  [1, maxOutputLen]
    cudaMalloc(&ctx->d_input,  maxSeqLen     * sizeof(int64_t));
    cudaMalloc(&ctx->d_output, ctx->maxOutputLen * sizeof(float));
    cudaStreamCreate(&ctx->stream);

    // 7. Bind addresses (TRT IExecutionContext v3 API)
    ctx->context->setTensorAddress("speech_tokens",  ctx->d_input);
    ctx->context->setTensorAddress("audio_waveform", ctx->d_output);

    *sampleRate = 24000;
    return ctx;
}

// ---------------------------------------------------------------------------
// Trt_Process
//
// h_tokens       — int64 host buffer, length = seqLen
// seqLen         — number of speech tokens
// h_output       — float host buffer, caller must be >= maxOutputLen floats
// outSamples     — written back: actual number of samples produced
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
int Trt_Process(void* hCtx,
                int64_t* h_tokens, int seqLen,
                float*   h_output, int* outSamples)
{
    auto* ctx = (TrtContext*)hCtx;
    if (!ctx) return -1;

    if (seqLen > ctx->maxSeqLen) {
        std::cout << "[TRT] ERROR: seqLen " << seqLen
                  << " exceeds maxSeqLen " << ctx->maxSeqLen << std::endl;
        return -2;
    }

    // 1. Tell TRT the actual input shape for this inference
    Dims2 inputDims{1, seqLen};
    if (!ctx->context->setInputShape("speech_tokens", inputDims)) {
        std::cout << "[TRT] ERROR: setInputShape failed" << std::endl;
        return 1;
    }

    // 2. Query how many output samples TRT will produce
    //    (output shape is dynamic — depends on seqLen via the ISTFT)
    auto outDims = ctx->context->getTensorShape("audio_waveform");
    int nSamples = (outDims.nbDims >= 2) ? outDims.d[1] : outDims.d[0];
    if (nSamples <= 0 || nSamples > ctx->maxOutputLen) {
        std::cout << "[TRT] ERROR: unexpected output size: " << nSamples << std::endl;
        return 2;
    }
    *outSamples = nSamples;

    // 3. H2D — tokens (int64)
    size_t inBytes  = seqLen   * sizeof(int64_t);
    size_t outBytes = nSamples * sizeof(float);

    if (cudaMemcpyAsync(ctx->d_input, h_tokens, inBytes,
                        cudaMemcpyHostToDevice, ctx->stream) != cudaSuccess) return 3;

    // 4. Inference
    if (!ctx->context->enqueueV3(ctx->stream)) return 4;

    // 5. D2H — waveform (float)
    if (cudaMemcpyAsync(h_output, ctx->d_output, outBytes,
                        cudaMemcpyDeviceToHost, ctx->stream) != cudaSuccess) return 5;

    cudaStreamSynchronize(ctx->stream);
    return 0;
}

// ---------------------------------------------------------------------------
// Trt_GetMaxOutputLen — lets C# pre-allocate the right output buffer
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
int Trt_GetMaxOutputLen(void* hCtx)
{
    auto* ctx = (TrtContext*)hCtx;
    return ctx ? ctx->maxOutputLen : 0;
}

// ---------------------------------------------------------------------------
// Trt_Destroy
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void Trt_Destroy(void* hCtx)
{
    auto* ctx = (TrtContext*)hCtx;
    if (ctx) {
        if (ctx->d_input)  cudaFree(ctx->d_input);
        if (ctx->d_output) cudaFree(ctx->d_output);
        if (ctx->stream)   cudaStreamDestroy(ctx->stream);
        delete ctx;
    }
}