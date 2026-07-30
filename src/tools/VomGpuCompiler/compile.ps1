# compile.ps1 — Compile VOM GPU Compiler on the fly and execute GPU compiler pass.
# Compatible with PowerShell 5.1 (stock Windows 11).

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csharpCode = @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Threading;

namespace Subsystem.Vom
{
    // --- VOM Memory Layout & Generation Handle Specification ---

    public enum VomFormat
    {
        Float32 = 0,
        Half = 1,
        Raw32 = 2,
        Bytes = 3
    }

    public struct Handle
    {
        public string Path;
        public string Type;
        public string Owner;
        public VomFormat Format;
        public int ByteCount;
        public IntPtr Resource;
        public IntPtr Fence;
        public uint Id;
    }

    public class HandleEntry
    {
        public Handle Descriptor;
        public int RefCount;
        public Action Reclaim;
    }

    public class Owner
    {
        public string Path { get; private set; }
        public List<HandleEntry> Handles { get; private set; }
        public Dictionary<string, uint> PathToId { get; private set; }
        public long CurrentBytes;
        public int CurrentElements;

        public Owner(string path)
        {
            Path = path;
            Handles = new List<HandleEntry>();
            PathToId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static unsafe class Vom
    {
        private static readonly Dictionary<string, Owner> _owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        public const int Alignment = 256;

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr _aligned_malloc(IntPtr size, IntPtr alignment);

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void _aligned_free(IntPtr ptr);

        public static Owner CreateOwner(string path)
        {
            lock (_lock)
            {
                Owner o;
                if (!_owners.TryGetValue(path, out o))
                {
                    o = new Owner(path);
                    _owners[path] = o;
                }
                return o;
            }
        }

        public static Handle Alloc(Owner owner, int byteCount, VomFormat format = VomFormat.Float32,
                                   string type = "VomRegion", bool withFence = false,
                                   string subdir = "Objects", string name = null)
        {
            int padded = (byteCount + (Alignment - 1)) & ~(Alignment - 1);
            IntPtr ptr = _aligned_malloc((IntPtr)padded, (IntPtr)Alignment);
            if (ptr == IntPtr.Zero) throw new OutOfMemoryException();

            HandleEntry entry = new HandleEntry();
            entry.RefCount = 1;
            entry.Reclaim = new Action(() => _aligned_free(ptr));

            lock (owner.Handles)
            {
                uint id = (uint)owner.Handles.Count + 1;
                string leaf = name != null ? name : string.Format("0x{0:X8}", id);
                string path = string.IsNullOrEmpty(subdir) 
                    ? string.Format("{0}\\{1}", owner.Path, leaf)
                    : string.Format("{0}\\{1}\\{2}", owner.Path, subdir, leaf);

                Handle h = new Handle();
                h.Path = path;
                h.Type = type;
                h.Owner = owner.Path;
                h.Format = format;
                h.ByteCount = padded;
                h.Resource = ptr;
                h.Fence = IntPtr.Zero;
                h.Id = id;

                entry.Descriptor = h;
                owner.Handles.Add(entry);
                owner.PathToId[path] = id;
                
                Interlocked.Add(ref owner.CurrentBytes, padded);
                Interlocked.Increment(ref owner.CurrentElements);
                return h;
            }
        }

        public static Handle RegisterNative(Owner owner, string type, IntPtr native, Action reclaim,
                                            int byteCount = 0, VomFormat format = VomFormat.Bytes,
                                            string subdir = "Surfaces", string name = null)
        {
            HandleEntry entry = new HandleEntry();
            entry.RefCount = 1;
            entry.Reclaim = reclaim;

            lock (owner.Handles)
            {
                uint id = (uint)owner.Handles.Count + 1;
                string leaf = name != null ? name : string.Format("0x{0:X8}", id);
                string path = string.IsNullOrEmpty(subdir) 
                    ? string.Format("{0}\\{1}", owner.Path, leaf)
                    : string.Format("{0}\\{1}\\{2}", owner.Path, subdir, leaf);

                Handle h = new Handle();
                h.Path = path;
                h.Type = type;
                h.Owner = owner.Path;
                h.Format = format;
                h.ByteCount = byteCount;
                h.Resource = native;
                h.Fence = IntPtr.Zero;
                h.Id = id;

                entry.Descriptor = h;
                owner.Handles.Add(entry);
                owner.PathToId[path] = id;

                if (byteCount > 0) Interlocked.Add(ref owner.CurrentBytes, byteCount);
                Interlocked.Increment(ref owner.CurrentElements);
                return h;
            }
        }

        public static void Terminate(Owner owner)
        {
            lock (_lock)
            {
                lock (owner.Handles)
                {
                    // Release in reverse allocation order
                    for (int i = owner.Handles.Count - 1; i >= 0; i--)
                    {
                        HandleEntry entry = owner.Handles[i];
                        if (entry != null)
                        {
                            try
                            {
                                Console.WriteLine("Reclaiming VOM handle: {0} ({1})", entry.Descriptor.Path, entry.Descriptor.Type);
                                if (entry.Reclaim != null) entry.Reclaim();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Error in reclaim: " + ex.Message);
                            }
                        }
                    }
                    owner.Handles.Clear();
                    owner.PathToId.Clear();
                    owner.CurrentBytes = 0;
                    owner.CurrentElements = 0;
                }
                _owners.Remove(owner.Path);
            }
        }
    }

    // --- VOM GPU Compiler Harness & PE Builder ---

    public static unsafe class VomGpuCompiler
    {
        // --- Native Structs matching HLSL Layouts ---

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct CompileUnit
        {
            public uint FuncIndex;
            public uint OpcodeCount;
            public uint OpcodeOffset;
            public uint RvaOffset;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct LayoutConfig
        {
            public uint O_DosHeader;
            public uint O_TextSection;
            public uint O_RdataSection;
            public uint TotalMethods;
        }

        public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_COMMAND_QUEUE_DESC
        {
            public int Type;
            public int Priority;
            public int Flags;
            public uint NodeMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_RESOURCE_DESC
        {
            public int Dimension;
            public long Alignment;
            public ulong Width;
            public uint Height;
            public ushort DepthOrArraySize;
            public ushort MipLevels;
            public int Format;
            public int SampleCount;
            public int SampleQuality;
            public int Layout;
            public int Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_HEAP_PROPERTIES
        {
            public int Type;
            public int CPUPageProperty;
            public int MemoryPoolPreference;
            public uint CreationNodeMask;
            public uint VisibleNodeMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_RANGE
        {
            public IntPtr Begin;
            public IntPtr End;
        }

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public IntPtr DedicatedVideoMemory;
            public IntPtr DedicatedSystemMemory;
            public IntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        // --- P/Invoke Signatures ---

        [DllImport("d3d12.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int D3D12CreateDevice(IntPtr pAdapter, uint minimumFeatureLevel, byte[] riid, out IntPtr ppDevice);

        [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int CreateDXGIFactory1(byte[] riid, out IntPtr ppFactory);

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int D3DCompile(
            [MarshalAs(UnmanagedType.LPStr)] string pSrcData,
            IntPtr srcDataSize,
            [MarshalAs(UnmanagedType.LPStr)] string pSourceName,
            IntPtr pDefines,
            IntPtr pInclude,
            [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
            [MarshalAs(UnmanagedType.LPStr)] string pTarget,
            uint Flags1,
            uint Flags2,
            out IntPtr ppCode,
            out IntPtr ppErrorMsgs
        );

        // --- Helper for COM VTable Dispatch ---

        public static IntPtr GetVTableSlot(IntPtr pObject, int slot)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pObject);
            return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        }

        // --- Delegates for COM Methods ---

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateCommandQueueDelegate(IntPtr device, ref D3D12_COMMAND_QUEUE_DESC desc, byte[] riid, out IntPtr ppCommandQueue);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateCommandAllocatorDelegate(IntPtr device, int type, byte[] riid, out IntPtr ppCommandAllocator);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateCommandListDelegate(IntPtr device, uint nodeMask, int type, IntPtr pCommandAllocator, IntPtr pInitialState, byte[] riid, out IntPtr ppCommandList);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateRootSignatureDelegate(IntPtr device, uint nodeMask, IntPtr pBlobWithRootSignature, IntPtr blobLengthInBytes, byte[] riid, out IntPtr ppRootSignature);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateComputePipelineStateDelegate(IntPtr device, ref D3D12_COMPUTE_PIPELINE_STATE_DESC pDesc, byte[] riid, out IntPtr ppPipelineState);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateCommittedResourceDelegate(
            IntPtr device,
            ref D3D12_HEAP_PROPERTIES pHeapProperties,
            int heapFlags,
            ref D3D12_RESOURCE_DESC pDesc,
            int initialResourceState,
            IntPtr pOptimizedClearValue,
            byte[] riid,
            out IntPtr ppvResource
        );

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CommandListCloseDelegate(IntPtr commandList);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CommandListResetDelegate(IntPtr commandList, IntPtr allocator, IntPtr initialState);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void SetComputeRootSignatureDelegate(IntPtr commandList, IntPtr rootSignature);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void SetComputeRootUnorderedAccessViewDelegate(IntPtr commandList, uint parameterIndex, long bufferGPUAddress);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void SetComputeRootShaderResourceViewDelegate(IntPtr commandList, uint parameterIndex, long bufferGPUAddress);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void SetComputeRoot32BitConstantsDelegate(IntPtr commandList, uint parameterIndex, uint num32BitValuesToSet, void* pSrcData, uint destOffsetIn32BitValues);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void SetPipelineStateDelegate(IntPtr commandList, IntPtr pipelineState);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void DispatchDelegate(IntPtr commandList, uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void CopyBufferRegionDelegate(IntPtr commandList, IntPtr pDstBuffer, ulong dstOffset, IntPtr pSrcBuffer, ulong srcOffset, ulong numBytes);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void ExecuteCommandListsDelegate(IntPtr commandQueue, uint numCommandLists, IntPtr* ppCommandLists);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int QueueSignalDelegate(IntPtr commandQueue, IntPtr pFence, ulong value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate ulong GetCompletedValueDelegate(IntPtr pFence);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate long GetGPUVirtualAddressDelegate(IntPtr pResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int MapResourceDelegate(IntPtr pResource, uint subresource, ref D3D12_RANGE pReadRange, out void* ppData);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void UnmapResourceDelegate(IntPtr pResource, uint subresource, IntPtr pWrittenRange);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateFenceDelegate(IntPtr device, ulong initialValue, int flags, byte[] riid, out IntPtr ppFence);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr GetBufferPointerDelegate(IntPtr pBlob);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr GetBufferSizeDelegate(IntPtr pBlob);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EnumAdapters1Delegate(IntPtr factory, uint adapterIdx, out IntPtr ppAdapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

        public static void Compile(string outputExePath)
        {
            Console.WriteLine("Initializing VOM GPU compiler harness...");
            Owner owner = Vom.CreateOwner("\\Compiler");

            try
            {
                byte[] deviceIid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7").ToByteArray();
                IntPtr pDevice = IntPtr.Zero;

                // Try default device creation first (hardware adapter)
                int hr = D3D12CreateDevice(IntPtr.Zero, 0xb000, deviceIid, out pDevice); // 0xb000 = D3D_FEATURE_LEVEL_11_0
                Console.WriteLine("D3D12CreateDevice (Default/Hardware) returned: 0x{0:X8}", hr);

                if (hr != 0 || pDevice == IntPtr.Zero)
                {
                    Console.WriteLine("Default device creation failed. Enumerating DXGI adapters for software fallback...");

                    byte[] factoryIid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387").ToByteArray(); // IDXGIFactory1
                    IntPtr pFactory1 = IntPtr.Zero;
                    hr = CreateDXGIFactory1(factoryIid, out pFactory1);
                    Console.WriteLine("CreateDXGIFactory1 (Factory1) returned: 0x{0:X8}, pFactory1: 0x{1:X}", hr, pFactory1.ToInt64());

                    if (hr == 0 && pFactory1 != IntPtr.Zero)
                    {
                        Vom.RegisterNative(owner, "IDXGIFactory1", pFactory1, new Action(() => { if (pFactory1 != IntPtr.Zero) Marshal.Release(pFactory1); }), 0, VomFormat.Bytes, "D3D12", "Factory1");

                        EnumAdapters1Delegate enumAdapters = (EnumAdapters1Delegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pFactory1, 12), typeof(EnumAdapters1Delegate));

                        uint adapterIdx = 0;
                        IntPtr pAdapter = IntPtr.Zero;

                        while (enumAdapters(pFactory1, adapterIdx, out pAdapter) == 0 && pAdapter != IntPtr.Zero)
                        {
                            GetDesc1Delegate getDesc1 = (GetDesc1Delegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pAdapter, 10), typeof(GetDesc1Delegate));

                            DXGI_ADAPTER_DESC1 desc;
                            int descHr = getDesc1(pAdapter, out desc);
                            if (descHr == 0)
                            {
                                Console.WriteLine("Adapter {0}: \"{1}\" [Flags: 0x{2:X}]", adapterIdx, desc.Description, desc.Flags);

                                // DXGI_ADAPTER_FLAG_SOFTWARE = 2
                                bool isSoftware = (desc.Flags & 2) != 0 || desc.Description.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (isSoftware)
                                {
                                    Console.WriteLine("Attempting D3D12 device creation on software adapter: \"{0}\"...", desc.Description);
                                    hr = D3D12CreateDevice(pAdapter, 0xb000, deviceIid, out pDevice);
                                    Console.WriteLine("D3D12CreateDevice on Software Adapter returned: 0x{0:X8}", hr);

                                    if (hr == 0 && pDevice != IntPtr.Zero)
                                    {
                                        Marshal.Release(pAdapter);
                                        break; // Successfully created device!
                                    }
                                }
                            }

                            Marshal.Release(pAdapter);
                            adapterIdx++;
                        }
                    }
                }

                if (hr != 0 || pDevice == IntPtr.Zero)
                {
                    Console.WriteLine("FAIL: D3D12CreateDevice failed with all hardware and software adapters (HRESULT 0x{0:X8})", hr);
                    return;
                }

                // Register Device in VOM
                Vom.RegisterNative(owner, "ID3D12Device", pDevice, new Action(() => { if (pDevice != IntPtr.Zero) Marshal.Release(pDevice); }), 0, VomFormat.Bytes, "D3D12", "Device");
                Console.WriteLine("D3D12 Device initialized successfully.");

                // 2. Embedded Shader Source (Sight Unseen HLSL)
                Console.WriteLine("Loading embedded shader source...");
                string hlslCode = @"
#define VomRS ""RootFlags(0), RootConstants(num32BitConstants=4, b0), SRV(t0), SRV(t1), UAV(u0)""
// VomCompiler.hlsl — The GPU Compiler for a Native Win64 PE Executable (sssd.exe)
// Assembles the PE32+ headers, Import Address Table, and raw x64 machine code.
#define GROUP_SIZE 64
#define LDS_SIZE 1024

struct CompileUnit {
    uint FuncIndex;
    uint OpcodeCount;
    uint OpcodeOffset;
    uint RvaOffset;
};

StructuredBuffer<CompileUnit>   g_IrInput   : register(t0);
StructuredBuffer<uint>          g_Bytecodes : register(t1);
RWStructuredBuffer<uint4>       g_PeOutput  : register(u0);

cbuffer LayoutConfig : register(b0) {
    uint O_DosHeader;
    uint O_TextSection;
    uint O_RdataSection;
    uint TotalMethods;
};

groupshared uint lds_scratch[LDS_SIZE];

void WriteToLds(uint byteOffset, uint value) {
    uint wordIdx = byteOffset / 4;
    uint bitShift = (byteOffset % 4) * 8;
    if (bitShift == 0) {
        lds_scratch[wordIdx] = value;
    } else {
        InterlockedOr(lds_scratch[wordIdx], value << bitShift);
        InterlockedOr(lds_scratch[wordIdx + 1], value >> (32 - bitShift));
    }
}

void AssemblePeHeaders(uint tid) {
    if (tid < LDS_SIZE) { lds_scratch[tid] = 0; }
    GroupMemoryBarrierWithGroupSync();

    if (tid == 0) {
        // ---- DOS header ----
        WriteToLds(0,   0x00005A4D);   // 'MZ'
        WriteToLds(60,  0x00000080);   // e_lfanew -> PE @ 0x80
        // ---- PE signature + COFF header (@0x80) ----
        WriteToLds(128, 0x00004550);   // 'PE\0\0'
        WriteToLds(132, 0x00028664);   // Machine=0x8664(AMD64), NumberOfSections=2
        WriteToLds(136, 0x00000000);   // TimeDateStamp
        WriteToLds(140, 0x00000000);   // PointerToSymbolTable
        WriteToLds(144, 0x00000000);   // NumberOfSymbols
        WriteToLds(148, 0x002200F0);   // SizeOfOptionalHeader=0xF0, Characteristics=0x0022 (EXE|LARGE_ADDR_AWARE)
        // ---- Optional header PE32+ (@0x98=152) ----
        WriteToLds(152, 0x000E020B);   // Magic=0x020B(PE32+), LinkerVer 11.0
        WriteToLds(156, 0x00000200);   // SizeOfCode
        WriteToLds(160, 0x00000200);   // SizeOfInitializedData
        WriteToLds(164, 0x00000000);   // SizeOfUninitializedData
        WriteToLds(168, 0x00000200);   // AddressOfEntryPoint (RVA == file off, low-align)
        WriteToLds(172, 0x00000200);   // BaseOfCode
        WriteToLds(176, 0x00000000);   // ImageBase low (0x100000000)
        WriteToLds(180, 0x00000001);   // ImageBase high
        WriteToLds(184, 0x00000200);   // SectionAlignment = 0x200
        WriteToLds(188, 0x00000200);   // FileAlignment    = 0x200  (==SectionAlignment: low-alignment flat map)
        WriteToLds(192, 0x00000006);   // OS version 6.0
        WriteToLds(196, 0x00000000);   // Image version 0.0
        WriteToLds(200, 0x00000006);   // Subsystem version 6.0
        WriteToLds(204, 0x00000000);   // Win32VersionValue
        WriteToLds(208, 0x00001000);   // SizeOfImage = 0x1000
        WriteToLds(212, 0x00000200);   // SizeOfHeaders = 0x200
        WriteToLds(216, 0x00000000);   // CheckSum
        WriteToLds(220, 0x00000003);   // Subsystem=3 (CUI), DllCharacteristics=0
        WriteToLds(224, 0x00100000);   // SizeOfStackReserve lo
        WriteToLds(228, 0x00000000);   // SizeOfStackReserve hi
        WriteToLds(232, 0x00001000);   // SizeOfStackCommit lo
        WriteToLds(236, 0x00000000);   // SizeOfStackCommit hi
        WriteToLds(240, 0x00100000);   // SizeOfHeapReserve lo
        WriteToLds(244, 0x00000000);   // SizeOfHeapReserve hi
        WriteToLds(248, 0x00001000);   // SizeOfHeapCommit lo
        WriteToLds(252, 0x00000000);   // SizeOfHeapCommit hi
        WriteToLds(256, 0x00000000);   // LoaderFlags
        WriteToLds(260, 0x00000010);   // NumberOfRvaAndSizes = 16
        // Import Table RVA Directory entry (Directory index 1)
        WriteToLds(272, O_RdataSection); // Import Table RVA
        WriteToLds(276, 0x00000100);     // Size of Import Table
        // ---- Section table (@392 = 152 + 0xF0) ----
        WriteToLds(392, 0x7865742E);   // .tex
        WriteToLds(396, 0x00000074);   // t
        WriteToLds(400, 0x00000200);   // .text VirtualSize
        WriteToLds(404, 0x00000200);   // .text VirtualAddress = 0x200
        WriteToLds(408, 0x00000200);   // .text SizeOfRawData
        WriteToLds(412, 0x00000200);   // .text PointerToRawData = 0x200
        WriteToLds(416, 0x00000000);
        WriteToLds(420, 0x00000000);
        WriteToLds(424, 0x00000000);
        WriteToLds(428, 0x60000020);   // .text Characteristics: CODE|EXECUTE|READ
        WriteToLds(432, 0x6164722E);   // .rda
        WriteToLds(436, 0x00006174);   // ta
        WriteToLds(440, 0x00000200);   // .rdata VirtualSize
        WriteToLds(444, 0x00000400);   // .rdata VirtualAddress = 0x400
        WriteToLds(448, 0x00000200);   // .rdata SizeOfRawData
        WriteToLds(452, 0x00000400);   // .rdata PointerToRawData = 0x400
        WriteToLds(456, 0x00000000);
        WriteToLds(460, 0x00000000);
        WriteToLds(464, 0x00000000);
        WriteToLds(468, 0xC0000040);   // .rdata Characteristics: INITIALIZED_DATA|READ|WRITE
    }
    GroupMemoryBarrierWithGroupSync();
    if (tid < 32) {
        g_PeOutput[O_DosHeader / 16 + tid] = uint4(
            lds_scratch[tid * 4 + 0], lds_scratch[tid * 4 + 1],
            lds_scratch[tid * 4 + 2], lds_scratch[tid * 4 + 3]
        );
    }
}

void AssembleImportTable(uint tid) {
    if (tid < LDS_SIZE) { lds_scratch[tid] = 0; }
    GroupMemoryBarrierWithGroupSync();

    if (tid == 0) {
        uint rdataBase = O_RdataSection;
        WriteToLds(0, rdataBase + 40);  // ILT RVA
        WriteToLds(4, 0);               // TimeDateStamp
        WriteToLds(8, 0);               // ForwarderChain
        WriteToLds(12, rdataBase + 200);// DLL Name RVA (kernel32.dll)
        WriteToLds(16, rdataBase + 100);// IAT RVA
        WriteToLds(20, 0); WriteToLds(24, 0); WriteToLds(28, 0); WriteToLds(32, 0); WriteToLds(36, 0);

        WriteToLds(40, rdataBase + 220); // ILT[0] -> GetStdHandle
        WriteToLds(44, 0);
        WriteToLds(48, rdataBase + 240); // ILT[1] -> CreateProcessW
        WriteToLds(52, 0);
        WriteToLds(56, rdataBase + 260); // ILT[2] -> WaitForSingleObject
        WriteToLds(60, 0);
        WriteToLds(64, rdataBase + 284); // ILT[3] -> CloseHandle
        WriteToLds(68, 0);
        WriteToLds(72, rdataBase + 300); // ILT[4] -> ExitProcess
        WriteToLds(76, 0);
        WriteToLds(80, rdataBase + 320); // ILT[5] -> GetExitCodeProcess
        WriteToLds(84, 0);
        WriteToLds(88, 0); WriteToLds(92, 0); // ILT Terminator
    }
    if (tid == 1) {
        uint rdataBase = O_RdataSection;
        WriteToLds(100, rdataBase + 220); WriteToLds(104, 0); // IAT[0] -> GetStdHandle
        WriteToLds(108, rdataBase + 240); WriteToLds(112, 0); // IAT[1] -> CreateProcessW
        WriteToLds(116, rdataBase + 260); WriteToLds(120, 0); // IAT[2] -> WaitForSingleObject
        WriteToLds(124, rdataBase + 284); WriteToLds(128, 0); // IAT[3] -> CloseHandle
        WriteToLds(132, rdataBase + 300); WriteToLds(136, 0); // IAT[4] -> ExitProcess
        WriteToLds(140, rdataBase + 320); WriteToLds(144, 0); // IAT[5] -> GetExitCodeProcess
        WriteToLds(148, 0); WriteToLds(152, 0); // IAT Terminator

        // DLL Name: kernel32.dll
        WriteToLds(200, 0x6E72656B); // kern
        WriteToLds(204, 0x32336C65); // el32
        WriteToLds(208, 0x6C6C642E); // .dll
        WriteToLds(212, 0x00000000);
    }
    if (tid == 2) {
        // Name Hints:
        // GetStdHandle (Hint: 0, Name: GetStdHandle)
        WriteToLds(220, 0x65470000);
        WriteToLds(224, 0x64745374);
        WriteToLds(228, 0x646E6148);
        WriteToLds(232, 0x0000656C);

        // CreateProcessW (Hint: 0, Name: CreateProcessW)
        WriteToLds(240, 0x72430000);
        WriteToLds(244, 0x65746165);
        WriteToLds(248, 0x636F7250);
        WriteToLds(252, 0x57737365);
        WriteToLds(256, 0x00000000);
    }
    if (tid == 3) {
        // WaitForSingleObject (Hint: 0, Name: WaitForSingleObject)
        WriteToLds(260, 0x61570000);
        WriteToLds(264, 0x6F467469);
        WriteToLds(268, 0x6E695372);
        WriteToLds(272, 0x4F656C67);
        WriteToLds(276, 0x63656A62);
        WriteToLds(280, 0x00000074);

        // CloseHandle (Hint: 0, Name: CloseHandle)
        WriteToLds(284, 0x6C430000);
        WriteToLds(288, 0x4865736F);
        WriteToLds(292, 0x6C646E61);
        WriteToLds(296, 0x00000065);

        // ExitProcess (Hint: 0, Name: ExitProcess)
        WriteToLds(300, 0x78450000);
        WriteToLds(304, 0x72507469);
        WriteToLds(308, 0x7365636F);
        WriteToLds(312, 0x00000073);

        // GetExitCodeProcess (Hint: 0, Name: GetExitCodeProcess)
        WriteToLds(320, 0x65470000); // \0\0Ge
        WriteToLds(324, 0x69784574); // tExi
        WriteToLds(328, 0x646F4374); // tCod
        WriteToLds(332, 0x6F725065); // ePro
        WriteToLds(336, 0x73736563); // cess
        WriteToLds(340, 0x00000000); // \0\0\0\0

        // Wide string command line S:\subsystem\ss.exe mcp at offset 360
        WriteToLds(360, 0x003A0053); // S:
        WriteToLds(364, 0x0073005C); // \s
        WriteToLds(368, 0x00620075); // ub
        WriteToLds(372, 0x00790073); // sy
        WriteToLds(376, 0x00740073); // st
        WriteToLds(380, 0x006D0065); // em
        WriteToLds(384, 0x0073005C); // \s
        WriteToLds(388, 0x002E0073); // s.
        WriteToLds(392, 0x00780065); // ex
        WriteToLds(396, 0x00200065); // e 
        WriteToLds(400, 0x0063006D); // mc
        WriteToLds(404, 0x00000070); // p\0

        // Wide string CWD S:\subsystem at offset 416
        WriteToLds(416, 0x003A0053); // S:
        WriteToLds(420, 0x0073005C); // \s
        WriteToLds(424, 0x00620075); // ub
        WriteToLds(428, 0x00790073); // sy
        WriteToLds(432, 0x00740073); // st
        WriteToLds(436, 0x006D0065); // em
        WriteToLds(440, 0x00000000); // \0
    }
    GroupMemoryBarrierWithGroupSync();
    if (tid < 32) {
        g_PeOutput[O_RdataSection / 16 + tid] = uint4(
            lds_scratch[tid * 4 + 0], lds_scratch[tid * 4 + 1],
            lds_scratch[tid * 4 + 2], lds_scratch[tid * 4 + 3]
        );
    }
}

void GenerateCode(uint tid) {
    if (tid < TotalMethods) {
        CompileUnit unit = g_IrInput[tid];
        uint codeOffset = O_TextSection + unit.RvaOffset;

        for (uint i = 0; i < unit.OpcodeCount; i++) {
            uint val = g_Bytecodes[unit.OpcodeOffset + i];
            uint instrOffset = codeOffset + i * 4;
            uint iw = instrOffset / 16;
            uint ir = (instrOffset % 16) / 4;
            uint ishift = (instrOffset % 4) * 8;
            InterlockedOr(g_PeOutput[iw][ir], val << ishift);
        }
    }
}

[RootSignature(VomRS)]
[numthreads(GROUP_SIZE, 1, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID, uint3 groupThreadId : SV_GroupThreadID, uint3 groupId : SV_GroupID) {
    uint tid = groupThreadId.x;
    uint gid = groupId.x;
    if (gid == 0) { AssemblePeHeaders(tid); }
    else if (gid == 1) { AssembleImportTable(tid); }
    else { GenerateCode((gid - 2) * GROUP_SIZE + tid); }
}
";

                Console.WriteLine("Compiling shader via d3dcompiler_47.dll...");
                IntPtr pShaderBlob = IntPtr.Zero;
                IntPtr pErrorBlob = IntPtr.Zero;
                hr = D3DCompile(hlslCode, (IntPtr)hlslCode.Length, "VomCompiler.hlsl", IntPtr.Zero, IntPtr.Zero, "CSMain", "cs_5_1", 0, 0, out pShaderBlob, out pErrorBlob);
                if (hr != 0)
                {
                    if (pErrorBlob != IntPtr.Zero)
                    {
                        GetBufferPointerDelegate getBufferPointer = (GetBufferPointerDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pErrorBlob, 3), typeof(GetBufferPointerDelegate));
                        IntPtr errorPtr = getBufferPointer(pErrorBlob);
                        string errors = Marshal.PtrToStringAnsi(errorPtr);
                        Console.WriteLine("Shader compilation error:\n" + errors);
                        Marshal.Release(pErrorBlob);
                    }
                    else
                    {
                        Console.WriteLine("Shader compilation failed with HRESULT 0x{0:X8} (no error log)", hr);
                    }
                    return;
                }
                Console.WriteLine("Shader compiled successfully.");
                Vom.RegisterNative(owner, "ID3DBlob_Shader", pShaderBlob, new Action(() => { if (pShaderBlob != IntPtr.Zero) Marshal.Release(pShaderBlob); }), 0, VomFormat.Bytes, "D3D12", "ShaderBlob");

                GetBufferPointerDelegate getShaderBuffer = (GetBufferPointerDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pShaderBlob, 3), typeof(GetBufferPointerDelegate));
                GetBufferSizeDelegate getShaderSize = (GetBufferSizeDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pShaderBlob, 4), typeof(GetBufferSizeDelegate));
                IntPtr bytecodePtr = getShaderBuffer(pShaderBlob);
                IntPtr bytecodeSize = getShaderSize(pShaderBlob);

                Console.WriteLine("Creating root signature from compute shader bytecode...");
                IntPtr pRootSignature = IntPtr.Zero;
                byte[] rootSigIid = new Guid("c54a6b66-72df-4ee8-8be5-a946a1429214").ToByteArray();
                
                // FIXED vtable offset: CreateRootSignature is slot 16 (was 17)
                CreateRootSignatureDelegate createRootSignature = (CreateRootSignatureDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pDevice, 16), typeof(CreateRootSignatureDelegate));
                hr = createRootSignature(pDevice, 0, bytecodePtr, bytecodeSize, rootSigIid, out pRootSignature);
                if (hr != 0)
                {
                    Console.WriteLine("FAIL: CreateRootSignature failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12RootSignature", pRootSignature, new Action(() => { if (pRootSignature != IntPtr.Zero) Marshal.Release(pRootSignature); }), 0, VomFormat.Bytes, "D3D12", "RootSignature");

                Console.WriteLine("Creating compute pipeline state...");
                D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = new D3D12_COMPUTE_PIPELINE_STATE_DESC
                {
                    pRootSignature = pRootSignature,
                    CS = new D3D12_SHADER_BYTECODE { pShaderBytecode = bytecodePtr, BytecodeLength = bytecodeSize },
                    NodeMask = 0,
                    CachedPSO = new D3D12_CACHED_PIPELINE_STATE { pCachedBlob = IntPtr.Zero, CachedBlobSizeInBytes = IntPtr.Zero },
                    Flags = 0
                };

                IntPtr pPipelineState = IntPtr.Zero;
                byte[] psoIid = new Guid("765a30f3-f624-4c6f-a828-ace948622445").ToByteArray();
                
                // FIXED vtable offset: CreateComputePipelineState is slot 11 (was 12)
                CreateComputePipelineStateDelegate createComputePipelineState = (CreateComputePipelineStateDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pDevice, 11), typeof(CreateComputePipelineStateDelegate));
                
                hr = createComputePipelineState(pDevice, ref psoDesc, psoIid, out pPipelineState);
                if (hr != 0)
                {
                    Console.WriteLine("FAIL: CreateComputePipelineState failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12PipelineState", pPipelineState, new Action(() => { if (pPipelineState != IntPtr.Zero) Marshal.Release(pPipelineState); }), 0, VomFormat.Bytes, "D3D12", "PipelineState");

                Console.WriteLine("Creating command queue and list...");
                D3D12_COMMAND_QUEUE_DESC queueDesc = new D3D12_COMMAND_QUEUE_DESC { Type = 2, Priority = 0, Flags = 0, NodeMask = 0 }; // Type 2 = Compute
                byte[] queueIid = new Guid("0ec870a6-5d7e-4c22-8cfc-5baae07616ed").ToByteArray();
                IntPtr pCommandQueue = IntPtr.Zero;
                
                // FIXED vtable offset: CreateCommandQueue is slot 8 (was 9)
                CreateCommandQueueDelegate createCommandQueue = (CreateCommandQueueDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pDevice, 8), typeof(CreateCommandQueueDelegate));
                hr = createCommandQueue(pDevice, ref queueDesc, queueIid, out pCommandQueue);
                if (hr != 0)
                {
                    Console.WriteLine("FAIL: CreateCommandQueue failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12CommandQueue", pCommandQueue, new Action(() => { if (pCommandQueue != IntPtr.Zero) Marshal.Release(pCommandQueue); }), 0, VomFormat.Bytes, "D3D12", "CommandQueue");

                byte[] allocatorIid = new Guid("6102dee4-af59-4b09-b999-b44d73f09b24").ToByteArray();
                IntPtr pCommandAllocator = IntPtr.Zero;
                
                // FIXED vtable offset: CreateCommandAllocator is slot 9 (was 10)
                CreateCommandAllocatorDelegate createCommandAllocator = (CreateCommandAllocatorDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pDevice, 9), typeof(CreateCommandAllocatorDelegate));
                hr = createCommandAllocator(pDevice, 2, allocatorIid, out pCommandAllocator); // Type 2 = Compute
                if (hr != 0)
                {
                    Console.WriteLine("FAIL: CreateCommandAllocator failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12CommandAllocator", pCommandAllocator, new Action(() => { if (pCommandAllocator != IntPtr.Zero) Marshal.Release(pCommandAllocator); }), 0, VomFormat.Bytes, "D3D12", "CommandAllocator");

                byte[] commandListIid = new Guid("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455").ToByteArray();
                IntPtr pCommandList = IntPtr.Zero;
                
                // FIXED vtable offset: CreateCommandList is slot 12 (was 13)
                CreateCommandListDelegate createCommandList = (CreateCommandListDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pDevice, 12), typeof(CreateCommandListDelegate));
                hr = createCommandList(pDevice, 0, 2, pCommandAllocator, pPipelineState, commandListIid, out pCommandList);
                if (hr != 0)
                {
                    Console.WriteLine("FAIL: CreateCommandList failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12GraphicsCommandList", pCommandList, new Action(() => { if (pCommandList != IntPtr.Zero) Marshal.Release(pCommandList); }), 0, VomFormat.Bytes, "D3D12", "CommandList");

                CommandListCloseDelegate cmdClose = (CommandListCloseDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 9), typeof(CommandListCloseDelegate));
                CommandListResetDelegate cmdReset = (CommandListResetDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 10), typeof(CommandListResetDelegate));
                SetComputeRootSignatureDelegate cmdSetSignature = (SetComputeRootSignatureDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 29), typeof(SetComputeRootSignatureDelegate));
                SetComputeRootUnorderedAccessViewDelegate cmdSetUav = (SetComputeRootUnorderedAccessViewDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 41), typeof(SetComputeRootUnorderedAccessViewDelegate));
                SetComputeRootShaderResourceViewDelegate cmdSetSrv = (SetComputeRootShaderResourceViewDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 39), typeof(SetComputeRootShaderResourceViewDelegate));
                SetComputeRoot32BitConstantsDelegate cmdSetConstants = (SetComputeRoot32BitConstantsDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 35), typeof(SetComputeRoot32BitConstantsDelegate));
                SetPipelineStateDelegate cmdSetPso = (SetPipelineStateDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 25), typeof(SetPipelineStateDelegate));
                DispatchDelegate cmdDispatch = (DispatchDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 14), typeof(DispatchDelegate));
                CopyBufferRegionDelegate cmdCopy = (CopyBufferRegionDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandList, 15), typeof(CopyBufferRegionDelegate));

                Console.WriteLine("Preparing buffer payloads...");
                int peSize = 4096;

                byte[] codeBytes = new byte[] {
                    0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xE4, 0xF0, 
                    0x48, 0x81, 0xEC, 0x00, 0x01, 0x00, 0x00, 0x48, 
                    0x8D, 0x7C, 0x24, 0x50, 0x31, 0xC0, 0xB9, 0x11, 
                    0x00, 0x00, 0x00, 0xF3, 0x48, 0xAB, 0xC7, 0x44, 
                    0x24, 0x50, 0x68, 0x00, 0x00, 0x00, 0xC7, 0x84, 
                    0x24, 0x8C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 
                    0x00, 0xB9, 0xF6, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 
                    0x28, 0x02, 0x00, 0x00, 0x48, 0x89, 0x84, 0x24, 
                    0xA0, 0x00, 0x00, 0x00, 0xB9, 0xF5, 0xFF, 0xFF, 
                    0xFF, 0xFF, 0x15, 0x15, 0x02, 0x00, 0x00, 0x48, 
                    0x89, 0x84, 0x24, 0xA8, 0x00, 0x00, 0x00, 0xB9, 
                    0xF4, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 0x02, 0x02, 
                    0x00, 0x00, 0x48, 0x89, 0x84, 0x24, 0xB0, 0x00, 
                    0x00, 0x00, 0x48, 0x31, 0xC9, 0x48, 0x8D, 0x15, 
                    0xF4, 0x02, 0x00, 0x00, 0x45, 0x31, 0xC0, 0x45, 
                    0x31, 0xC9, 0xC7, 0x44, 0x24, 0x20, 0x01, 0x00, 
                    0x00, 0x00, 0xC7, 0x44, 0x24, 0x28, 0x00, 0x00, 
                    0x00, 0x00, 0x48, 0xC7, 0x44, 0x24, 0x30, 0x00, 
                    0x00, 0x00, 0x00, 0x48, 0x8D, 0x05, 0x06, 0x03, 
                    0x00, 0x00, 0x48, 0x89, 0x44, 0x24, 0x38, 0x48, 
                    0x8D, 0x44, 0x24, 0x50, 0x48, 0x89, 0x44, 0x24, 
                    0x40, 0x48, 0x8D, 0x84, 0x24, 0xC0, 0x00, 0x00, 
                    0x00, 0x48, 0x89, 0x44, 0x24, 0x48, 0xFF, 0x15, 
                    0xB0, 0x01, 0x00, 0x00, 0x85, 0xC0, 0x75, 0x0E, 
                    0x65, 0x8B, 0x0C, 0x25, 0x68, 0x00, 0x00, 0x00, 
                    0xFF, 0x15, 0xB6, 0x01, 0x00, 0x00, 0x48, 0x8B, 
                    0x8C, 0x24, 0xC0, 0x00, 0x00, 0x00, 0x48, 0xC7, 
                    0xC2, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x15, 0x91, 
                    0x01, 0x00, 0x00, 0x48, 0x8B, 0x8C, 0x24, 0xC0, 
                    0x00, 0x00, 0x00, 0x48, 0x8D, 0x54, 0x24, 0x30, 
                    0xFF, 0x15, 0x96, 0x01, 0x00, 0x00, 0x48, 0x8B, 
                    0x8C, 0x24, 0xC8, 0x00, 0x00, 0x00, 0xFF, 0x15, 
                    0x78, 0x01, 0x00, 0x00, 0x48, 0x8B, 0x8C, 0x24, 
                    0xC0, 0x00, 0x00, 0x00, 0xFF, 0x15, 0x6A, 0x01, 
                    0x00, 0x00, 0x8B, 0x4C, 0x24, 0x30, 0xFF, 0x15, 
                    0x68, 0x01, 0x00, 0x00, 0xC3
                };

                uint[] bytecodes = new uint[(codeBytes.Length + 3) / 4];
                Buffer.BlockCopy(codeBytes, 0, bytecodes, 0, codeBytes.Length);

                CompileUnit[] irData = new CompileUnit[1];
                irData[0] = new CompileUnit
                {
                    FuncIndex = 0,
                    OpcodeCount = (uint)bytecodes.Length,
                    OpcodeOffset = 0,
                    RvaOffset = 0
                };

                // VOM-shaped Memory Allocations
                Handle irRegion = Vom.Alloc(owner, irData.Length * sizeof(CompileUnit), VomFormat.Bytes, "CompileUnitData", false, "Buffers", "IrInput");
                fixed (CompileUnit* pIrSrc = irData)
                {
                    Buffer.MemoryCopy(pIrSrc, (void*)irRegion.Resource, irRegion.ByteCount, irData.Length * sizeof(CompileUnit));
                }

                Handle bcRegion = Vom.Alloc(owner, bytecodes.Length * 4, VomFormat.Bytes, "BytecodeData", false, "Buffers", "Bytecodes");
                fixed (uint* pBcSrc = bytecodes)
                {
                    Buffer.MemoryCopy(pBcSrc, (void*)bcRegion.Resource, bcRegion.ByteCount, bytecodes.Length * 4);
                }

                // FIXED vtable offset: CreateCommittedResource is slot 27 (correct)
                CreateCommittedResourceDelegate createCommittedResource = (CreateCommittedResourceDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pDevice, 27), typeof(CreateCommittedResourceDelegate));

