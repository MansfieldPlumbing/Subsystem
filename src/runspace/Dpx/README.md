# DPX — High-Performance Neural Engine Architecture

## Core Architectural Doctrine

### Node-Local Synchrony vs Decoupled Free-Running Pipeline

#### At the Node Level (Local Loop)
Each node is internally synchronous: **Compute to Scratch -> Blit to Blit Buffer**, over and over in a continuous loop. It completes its local compute pass on its scratch buffer, blits the result, and repeats.

#### Across the Pipeline (Node-to-Node)
The system is **NOT globally synchronous**. There are no forced global barriers or CPU-blocking pipeline stalls across nodes. Nodes run **autonomously and decoupled**. Node B blits from Node A's Blit Buffer into its own Scratch Buffer whenever Node B is ready to execute its next cycle. If Node A has looped 3 times while Node B looped 2 times, Node B simply ingests Node A's latest blitted frame ("latest wins").

### Architectural Summary Matrix

| Scope | Execution Mode | Behavior |
| :--- | :--- | :--- |
| **Inside a Single Node** | **Node-Synchronous** | `Scratch (Workload)` -> `Blit Buffer` loop |
| **Between Multiple Nodes** | **Decoupled / Free-Running** | `Consumer Ingress` blits latest frame from previous node's `Blit Buffer` without global locking |

This makes the node pipeline fully decoupled, continuous, and free-running!

---

## Dynamic Workload Evaluator & VRAM Allocation Strategy

Following the `llama.cpp` sizing model, DPX dynamically calculates the exact memory footprint of a model before dispatching execution:

$$\text{Total Workload Bytes} = W_{\text{weights}} + W_{\text{scratch}} + W_{\text{kv\_cache}}$$

1. **Hardware Discovery**: DPX enumerates all physical DXGI/Vulkan GPU adapters (e.g. heterogeneous setups with mixed VRAM sizes like 8.1 GB AMD Radeon Pro V340L and 5.0 GB NVIDIA Quadro P2000).
2. **Workload Sizing Evaluation**:
   - **Single-GPU Placement**: If $\text{Total Workload Bytes} < \text{VRAM}_{\text{adapter}}$, DPX loads 100% of weights, scratch buffers, and KV cache into the primary GPU VRAM. **Zero PCIe ping-ponging**.
   - **Multi-GPU / Multi-Die Pipeline Split**: If $\text{Total Workload Bytes} > \text{VRAM}_{\text{adapter}}$, DPX partitions layer blocks across available GPUs, connecting them via `DpxMultiplexer` decoupled `Scratch -> Blit` buffers.
