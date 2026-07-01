// DpTensor.cs - the VOM-native tensor (CRQ164). A Handle (+ optional Fence), resolved to a Span
// per use. NO managed float[]/long[]/byte[] payload, ever - not even as a fallback path. bf16 is
// the default element format for activations/weights: a free shift/truncate from fp32 (no
// exponent rebias, same dynamic range as fp32, NPU-native - Hexagon HMX is bf16-native). The rare
// full-precision op gets a TRANSIENT VOM-backed scratch region (Vom.Alloc'd under the caller's
// owner, Close'd by the caller when done) - never a GC array standing in as the "real" storage.
//
// Packed quantized weights (2/4/8-bit) carry their scale/zero-point sidecars as VOM handles too
// (QScale/QZero), not GC float[]/long[] - the whole tensor, including its metadata, is ambient
// and queryable through the same owner table as everything else (CRQ137's vom: provider will be
// able to walk these once it exists).
using System;
using System.Runtime.CompilerServices;
using Subsystem.Vom;
// Vom (the static class) shares its name with the Subsystem.Vom namespace it lives in; the
// established convention for the bare-static-call syntax (Vom.Alloc/.Close/.GetFence) is this
// alias, same as DpxDecoder.cs's `VomClass` — resolves a namespace/type binder ambiguity that a
// full dotnet build tolerates but ss build self's narrower Roslyn compile set does not.
using VomClass = Subsystem.Vom.Vom;

namespace Subsystem.Dpx;

