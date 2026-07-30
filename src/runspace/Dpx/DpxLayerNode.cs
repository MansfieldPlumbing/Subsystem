#nullable enable
using System;

namespace Subsystem.Dpx;

// DpxLayerNode — 1 Transformer Layer = 1 Autonomous Pipeline Node.
// Core Doctrine: "Contracts Are All You Need"
// At startup, each node receives the capability contract of its upstream producer:
// (Blit Buffer VRAM address, 256-byte aligned row pitch, and Hardware Fence handle).
// Execution is hardware-sympathetic with 256-byte row-major pitch alignment:
// AlignedPitch = (widthInBytes + 255) & ~255.
public sealed class DpxLayerNode
{
    public int LayerIndex { get; }
    public ulong ScratchBytes { get; }
    public ulong BlitBytes { get; }
    public int AlignedScratchPitch { get; }
    public int AlignedBlitPitch { get; }

    public IntPtr ScratchBuffer { get; internal set; }
    public IntPtr BlitBuffer { get; internal set; }
    public long ScratchVA { get; internal set; }
    public long BlitVA { get; internal set; }

    public IntPtr HardwareFence { get; internal set; }
    public ulong FenceValue { get; internal set; }

    public CapabilityContract? UpstreamContract { get; private set; }

    public sealed class CapabilityContract
    {
        public IntPtr ProducerBlitBuffer { get; }
        public long ProducerBlitVA { get; }
        public IntPtr ProducerFence { get; }
        public int RowPitch { get; }

        public CapabilityContract(IntPtr blitBuf, long blitVA, IntPtr fence, int pitch)
        {
            ProducerBlitBuffer = blitBuf;
            ProducerBlitVA = blitVA;
            ProducerFence = fence;
            RowPitch = pitch;
        }
    }

    public DpxLayerNode(int layerIndex, int rawWidthBytes)
    {
        LayerIndex = layerIndex;
        int alignedPitch = CalculateAlignedPitch(rawWidthBytes);
        AlignedScratchPitch = alignedPitch;
        AlignedBlitPitch = alignedPitch;
        ScratchBytes = (ulong)alignedPitch;
        BlitBytes = (ulong)alignedPitch;
    }

    public static int CalculateAlignedPitch(int widthBytes)
    {
        return (widthBytes + 255) & ~255;
    }

    public void BindUpstreamContract(DpxLayerNode prevNode)
    {
        UpstreamContract = new CapabilityContract(prevNode.BlitBuffer, prevNode.BlitVA, prevNode.HardwareFence, prevNode.AlignedBlitPitch);
    }
}
