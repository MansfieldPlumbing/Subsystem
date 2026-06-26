# DEPTH_TRT

**Part of the sm86 / Ampere TensorRT collection:** [huggingface.co/MansfieldPlumbing](https://huggingface.co/MansfieldPlumbing)

**Native Frame Inference Series | Depth Anything V2 | TensorRT | C# + C++**
A high-performance implementation of **Depth Anything V2 (ViT-Small)** compiled to TensorRT for native Windows inference.

---

## Technical Innovation: High-Throughput In-Memory Pipeline

This project achieves maximum execution speed by implementing a native **In-Memory Pipeline** that bypasses standard managed code bottlenecks:

* **Unsafe Memory Transposition:** Leverages C# `unsafe` pointer arithmetic and `LockBits` to perform high-speed RGB-to-Planar transposition. This achieves native C++ performance levels by eliminating CLR memory-safety overhead during the tensor preparation phase.
* **Zero-Python Runtime:** Built purely on C++/C# interoperability (P/Invoke). No environment overhead, no interpreter latency, and zero external runtime dependencies.
* **Hardware-Aware Memory Alignment:** The pipeline is architected to align with the underlying hardware's memory boundaries, passing raw memory addresses directly to the TensorRT execution context for a **zero-copy data path** from CPU to GPU.
* **Integrated Normalization:** ImageNet-standard normalization is fused into the unmanaged memory-copy phase, eliminating redundant data traversals.

> **Engineering Note:** By utilizing the `unsafe` context and `System.Runtime.InteropServices`, this implementation maintains a zero-copy data path. This allows for high-throughput depth map generation on Windows systems with NVIDIA hardware, providing a standalone binary solution that is significantly more portable and performant than traditional Python-based inference scripts.

---

## Quick Start

```powershell
.\Depth_TRT.exe "image.jpg"                  # outputs image_depth.png
.\Depth_TRT.exe "image.jpg" -o "custom.png"
.\Depth_TRT.exe "image.jpg" --no-invert      # disparity convention (near=dark)
```

---

## Engine Generation

This repository does not ship with a pre-built engine, as TensorRT engines are specific to your GPU architecture (Compute Capability). Build your engine using `trtexec`:

```powershell
# Example: Build engine for sm86 (RTX 30-series)
trtexec `
  --onnx="models\depth_anything_v2_vits.onnx" `
  --saveEngine="models\depth_v2_vits_sm86_trt10.x.engine" `
  --fp16 `
  --useCudaGraph --noDataTransfers --noTF32
```
*The application will auto-discover the first `.engine` file found within the `models\` directory at runtime.*

---

## Build from Source

Execute the automated build launcher to configure your local environment:

```powershell
.\launch.bat   # or: pwsh -File setup.ps1
```

**Menu Options:**
1. **Unblock scripts**
2. **Preflight checks** (validates CUDA, TensorRT, MSVC, .NET)
3. **Install dependencies** (winget-installable items)
4. **Build** (compiles `depth_trt.dll` + `Depth_TRT.exe`)

---

## Requirements

| Dependency | Version | Notes |
| :--- | :--- | :--- |
| **NVIDIA Driver** | ≥ 561.0 | Stable production branch recommended |
| **CUDA Toolkit** | ≥ 13.0 | Required for `cudart64_13.dll` |
| **TensorRT SDK** | ≥ 10.0 | Ensure `nvinfer.dll` is in the system PATH or app root |
| **VS Build Tools 2022** | C++ workload | Required for native bridge compilation |
| **.NET SDK 9** | ≥ 9.0 | Host application runtime |

---

## License
This project is licensed under the **MIT License** - see the LICENSE file for details.

### Author
**Mr. Mansfield** - [github.com/MansfieldPlumbing](https://github.com/MansfieldPlumbing)