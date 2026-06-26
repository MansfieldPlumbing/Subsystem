// src/depth_trt.cpp
// Compiles to: depth_trt.dll
// Purpose: TensorRT inference bridge for Depth Anything V2.
//          Single-graph, fixed 518x518, FP32 I/O.
//          Designed for streaming use — Init once, Infer per frame, Destroy at end.
//
// IO Contract:
//   Input:  "image"  [1, 3, 518, 518]  FP32  RGB normalized [0,1]  CHW
//   Output: "depth"  [1, 1, 518, 518]  FP32  relative depth, raw model output
//
// Normalization:
//   Depth Anything V2 expects ImageNet normalization:
//   R = (r - 0.485) / 0.229
//   G = (g - 0.456) / 0.224
//   B = (b - 0.406) / 0.225
//   Applied in the bridge so the caller just passes raw [0,1] RGB.
//
// Output:
//   Raw depth values — caller decides how to normalize for display.
//   For SD-compatible grayscale: normalize to [0,1] then scale to [0,255].
//   Near = bright (high value), far = dark (low value) — standard depth convention.

#include <iostream>
#include <fstream>
#include <vector>
#include <algorithm>
#include <NvInfer.h>
#include <cuda_runtime_api.h>

using namespace nvinfer1;

// ---------------------------------------------------------------------------
// CONSTANTS
// ---------------------------------------------------------------------------
static constexpr int   MODEL_H   = 518;
static constexpr int   MODEL_W   = 518;
static constexpr int   IN_ELEMS  = 3 * MODEL_H * MODEL_W;
static constexpr int   OUT_ELEMS = 1 * MODEL_H * MODEL_W;

// ImageNet normalization constants
static constexpr float MEAN_R = 0.485f, MEAN_G = 0.456f, MEAN_B = 0.406f;
static constexpr float STD_R  = 0.229f, STD_G  = 0.224f, STD_B  = 0.225f;

// ---------------------------------------------------------------------------
// LOGGER
// ---------------------------------------------------------------------------
class Logger : public ILogger {
    void log(Severity s, const char* msg) noexcept override {
        if (s <= Severity::kWARNING)
            std::cout << "[TRT] " << msg << std::endl;
    }
} gLogger;

// ---------------------------------------------------------------------------
// CONTEXT
// ---------------------------------------------------------------------------
struct DepthCtx {
    IRuntime*          runtime  = nullptr;
    ICudaEngine*       engine   = nullptr;
    IExecutionContext* context  = nullptr;
    void*              d_input  = nullptr;   // GPU: [1,3,518,518] FP32
    void*              d_output = nullptr;   // GPU: [1,1,518,518] FP32
    float*             h_input  = nullptr;   // CPU pinned: normalized CHW
    float*             h_output = nullptr;   // CPU pinned: raw depth
    cudaStream_t       stream   = nullptr;
};

// ---------------------------------------------------------------------------
// CUDA ERROR CHECK HELPER
// Returns nullptr from the enclosing function on failure.
// ---------------------------------------------------------------------------
#define CUDA_CHECK(call)                                                      \
    do {                                                                      \
        cudaError_t _e = (call);                                              \
        if (_e != cudaSuccess) {                                              \
            std::cout << "[Depth] CUDA error at " << __FILE__                \
                      << ":" << __LINE__ << " - "                            \
                      << cudaGetErrorString(_e) << std::endl;                \
            delete ctx; return nullptr;                                       \
        }                                                                     \
    } while (0)