                D3D12_HEAP_PROPERTIES uploadHeapProps = new D3D12_HEAP_PROPERTIES { Type = 2, CPUPageProperty = 0, MemoryPoolPreference = 0 }; // Type 2 = Upload
                D3D12_HEAP_PROPERTIES defaultHeapProps = new D3D12_HEAP_PROPERTIES { Type = 1, CPUPageProperty = 0, MemoryPoolPreference = 0 }; // Type 1 = Default
                D3D12_HEAP_PROPERTIES readbackHeapProps = new D3D12_HEAP_PROPERTIES { Type = 3, CPUPageProperty = 0, MemoryPoolPreference = 0 }; // Type 3 = Readback

                D3D12_RESOURCE_DESC bufferDesc = new D3D12_RESOURCE_DESC
                {
                    Dimension = 1,
                    Alignment = 0,
                    Width = (ulong)peSize,
                    Height = 1,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = 0,
                    SampleCount = 1,
                    SampleQuality = 0,
                    Layout = 1,
                    Flags = 0
                };

                byte[] resourceIid = new Guid("696442be-a72e-4059-bc79-5b5c98040fad").ToByteArray();

                // Input IR D3DResource
                IntPtr pIrUpload = IntPtr.Zero;
                D3D12_RESOURCE_DESC irUploadDesc = bufferDesc; irUploadDesc.Width = (ulong)(irData.Length * sizeof(CompileUnit));
                hr = createCommittedResource(pDevice, ref uploadHeapProps, 0, ref irUploadDesc, 2755, IntPtr.Zero, resourceIid, out pIrUpload); // 2755 = GENERIC_READ
                if (hr != 0 || pIrUpload == IntPtr.Zero)
                {
                    Console.WriteLine("FAIL: CreateCommittedResource for IR upload failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12Resource_IrUpload", pIrUpload, new Action(() => { if (pIrUpload != IntPtr.Zero) Marshal.Release(pIrUpload); }), 0, VomFormat.Bytes, "D3D12", "IrUpload");

                MapResourceDelegate mapResource = (MapResourceDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pIrUpload, 8), typeof(MapResourceDelegate));
                UnmapResourceDelegate unmapResource = (UnmapResourceDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pIrUpload, 9), typeof(UnmapResourceDelegate));
                
