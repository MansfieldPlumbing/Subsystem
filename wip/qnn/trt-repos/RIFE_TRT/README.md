# RIFE TRT

Part of my sm86 / Ampere TensorRT collection: https://huggingface.co/MansfieldPlumbing

RIFE 4.9 frame interpolation compiled to TensorRT for native Windows inference.  
Validated on RTX 3090 (sm86). Pascal/Blackwell/Hopper builds welcome — open an issue with results.

## Model Source
This repo uses the RIFE 4.9 ONNX export from **yuvraj108c/rife-onnx**:  
🔗 [rife49_ensemble_True_scale_1_sim.onnx](https://huggingface.co/yuvraj108c/rife-onnx/blob/main/rife49_ensemble_True_scale_1_sim.onnx)

Original paper: *Real-Time Intermediate Flow Estimation for Video Frame Interpolation* (Huang et al., 2020)  
🔗 [arXiv:2011.06294](https://arxiv.org/abs/2011.06294)

---

## Quick Start

```powershell
.\RIFE_TRT.exe "video.mp4"          # 2x (default)
.\RIFE_TRT.exe "video.mp4" -4x      # 4x
.\RIFE_TRT.exe "video.mp4" -8x      # 8x
.\RIFE_TRT.exe "video.mp4" -4x -o "video_smooth.mp4"
```

Output: `video_2x.mp4` (or as specified by `-o`).

---

## Engine

This repo ships no pre-built engine — engines are GPU-architecture-specific.

**Build your engine once with trtexec:**

```powershell
# Download ONNX
Invoke-WebRequest -Uri "https://huggingface.co/yuvraj108c/rife-onnx/resolve/main/rife49_ensemble_True_scale_1_sim.onnx" `
                  -OutFile "models\rife49_ensemble_True_scale_1_sim.onnx"

# Build engine for sm86 (RTX 3090/3080/3070/3060 Ti)
trtexec `
  --onnx="models\rife49_ensemble_True_scale_1_sim.onnx" `
  --saveEngine="models\rife49_sm86_trt10.15.engine" `
  --fp16 `
  --minShapes=img0:1x3x270x480,img1:1x3x270x480 `
  --optShapes=img0:1x3x480x854,img1:1x3x480x854 `
  --maxShapes=img0:1x3x1080x1920,img1:1x3x1080x1920 `
  --useCudaGraph --noDataTransfers --noTF32
```

Build takes 5–20 minutes. Run once, reuse forever.

---

## Architecture & Pipeline

[cite_start]**Zero-copy in-memory execution.** No per-frame disk IO or intermediate PNG extraction. [cite: 467]

1. [cite_start]**Media Foundation Bridge**: Native Windows C++ COM bridge leverages hardware decoding to pull video directly into packed RGB32 memory buffers. 
2. [cite_start]**Multi-Threaded Reshape**: Unsafe C# Parallel.For handles real-time transposition to the CHW float32 arrays expected by the TRT graph. [cite: 469]
3. [cite_start]**TensorRT Graph**: Dual-input graph executes recursively via an asynchronous CUDA stream. [cite: 470]
4. **Zero-Encode Muxing**: Audio tracks are stream-copied from the source. 
   > [cite_start]**Note:** FFmpeg is included in this pipeline primarily as a fallback for final stream-copying and audio muxing. [cite: 471]

---

## Build

```powershell
.\launch.bat   # or: pwsh -File setup.ps1
```

Menu:
```
  [1]  Unblock scripts
  [2]  Preflight checks     (validates CUDA, TensorRT, MSVC, .NET, ffmpeg)
  [3]  Install dependencies (winget-installable items)
  [4]  Python environment   (optional — engine building)
  [5]  Build                (compiles rife_trt.dll + mf_bridge.dll + RIFE_TRT.exe)
```

---

## Requirements

| Dependency | Version | Notes |
|---|---|---|
| NVIDIA Driver | ≥ 561.0 | https://www.nvidia.com/drivers |
| CUDA Toolkit | ≥ 13.0 | https://developer.nvidia.com/cuda-downloads |
| TensorRT SDK | ≥ 10.0 | https://developer.nvidia.com/tensorrt — zip extract |
| ffmpeg | any | `winget install Gyan.FFmpeg` |
| VS Build Tools 2022 | C++ workload | `winget install Microsoft.VisualStudio.2022.BuildTools` |
| .NET SDK 9 | ≥ 9.0 | `winget install Microsoft.DotNet.SDK.9` |

---
## License

MIT License — see [LICENSE](LICENSE) for details.

The underlying RIFE model weights are from the original authors (Huang et al.), released under the MIT license.  
TensorRT engines compiled from this ONNX are for personal and research use. Commercial use is subject to the original model's license.

---

## Author

**Mr. Mansfield** — [github.com/MansfieldPlumbing](https://github.com/MansfieldPlumbing)