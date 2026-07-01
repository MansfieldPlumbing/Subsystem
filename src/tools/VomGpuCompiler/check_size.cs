using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct D3D12_SHADER_BYTECODE
{
    public IntPtr pShaderBytecode;
    public IntPtr BytecodeLength;
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D12_CACHED_PIPELINE_STATE
{
    public IntPtr pCachedBlob;
    public IntPtr CachedBlobSizeInBytes;
}

[StructLayout(LayoutKind.Sequential)]
public struct D3D12_COMPUTE_PIPELINE_STATE_DESC
{
    public IntPtr pRootSignature;
    public D3D12_SHADER_BYTECODE CS;
    public uint NodeMask;
    public D3D12_CACHED_PIPELINE_STATE CachedPSO;
    public int Flags;
}

public class Program
{
    public static void Main()
    {
        Console.WriteLine("Size: " + Marshal.SizeOf(typeof(D3D12_COMPUTE_PIPELINE_STATE_DESC)));
        Console.WriteLine("Offset pRootSignature: " + Marshal.OffsetOf(typeof(D3D12_COMPUTE_PIPELINE_STATE_DESC), "pRootSignature"));
        Console.WriteLine("Offset CS: " + Marshal.OffsetOf(typeof(D3D12_COMPUTE_PIPELINE_STATE_DESC), "CS"));
        Console.WriteLine("Offset NodeMask: " + Marshal.OffsetOf(typeof(D3D12_COMPUTE_PIPELINE_STATE_DESC), "NodeMask"));
        Console.WriteLine("Offset CachedPSO: " + Marshal.OffsetOf(typeof(D3D12_COMPUTE_PIPELINE_STATE_DESC), "CachedPSO"));
        Console.WriteLine("Offset Flags: " + Marshal.OffsetOf(typeof(D3D12_COMPUTE_PIPELINE_STATE_DESC), "Flags"));
    }
}
