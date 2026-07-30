# DPX — High-Performance Neural Engine Architecture

## Core Architectural Doctrine: "Contracts Are All You Need"

At pipeline initialization (boot), Node $B$ (Consumer) receives a static **Capability Contract** from Node $A$ (Producer):
1. The VRAM address / handle of Node $A$'s **Blit Buffer**.
2. The **256-Byte Aligned Row Pitch Layout** contract.
3. The VRAM handle / address of Node $A$'s **Hardware Fence** (`ID3D12Fence` / `VkFence`).

Once contracts are bound at startup, **no CPU runtime orchestration is ever needed again**.

Node $B$ queries or waits on Node $A$'s fence directly on the GPU hardware (`ID3D12CommandQueue::Wait`). The CPU does **0 runtime dispatch coordination** during execution. The contract is handed over once at startup, and the hardware pipeline runs free-running forever!

---

## 1 Layer = 1 Autonomous Pipeline Node

In a 28-layer model (like Gemma 4 E2B), **Layer 0, Layer 1, Layer 2 ... Layer 27 are 28 distinct autonomous Nodes**.

### Node Architecture Specification

Each Layer Node owns:
1. **1 Scratch Buffer** (in GPU VRAM `DEFAULT` heap): Where the layer executes its local compute workload (Attention, Projections, RMSNorm, Feed-Forward).
2. **1 Blit Buffer** (in GPU VRAM `DEFAULT` heap): Where the layer blits its finalized output tensor frame before repeating its loop.
3. **1 Hardware Fence**: For optional signal/wait completion tracking across nodes.

```
[Layer 0 Node] ----Blit----> (Layer 0 Blit Buffer)
                                    |
                         "Contracts Are All You Need"
                             (GPU Hardware Wait)
                                    v
[Layer 1 Node] ----Blit----> (Layer 1 Blit Buffer)
                                    |
                             (GPU Hardware Wait)
                                    v
[Layer 2 Node] ----Blit----> (Layer 2 Blit Buffer)
                                   ...
```

---

## Architectural Summary Matrix

| Scope | Execution Mode | Behavior |
| :--- | :--- | :--- |
| **Inside a Single Node** | **Node-Synchronous** | `Scratch (Workload)` -> `Blit Buffer` loop |
| **Between Multiple Nodes** | **Decoupled / Free-Running** | `Consumer Ingress` blits latest frame from previous node's `Blit Buffer` via startup Capability Contract |

This makes the node pipeline fully decoupled, continuous, and free-running!

---

## Hardware-Sympathetic Row-Major Memory Layout

To maximize memory bus utilization on modern GPUs (NVIDIA, AMD Radeon, Qualcomm Adreno), Scratch and Blit buffers follow strict 256-byte cache-line pitch alignment rules:

$$\text{AlignedPitch} = (\text{WidthInBytes} + 255) \mathbin{\&} \sim 255$$

- **Peak DMA Copy Bandwidth**: 256-byte alignment enables GPU DMA copy engines (`CopyBufferRegion` / `CopyTextureRegion`) to run at peak VRAM bus bandwidth (~400+ GB/s).
- **Data Packing & Tail Padding**: Incomplete trailing rows/blocks are padded with zeros up to the 256-byte cache line boundary to prevent unaligned VRAM access.
- **Tail Truncation**: Compute dispatches optionally cut incomplete trailing padding elements completely, ensuring 0 GPU cycles are wasted executing on zero-padding.

---

## Dynamic Workload Evaluator & VRAM Allocation Strategy

Following the `llama.cpp` sizing model, DPX dynamically calculates the exact memory footprint of a model before dispatching execution:

$$\text{Total Workload Bytes} = W_{\text{weights}} + W_{\text{scratch}} + W_{\text{kv\_cache}}$$

1. **Hardware Discovery**: DPX enumerates all physical DXGI/Vulkan GPU adapters (e.g. heterogeneous setups with mixed VRAM sizes like 8.1 GB AMD Radeon Pro V340L and 5.0 GB NVIDIA Quadro P2000).
2. **Workload Sizing Evaluation**:
   - **Single-GPU Placement**: If $\text{Total Workload Bytes} < \text{VRAM}_{\text{adapter}}$, DPX loads 100% of weights, scratch buffers, and KV cache into the primary GPU VRAM. **Zero PCIe ping-ponging**.
   - **Multi-GPU / Multi-Die Pipeline Split**: If $\text{Total Workload Bytes} > \text{VRAM}_{\text{adapter}}$, DPX partitions layer nodes across available GPUs, connecting them via `DpxMultiplexer` decoupled capability contracts.
