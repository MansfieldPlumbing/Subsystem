#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <iostream>

#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")

template <class T> void SafeRelease(T **ppT) {
    if (*ppT) { (*ppT)->Release(); *ppT = NULL; }
}

struct ReaderContext {
    IMFSourceReader* reader = nullptr;
    int width = 0;
    int height = 0;
};

struct WriterContext {
    IMFSinkWriter* writer = nullptr;
    int streamIndex = 0;
    long long frameDuration = 0;
    long long currentTimestamp = 0;
};

extern "C" __declspec(dllexport) int MF_Init() {
    HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return (int)hr;
    hr = MFStartup(MF_VERSION);
    return FAILED(hr) ? (int)hr : 0;
}

extern "C" __declspec(dllexport) void MF_Deinit() {
    MFShutdown();
    CoUninitialize();
}

extern "C" __declspec(dllexport) void Reader_Close(void* hCtx) {
    auto* ctx = (ReaderContext*)hCtx;
    if (ctx) {
        SafeRelease(&ctx->reader);
        delete ctx;
    }
}

extern "C" __declspec(dllexport) void* Reader_Open(const wchar_t* filepath, int* width, int* height, int* fpsNum, int* fpsDen) {
    auto* ctx = new ReaderContext();
    IMFAttributes* pAttributes = nullptr;
    MFCreateAttributes(&pAttributes, 1);
    pAttributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
    HRESULT hr = MFCreateSourceReaderFromURL(filepath, pAttributes, &ctx->reader);
    SafeRelease(&pAttributes);
    if (FAILED(hr)) { delete ctx; return nullptr; }
    ctx->reader->SetStreamSelection((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE);
    IMFMediaType* pType = nullptr;
    MFCreateMediaType(&pType);
    pType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    pType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    hr = ctx->reader->SetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, NULL, pType);
    SafeRelease(&pType);
    if (FAILED(hr)) { Reader_Close(ctx); return nullptr; }
    IMFMediaType* pCurrentType = nullptr;
    if (SUCCEEDED(ctx->reader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &pCurrentType))) {
        UINT32 w, h;
        MFGetAttributeSize(pCurrentType, MF_MT_FRAME_SIZE, &w, &h);
        *width = ctx->width = w; *height = ctx->height = h;
        UINT32 num, den;
        if (SUCCEEDED(MFGetAttributeRatio(pCurrentType, MF_MT_FRAME_RATE, &num, &den))) { *fpsNum = num; *fpsDen = den; } 
        else { *fpsNum = 30; *fpsDen = 1; }
        SafeRelease(&pCurrentType);
    }
    return ctx;
}

extern "C" __declspec(dllexport) int Reader_Read(void* hCtx, unsigned char* destBuffer) {
    auto* ctx = (ReaderContext*)hCtx;
    if (!ctx || !destBuffer) return 1;
    IMFSample* pSample = nullptr;
    DWORD flags = 0;
    HRESULT hr = ctx->reader->ReadSample((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, NULL, &flags, NULL, &pSample);
    if (FAILED(hr)) return 1;
    if (flags & MF_SOURCE_READERF_ENDOFSTREAM) return 2;
    if (pSample == nullptr) return 0; 
    IMFMediaBuffer* pBuffer = nullptr;
    if (FAILED(pSample->ConvertToContiguousBuffer(&pBuffer))) { SafeRelease(&pSample); return 1; }
    BYTE* pSrc = nullptr; LONG srcStride = 0; IMF2DBuffer* p2DBuffer = nullptr;
    if (SUCCEEDED(pBuffer->QueryInterface(IID_PPV_ARGS(&p2DBuffer)))) { p2DBuffer->Lock2D(&pSrc, &srcStride); } 
    else { DWORD len; pBuffer->Lock(&pSrc, NULL, &len); srcStride = ctx->width * 4; }
    if (pSrc) { MFCopyImage(destBuffer, ctx->width * 4, pSrc, srcStride, ctx->width, ctx->height); }
    if (p2DBuffer) { p2DBuffer->Unlock2D(); SafeRelease(&p2DBuffer); } else { pBuffer->Unlock(); }
    SafeRelease(&pBuffer); SafeRelease(&pSample);
    return 0;
}

extern "C" __declspec(dllexport) void* Writer_Open(const wchar_t* filepath, int width, int height, int fpsNum, int fpsDen, int bitrate) {
    auto* ctx = new WriterContext();
    ctx->frameDuration = (10000000LL * fpsDen) / fpsNum; 
    if (FAILED(MFCreateSinkWriterFromURL(filepath, NULL, NULL, &ctx->writer))) { delete ctx; return nullptr; }
    IMFMediaType* pOutType = nullptr; MFCreateMediaType(&pOutType);
    pOutType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    pOutType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    pOutType->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
    pOutType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(pOutType, MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(pOutType, MF_MT_FRAME_RATE, fpsNum, fpsDen);
    MFSetAttributeRatio(pOutType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    ctx->writer->AddStream(pOutType, (DWORD*)&ctx->streamIndex);
    SafeRelease(&pOutType);
    IMFMediaType* pInType = nullptr; MFCreateMediaType(&pInType);
    pInType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    pInType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    pInType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(pInType, MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(pInType, MF_MT_FRAME_RATE, fpsNum, fpsDen);
    MFSetAttributeRatio(pInType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    ctx->writer->SetInputMediaType(ctx->streamIndex, pInType, NULL);
    SafeRelease(&pInType);
    ctx->writer->BeginWriting();
    return ctx;
}

extern "C" __declspec(dllexport) int Writer_PushFrame(void* hCtx, unsigned char* data, int size) {
    auto* ctx = (WriterContext*)hCtx;
    if(!ctx) return 1;
    IMFSample* pSample = nullptr; IMFMediaBuffer* pBuffer = nullptr;
    MFCreateMemoryBuffer(size, &pBuffer);
    BYTE* pDest = nullptr;
    pBuffer->Lock(&pDest, NULL, NULL);
    memcpy(pDest, data, size);
    pBuffer->Unlock();
    pBuffer->SetCurrentLength(size);
    MFCreateSample(&pSample);
    pSample->AddBuffer(pBuffer);
    pSample->SetSampleTime(ctx->currentTimestamp);
    pSample->SetSampleDuration(ctx->frameDuration);
    HRESULT hr = ctx->writer->WriteSample(ctx->streamIndex, pSample);
    SafeRelease(&pBuffer); SafeRelease(&pSample);
    if (SUCCEEDED(hr)) { ctx->currentTimestamp += ctx->frameDuration; return 0; }
    return 1;
}

extern "C" __declspec(dllexport) void Writer_Close(void* hCtx) {
    auto* ctx = (WriterContext*)hCtx;
    if (ctx) { if (ctx->writer) { ctx->writer->Finalize(); SafeRelease(&ctx->writer); } delete ctx; }
}
