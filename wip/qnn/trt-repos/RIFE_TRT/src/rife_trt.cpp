#include <iostream>
#include <vector>
#include <memory>
#include <NvInfer.h>
#include <cuda_runtime_api.h>

using namespace nvinfer1;

class Logger : public ILogger {
    void log(Severity severity, const char* msg) noexcept override {
        if (severity <= Severity::kWARNING)
            std::cout << "[TRT] " << msg << std::endl;
    }
} gLogger;

struct RifeContext {
    std::unique_ptr<IRuntime>          runtime;
    std::unique_ptr<ICudaEngine>       engine;
    std::unique_ptr<IExecutionContext> context;
    void*        d_img0      = nullptr;
    void*        d_img1      = nullptr;
    void*        d_timestep  = nullptr;
    void*        d_output    = nullptr;
    cudaStream_t stream      = nullptr;
    int          H           = 0;
    int          W           = 0;
};

extern "C" __declspec(dllexport) void* Rife_Init(const char* enginePath, int height, int width) {
    auto* ctx = new RifeContext();
    ctx->H = height; ctx->W = width;
    ctx->runtime.reset(createInferRuntime(gLogger));
    if (!ctx->runtime) { delete ctx; return nullptr; }

    FILE* f = fopen(enginePath, "rb");
    if (!f) { std::cout << "[TRT] ERROR: Cannot open engine: " << enginePath << std::endl; delete ctx; return nullptr; }
    fseek(f, 0, SEEK_END); long size = ftell(f); fseek(f, 0, SEEK_SET);
    std::vector<char> data(size); fread(data.data(), 1, size, f); fclose(f);

    ctx->engine.reset(ctx->runtime->deserializeCudaEngine(data.data(), size));
    if (!ctx->engine) { std::cout << "[TRT] ERROR: Failed to deserialize engine." << std::endl; delete ctx; return nullptr; }
    ctx->context.reset(ctx->engine->createExecutionContext());
    if (!ctx->context) { delete ctx; return nullptr; }

    Dims4 frameDims{1, 3, height, width}; Dims tsDims{1, {1}};
    ctx->context->setInputShape("img0", frameDims); ctx->context->setInputShape("img1", frameDims); ctx->context->setInputShape("timestep", tsDims);

    size_t frameBytes = 3LL * height * width * sizeof(float);
    cudaMalloc(&ctx->d_img0, frameBytes); cudaMalloc(&ctx->d_img1, frameBytes);
    cudaMalloc(&ctx->d_timestep, sizeof(float)); cudaMalloc(&ctx->d_output, frameBytes);
    cudaStreamCreate(&ctx->stream);

    ctx->context->setTensorAddress("img0", ctx->d_img0); ctx->context->setTensorAddress("img1", ctx->d_img1);
    ctx->context->setTensorAddress("timestep", ctx->d_timestep); ctx->context->setTensorAddress("output", ctx->d_output);
    return ctx;
}

extern "C" __declspec(dllexport) int Rife_Interpolate(void* hCtx, float* h_img0, float* h_img1, float timestep, float* h_output) {
    auto* ctx = (RifeContext*)hCtx;
    if (!ctx) return -1;
    size_t frameBytes = 3LL * ctx->H * ctx->W * sizeof(float);
    if (cudaMemcpyAsync(ctx->d_img0, h_img0, frameBytes, cudaMemcpyHostToDevice, ctx->stream) != 0) return 1;
    if (cudaMemcpyAsync(ctx->d_img1, h_img1, frameBytes, cudaMemcpyHostToDevice, ctx->stream) != 0) return 2;
    if (cudaMemcpyAsync(ctx->d_timestep, &timestep, sizeof(float), cudaMemcpyHostToDevice, ctx->stream) != 0) return 3;
    if (!ctx->context->enqueueV3(ctx->stream)) return 4;
    if (cudaMemcpyAsync(h_output, ctx->d_output, frameBytes, cudaMemcpyDeviceToHost, ctx->stream) != 0) return 5;
    cudaStreamSynchronize(ctx->stream);
    return 0;
}

extern "C" __declspec(dllexport) void Rife_Destroy(void* hCtx) {
    auto* ctx = (RifeContext*)hCtx;
    if (ctx) {
        cudaFree(ctx->d_img0); cudaFree(ctx->d_img1); cudaFree(ctx->d_timestep); cudaFree(ctx->d_output);
        if (ctx->stream) cudaStreamDestroy(ctx->stream);
        delete ctx;
    }
}