                void* pCpuIr;
                D3D12_RANGE readRange = new D3D12_RANGE { Begin = IntPtr.Zero, End = IntPtr.Zero };
                mapResource(pIrUpload, 0, ref readRange, out pCpuIr);
                Buffer.MemoryCopy((void*)irRegion.Resource, pCpuIr, irUploadDesc.Width, irUploadDesc.Width);
                unmapResource(pIrUpload, 0, IntPtr.Zero);

                // Bytecode D3DResource
                IntPtr pBytecodeUpload = IntPtr.Zero;
                D3D12_RESOURCE_DESC bcUploadDesc = bufferDesc; bcUploadDesc.Width = (ulong)(bytecodes.Length * 4);
                hr = createCommittedResource(pDevice, ref uploadHeapProps, 0, ref bcUploadDesc, 2755, IntPtr.Zero, resourceIid, out pBytecodeUpload);
                if (hr != 0 || pBytecodeUpload == IntPtr.Zero)
                {
                    Console.WriteLine("FAIL: CreateCommittedResource for Bytecode upload failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12Resource_BcUpload", pBytecodeUpload, new Action(() => { if (pBytecodeUpload != IntPtr.Zero) Marshal.Release(pBytecodeUpload); }), 0, VomFormat.Bytes, "D3D12", "BytecodeUpload");

                void* pCpuBc;
                mapResource(pBytecodeUpload, 0, ref readRange, out pCpuBc);
                Buffer.MemoryCopy((void*)bcRegion.Resource, pCpuBc, bcUploadDesc.Width, bcUploadDesc.Width);
                unmapResource(pBytecodeUpload, 0, IntPtr.Zero);

                // PE Output Buffer
                IntPtr pPeOutput = IntPtr.Zero;
                D3D12_RESOURCE_DESC peDesc = bufferDesc;
                peDesc.Flags = 4; // ALLOW_UNORDERED_ACCESS
                hr = createCommittedResource(pDevice, ref defaultHeapProps, 0, ref peDesc, 0, IntPtr.Zero, resourceIid, out pPeOutput); // 0 = COMMON
                if (hr != 0 || pPeOutput == IntPtr.Zero)
                {
                    Console.WriteLine("FAIL: CreateCommittedResource for PE Output failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12Resource_PeOutput", pPeOutput, new Action(() => { if (pPeOutput != IntPtr.Zero) Marshal.Release(pPeOutput); }), 0, VomFormat.Bytes, "D3D12", "PeOutput");

                // PE Readback Buffer
                IntPtr pPeReadback = IntPtr.Zero;
                D3D12_RESOURCE_DESC readbackDesc = bufferDesc;
                hr = createCommittedResource(pDevice, ref readbackHeapProps, 0, ref readbackDesc, 1024, IntPtr.Zero, resourceIid, out pPeReadback); // 1024 = COPY_DEST
                if (hr != 0 || pPeReadback == IntPtr.Zero)
                {
                    Console.WriteLine("FAIL: CreateCommittedResource for PE Readback failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12Resource_PeReadback", pPeReadback, new Action(() => { if (pPeReadback != IntPtr.Zero) Marshal.Release(pPeReadback); }), 0, VomFormat.Bytes, "D3D12", "PeReadback");

                // Get GPU virtual addresses
                GetGPUVirtualAddressDelegate getGpuAddress = (GetGPUVirtualAddressDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pIrUpload, 11), typeof(GetGPUVirtualAddressDelegate)); // slot 11
                long irGpuAddr = getGpuAddress(pIrUpload);
                long bcGpuAddr = getGpuAddress(pBytecodeUpload);
                long peGpuAddr = getGpuAddress(pPeOutput);

                Console.WriteLine("Dispatching compute shader grid...");
                cmdSetSignature(pCommandList, pRootSignature);
                cmdSetPso(pCommandList, pPipelineState);

                LayoutConfig config = new LayoutConfig
                {
                    O_DosHeader = 0,
                    O_TextSection = 512,
                    O_RdataSection = 1024,
                    TotalMethods = 1
                };
                cmdSetConstants(pCommandList, 0, 4, &config, 0);

                cmdSetSrv(pCommandList, 1, irGpuAddr);
                cmdSetSrv(pCommandList, 2, bcGpuAddr);
                cmdSetUav(pCommandList, 3, peGpuAddr);

                cmdDispatch(pCommandList, 3, 1, 1);

                cmdCopy(pCommandList, pPeReadback, 0, pPeOutput, 0, (ulong)peSize);
                cmdClose(pCommandList);

                Console.WriteLine("Executing on queue...");
                IntPtr* pLists = stackalloc IntPtr[1];
                pLists[0] = pCommandList;
                ExecuteCommandListsDelegate executeCommandLists = (ExecuteCommandListsDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandQueue, 10), typeof(ExecuteCommandListsDelegate));
                executeCommandLists(pCommandQueue, 1, pLists);

                // Synchronize using D3D12 Fence
                IntPtr pFence = IntPtr.Zero;
                byte[] fenceIid = new Guid("0a753dcf-c4d8-4b91-adf6-be5a60d95a76").ToByteArray();
                
                // FIXED vtable offset: CreateFence is slot 36 (was 37)
                CreateFenceDelegate createFence = (CreateFenceDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pDevice, 36), typeof(CreateFenceDelegate));
                hr = createFence(pDevice, 0, 0, fenceIid, out pFence);
                if (hr != 0 || pFence == IntPtr.Zero)
                {
                    Console.WriteLine("FAIL: CreateFence failed (HRESULT 0x{0:X8})", hr);
                    return;
                }
                Vom.RegisterNative(owner, "ID3D12Fence", pFence, new Action(() => { if (pFence != IntPtr.Zero) Marshal.Release(pFence); }), 0, VomFormat.Bytes, "D3D12", "Fence");