public readonly struct DpTensor
{
    public readonly Owner Owner;
    public readonly Handle Data;     // primary payload; element layout = Data.Format
    public readonly int[] Shape;

    public readonly Handle? QScale;  // per-row/per-block quant scale (Bf16 or Float32); null = not packed
    public readonly Handle? QZero;   // per-row/per-block zero-point, same length as QScale; null = symmetric or not packed
    public readonly int Qbits;       // 0 = not packed; else 2/4/8
    public readonly int Qaxis;       // quantized dimension (axis 0 for gemma's per-output-channel weights)

    private DpTensor(Owner owner, Handle data, int[] shape, Handle? qScale, Handle? qZero, int qbits, int qaxis)
    {
        Owner = owner; Data = data; Shape = shape;
        QScale = qScale; QZero = qZero; Qbits = qbits; Qaxis = qaxis;
    }

    public long Count { get { long n = 1; foreach (var d in Shape) n *= d; return n; } }
    public bool IsQuant => Qbits > 0;

    private static int ElemBytes(VomFormat f) => f switch
    {
        VomFormat.Bf16   => 2,
        VomFormat.Half   => 2,
        VomFormat.Float32 => 4,
        VomFormat.Raw32  => 4,
        VomFormat.Int64  => 8,
        VomFormat.Bytes  => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, "unhandled VomFormat in DpTensor.ElemBytes"),
    };

    // --- Allocation ---

    public static DpTensor Alloc(Owner owner, int[] shape, VomFormat format = VomFormat.Bf16,
                                  bool withFence = false, string subdir = "Objects", string? name = null)
    {
        long count = 1; foreach (var d in shape) count *= d;
        int byteCount = checked((int)(count * ElemBytes(format)));
        var h = VomClass.Alloc(owner, byteCount, format, "DpTensor", withFence, subdir, name);
        return new DpTensor(owner, h, shape, null, null, 0, 0);
    }

    // Wrap an already-packed quantized weight: Data holds the packed sub-byte/byte bytes (format
    // Bytes), QScale/QZero are separate VOM regions (one element per quantized row/block).
    public static DpTensor AllocQuant(Owner owner, int[] shape, int packedByteCount, int qbits, int qaxis,
                                       int scaleCount, bool hasZero, string subdir = "Objects", string? name = null)
    {
        var data = VomClass.Alloc(owner, packedByteCount, VomFormat.Bytes, "DpTensor.Packed", false, subdir, name);
        var scale = VomClass.Alloc(owner, checked(scaleCount * ElemBytes(VomFormat.Bf16)), VomFormat.Bf16, "DpTensor.QScale", false, subdir, name is null ? null : name + ".qscale");
        Handle? zero = hasZero
            ? VomClass.Alloc(owner, checked(scaleCount * ElemBytes(VomFormat.Bf16)), VomFormat.Bf16, "DpTensor.QZero", false, subdir, name is null ? null : name + ".qzero")
            : null;
        return new DpTensor(owner, data, shape, scale, zero, qbits, qaxis);
    }

    // --- Element accessors (Span resolved per-use over the native region; nothing is copied to
    // managed memory here) ---

    public unsafe Span<ushort> ReadBf16()
    {
        if (Data.Format != VomFormat.Bf16)
            throw new InvalidOperationException($"ReadBf16 on a {Data.Format} tensor - wrong accessor.");
        return new Span<ushort>((void*)Data.Resource, (int)Count);
    }

    public unsafe Span<float> ReadF32()
    {
        if (Data.Format != VomFormat.Float32)
            throw new InvalidOperationException($"ReadF32 on a {Data.Format} tensor - call Resolve for bf16, or use ReadBf16.");
        return new Span<float>((void*)Data.Resource, (int)Count);
    }

    public unsafe Span<long> ReadI64()
    {
        if (Data.Format != VomFormat.Int64)
            throw new InvalidOperationException($"ReadI64 on a {Data.Format} tensor - wrong accessor.");
        return new Span<long>((void*)Data.Resource, (int)Count);
    }

    public unsafe Span<byte> ReadBytes()
    {
        if (Data.Format != VomFormat.Bytes)
            throw new InvalidOperationException($"ReadBytes on a {Data.Format} tensor - wrong accessor.");
        return new Span<byte>((void*)Data.Resource, Data.ByteCount);
    }

    // --- bf16 <-> fp32: a free shift/truncate, no exponent rebias (same bits, different width) ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ExtractBf16(float f) => (ushort)(BitConverter.SingleToUInt32Bits(f) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ResolveFp32(ushort b) => BitConverter.UInt32BitsToSingle((uint)b << 16);

    // The rare full-precision op: widen this bf16 tensor into a TRANSIENT fp32 region under
    // scratchOwner. The caller owns the result's lifetime (Close it when done) - this never
    // materializes a GC float[] as the "real" storage, only ever a VOM region.
    public DpTensor Resolve(Owner scratchOwner, string? name = null)
    {
        if (Data.Format != VomFormat.Bf16)
            throw new InvalidOperationException($"Resolve expects a Bf16 source, got {Data.Format}.");
        var dst = Alloc(scratchOwner, Shape, VomFormat.Float32, withFence: false, subdir: "Scratch", name: name);
        var src16 = ReadBf16();
        var dstF = dst.ReadF32();
        for (int i = 0; i < src16.Length; i++) dstF[i] = ResolveFp32(src16[i]);
        return dst;
    }

    // The inverse: narrow a transient/foreign fp32 region down into this bf16 tensor in place.
    public unsafe void Write(ReadOnlySpan<float> src)
    {
        if (Data.Format != VomFormat.Bf16)
            throw new InvalidOperationException($"Write(ReadOnlySpan<float>) targets a Bf16 tensor, got {Data.Format}.");
        var dst = ReadBf16();
        if (src.Length != dst.Length)
            throw new ArgumentException($"length mismatch: src {src.Length} vs dst {dst.Length}");
        for (int i = 0; i < src.Length; i++) dst[i] = ExtractBf16(src[i]);
    }

    // --- Zero-copy region-to-region blit (KV-cache writes, subgraph handoffs) - no managed
    // intermediate, no Marshal.Copy-through-array. Both sides must already be the same byte size. ---

    public unsafe void WriteTo(DpTensor dest)
    {
        if (Data.ByteCount != dest.Data.ByteCount)
            throw new ArgumentException($"blit size mismatch: src {Data.ByteCount}B vs dst {dest.Data.ByteCount}B");
        Buffer.MemoryCopy((void*)Data.Resource, (void*)dest.Data.Resource, dest.Data.ByteCount, Data.ByteCount);
    }

    // --- Fence (best-effort, latest-wins - the producer signals after a write, a consumer Waits /
    // WaitAny / WaitN against exactly the fences it needs; no locks in the hot path) ---

    public Fence? GetFence() => VomClass.GetFence(Owner, Data.Id);

    public void Signal(ulong value)
    {
        var f = GetFence() ?? throw new InvalidOperationException("DpTensor was not allocated withFence:true.");
        f.Signal(value);
    }

    public void Wait(ulong value)
    {
        var f = GetFence() ?? throw new InvalidOperationException("DpTensor was not allocated withFence:true.");
        f.Wait(value);
    }

    // --- Lifecycle ---

    public void Close()
    {
        VomClass.Close(Owner, Data.Id);
        if (QScale is Handle qs) VomClass.Close(Owner, qs.Id);
        if (QZero is Handle qz) VomClass.Close(Owner, qz.Id);
    }
}