// ---------------------------------------------------------------------------
// INIT
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void* Depth_Init(const char* enginePath)
{
    auto* ctx = new DepthCtx();

    // Load engine file
    std::ifstream f(enginePath, std::ios::binary);
    if (!f.is_open()) {
        std::cout << "[Depth] ERROR: Cannot open engine: " << enginePath << std::endl;
        delete ctx; return nullptr;
    }
    std::vector<char> data((std::istreambuf_iterator<char>(f)), {});

    ctx->runtime = createInferRuntime(gLogger);
    if (!ctx->runtime) { delete ctx; return nullptr; }

    ctx->engine = ctx->runtime->deserializeCudaEngine(data.data(), data.size());
    if (!ctx->engine) {
        std::cout << "[Depth] ERROR: Failed to deserialize engine." << std::endl;
        delete ctx; return nullptr;
    }

    ctx->context = ctx->engine->createExecutionContext();
    if (!ctx->context) { delete ctx; return nullptr; }

    // Discover tensor names - print all of them, then match by position/name
    int nTensors = ctx->engine->getNbIOTensors();
    std::string inputName, outputName;

    std::cout << "[Depth] Engine tensors (" << nTensors << "):" << std::endl;
    for (int i = 0; i < nTensors; i++) {
        std::string name = ctx->engine->getIOTensorName(i);
        auto mode = ctx->engine->getTensorIOMode(name.c_str());
        bool isInput = (mode == TensorIOMode::kINPUT);
        std::cout << "  [" << i << "] " << (isInput ? "INPUT " : "OUTPUT") << "  \"" << name << "\"" << std::endl;
        if (isInput  && inputName.empty())  inputName  = name;
        if (!isInput && outputName.empty()) outputName = name;
        // Also accept exact legacy names
        if (name == "image") inputName  = name;
        if (name == "depth") outputName = name;
    }

    if (inputName.empty() || outputName.empty()) {
        std::cout << "[Depth] ERROR: Could not identify input/output tensors." << std::endl;
        delete ctx; return nullptr;
    }

    std::cout << "[Depth] Using input=\"" << inputName << "\"  output=\"" << outputName << "\"" << std::endl;

    // Allocate GPU buffers - every CUDA call is checked
    CUDA_CHECK(cudaMalloc(&ctx->d_input,  IN_ELEMS  * sizeof(float)));
    CUDA_CHECK(cudaMalloc(&ctx->d_output, OUT_ELEMS * sizeof(float)));
    CUDA_CHECK(cudaMallocHost((void**)&ctx->h_input,  IN_ELEMS  * sizeof(float)));
    CUDA_CHECK(cudaMallocHost((void**)&ctx->h_output, OUT_ELEMS * sizeof(float)));
    CUDA_CHECK(cudaStreamCreate(&ctx->stream));

    // Bind tensor addresses using discovered names
    ctx->context->setTensorAddress(inputName.c_str(),  ctx->d_input);
    ctx->context->setTensorAddress(outputName.c_str(), ctx->d_output);

    std::cout << "[Depth] Engine loaded. Input: [1,3,518,518]  Output: [1,1,518,518]" << std::endl;
    return ctx;
}

// ---------------------------------------------------------------------------
// INFER
// Input:  rgbChw518 - float[3 * 518 * 518], CHW, RGB, values [0,1]
// Output: depthOut  - float[518 * 518], raw depth values
// Returns 0 on success, non-zero on error.
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
int Depth_Infer(void* hCtx, float* rgbChw518, float* depthOut)
{
    auto* ctx = (DepthCtx*)hCtx;
    if (!ctx) return -1;

    // Apply ImageNet normalization into pinned host buffer
    int planeSize = MODEL_H * MODEL_W;
    float* rPlane = rgbChw518;
    float* gPlane = rgbChw518 + planeSize;
    float* bPlane = rgbChw518 + planeSize * 2;

    float* dstR = ctx->h_input;
    float* dstG = ctx->h_input + planeSize;
    float* dstB = ctx->h_input + planeSize * 2;

    for (int i = 0; i < planeSize; i++) {
        dstR[i] = (rPlane[i] - MEAN_R) / STD_R;
        dstG[i] = (gPlane[i] - MEAN_G) / STD_G;
        dstB[i] = (bPlane[i] - MEAN_B) / STD_B;
    }

    // H2D async
    cudaMemcpyAsync(ctx->d_input, ctx->h_input,
                    IN_ELEMS * sizeof(float),
                    cudaMemcpyHostToDevice, ctx->stream);

    // Inference
    if (!ctx->context->enqueueV3(ctx->stream)) return 1;

    // D2H async
    cudaMemcpyAsync(ctx->h_output, ctx->d_output,
                    OUT_ELEMS * sizeof(float),
                    cudaMemcpyDeviceToHost, ctx->stream);

    cudaStreamSynchronize(ctx->stream);

    memcpy(depthOut, ctx->h_output, OUT_ELEMS * sizeof(float));
    return 0;
}

// ---------------------------------------------------------------------------
// NORMALIZE DEPTH  (call after Depth_Infer)
// Normalizes raw depth map to [0,1] across the frame.
// invert=1 -> near=1.0 (bright), far=0.0  (SD convention, white=close)
// invert=0 -> near=0.0 (dark),   far=1.0  (disparity convention)
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void Depth_Normalize(float* depth, int count, int invert)
{
    float minV = depth[0], maxV = depth[0];
    for (int i = 1; i < count; i++) {
        if (depth[i] < minV) minV = depth[i];
        if (depth[i] > maxV) maxV = depth[i];
    }
    float range = maxV - minV;
    if (range < 1e-6f) range = 1e-6f;

    for (int i = 0; i < count; i++) {
        float v = (depth[i] - minV) / range;
        depth[i] = invert ? (1.0f - v) : v;
    }
}

// ---------------------------------------------------------------------------
// DESTROY
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
void Depth_Destroy(void* hCtx)
{
    auto* ctx = (DepthCtx*)hCtx;
    if (!ctx) return;
    if (ctx->d_input)  cudaFree(ctx->d_input);
    if (ctx->d_output) cudaFree(ctx->d_output);
    if (ctx->h_input)  cudaFreeHost(ctx->h_input);
    if (ctx->h_output) cudaFreeHost(ctx->h_output);
    if (ctx->stream)   cudaStreamDestroy(ctx->stream);
    delete ctx->context;
    delete ctx->engine;
    delete ctx->runtime;
    delete ctx;
}