                QueueSignalDelegate queueSignal = (QueueSignalDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pCommandQueue, 14), typeof(QueueSignalDelegate));
                hr = queueSignal(pCommandQueue, pFence, 1);
                if (hr != 0)
                {
                    Console.WriteLine("FAIL: QueueSignal failed (HRESULT 0x{0:X8})", hr);
                    return;
                }

                GetCompletedValueDelegate getCompletedValue = (GetCompletedValueDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pFence, 8), typeof(GetCompletedValueDelegate));

                // Node-Local Hardware Fence Wait
                while (getCompletedValue(pFence) < 1)
                {
                    System.Threading.Thread.Sleep(1);
                }
                Console.WriteLine("GPU Execution completed and synchronized.");

                Console.WriteLine("Reading compiled PE bytes...");
                MapResourceDelegate mapReadback = (MapResourceDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pPeReadback, 8), typeof(MapResourceDelegate));
                UnmapResourceDelegate unmapReadback = (UnmapResourceDelegate)Marshal.GetDelegateForFunctionPointer(GetVTableSlot(pPeReadback, 9), typeof(UnmapResourceDelegate));

                void* pCpuData;
                D3D12_RANGE readRangePe = new D3D12_RANGE { Begin = IntPtr.Zero, End = (IntPtr)peSize };
                hr = mapReadback(pPeReadback, 0, ref readRangePe, out pCpuData);
                if (hr != 0)
                {
                    Console.WriteLine("FAIL: Map resource for readback failed (HRESULT 0x{0:X8})", hr);
                    return;
                }

                byte[] finalPeBytes = new byte[peSize];
                Marshal.Copy((IntPtr)pCpuData, finalPeBytes, 0, peSize);
                unmapReadback(pPeReadback, 0, IntPtr.Zero);

                File.WriteAllBytes(outputExePath, finalPeBytes);
                Console.WriteLine("SUCCESS: Compiled native PE binary written to: " + outputExePath);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unexpected compiler exception: " + e.ToString());
            }
            finally
            {
                Console.WriteLine("Terminating VOM owner, current bytes: {0}", owner.CurrentBytes);
                Vom.Terminate(owner);
                Console.WriteLine("VOM owner terminated. Live elements: {0}", owner.CurrentElements);
            }
        }
    }
}
"@

# 3. Compile the C# harness using Add-Type
Write-Host "Compiling compiler harness in memory..."
Add-Type -TypeDefinition $csharpCode -Language CSharp -CompilerParameters @{
    CompilerOptions = "/unsafe /platform:x64"
}

# 4. Execute the compiler to generate sssd.exe
$targetExe = Join-Path $root "sssd.exe"
Write-Host "Executing GPU Compilation pass..."
[Subsystem.Vom.VomGpuCompiler]::Compile($targetExe)

if (Test-Path $targetExe) {
    Write-Host "SUCCESS: Native PE executable compiled via GPU -> $targetExe"
    $fileInfo = Get-Item $targetExe
    Write-Host "Compiled size: $($fileInfo.Length) bytes"
} else {
    throw "GPU Compiler executed but target sssd.exe was not created."
}
