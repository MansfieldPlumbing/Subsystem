// GpuD3D12.cs — pure-C# D3D12 compute dispatch for dp-onnx's GPU seam. NO dpgpu.dll, NO C++, NO MSVC.
// Drives D3D12 by hand-rolled COM vtable interop + a serialized root sig; reuses the caller's gemm DXIL.
// Reproduces dpgpu.cpp's dpgpu_gemm bit-exactly (verified 0-diff vs CPU under net11/CoreCLR). The whole
// engine is now managed + shader bytecode -> sovereign; the same shape ports to Vulkan (vulkan-1.dll / SPIR-V).
// Persistent device/queue/root-sig/PSO/fence; per-call buffers released. MakeDevice = beefiest-by-VRAM
// (V340 over P2000) with DPGPU_ADAPTER override, parity with dpgpu.cpp.
using System.Runtime.InteropServices;

namespace Subsystem.Dpx;

unsafe static class GpuD3D12
{
    [DllImport("d3d12.dll", CallingConvention = CallingConvention.StdCall)] static extern int D3D12CreateDevice(IntPtr a, uint fl, byte[] riid, out IntPtr dev);
    [DllImport("d3d12.dll", CallingConvention = CallingConvention.StdCall)] static extern int D3D12SerializeRootSignature(ref ROOTSIGDESC d, int ver, out IntPtr blob, out IntPtr err);
    [DllImport("dxgi.dll",  CallingConvention = CallingConvention.StdCall)] static extern int CreateDXGIFactory1(byte[] riid, out IntPtr factory);

    static IntPtr Slot(IntPtr o, int i) { IntPtr vt = Marshal.ReadIntPtr(o); return Marshal.ReadIntPtr(vt, i * IntPtr.Size); }
    static T Fn<T>(IntPtr o, int s) { return (T)(object)Marshal.GetDelegateForFunctionPointer(Slot(o, s), typeof(T)); }
    static byte[] G(string g) { return new Guid(g).ToByteArray(); }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DEnum(IntPtr f, uint i, out IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DDesc(IntPtr a, out ADESC d);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DCreateRS(IntPtr d, uint nm, IntPtr blob, IntPtr len, byte[] r, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DCreatePSO(IntPtr d, ref CPSO desc, byte[] r, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DCreateQueue(IntPtr d, ref QDESC q, byte[] r, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DCreateAlloc(IntPtr d, int t, byte[] r, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DCreateList(IntPtr d, uint nm, int t, IntPtr a, IntPtr p, byte[] r, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DCreateRes(IntPtr d, ref HEAP h, int hf, ref RDESC rd, int st, IntPtr cv, byte[] r, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DCreateFence(IntPtr d, ulong v, int f, byte[] r, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DClose(IntPtr l);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DSetRS(IntPtr l, IntPtr rs);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DSetPSO(IntPtr l, IntPtr pso);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DSet32(IntPtr l, uint idx, uint num, void* data, uint off);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DSetSRV(IntPtr l, uint idx, long va);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DSetUAV(IntPtr l, uint idx, long va);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DDispatch(IntPtr l, uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DBarrier(IntPtr l, uint n, ref BAR b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DCopy(IntPtr l, IntPtr dst, ulong doff, IntPtr src, ulong soff, ulong n);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DExec(IntPtr q, uint n, IntPtr* lists);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DSignal(IntPtr q, IntPtr f, ulong v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  DMap(IntPtr r, uint s, ref RANGE rr, out void* p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DUnmap(IntPtr r, uint s, IntPtr w);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate long DGpuVA(IntPtr r);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate ulong DGetCompl(IntPtr f);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr DBlobPtr(IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr DBlobSize(IntPtr b);

    [StructLayout(LayoutKind.Explicit, Size = 32)] struct ROOTPARAM { [FieldOffset(0)] public uint ParameterType; [FieldOffset(8)] public uint ShaderRegister; [FieldOffset(12)] public uint RegisterSpace; [FieldOffset(16)] public uint Num32BitValues; [FieldOffset(24)] public uint ShaderVisibility; }
    [StructLayout(LayoutKind.Sequential)] struct ROOTSIGDESC { public uint NumParameters; public IntPtr pParameters; public uint NumStaticSamplers; public IntPtr pStaticSamplers; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct QDESC { public int Type; public int Priority; public int Flags; public uint NodeMask; }
    [StructLayout(LayoutKind.Sequential)] struct HEAP  { public int Type; public int Cpu; public int Pool; public uint CMask; public uint VMask; }
    [StructLayout(LayoutKind.Sequential)] struct RDESC { public int Dim; public long Align; public ulong Width; public uint Height; public ushort Depth; public ushort Mip; public int Fmt; public int SCount; public int SQual; public int Layout; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] struct RANGE { public IntPtr Begin; public IntPtr End; }
    [StructLayout(LayoutKind.Sequential)] struct BC    { public IntPtr p; public IntPtr len; }
    [StructLayout(LayoutKind.Sequential)] struct CACHE { public IntPtr p; public IntPtr len; }
    [StructLayout(LayoutKind.Sequential)] struct CPSO  { public IntPtr RS; public BC CS; public uint NodeMask; public CACHE Cached; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] struct BAR   { public int Type; public int Flags; public IntPtr Res; public uint Sub; public int Before; public int After; }
    [StructLayout(LayoutKind.Sequential)] struct LUID  { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ADESC { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; public uint VendorId; public uint DeviceId; public uint SubSysId; public uint Revision; public IntPtr DedVid; public IntPtr DedSys; public IntPtr SharedSys; public LUID Luid; public uint Flags; }

    const string IID_DEV = "189819f1-1db6-4b57-be54-1821339b85f7", IID_FAC = "770aae78-f26f-4dba-a829-253c83d1b387",
                 IID_RS = "c54a6b66-72df-4ee8-8be5-a946a1429214", IID_PSO = "765a30f3-f624-4c6f-a828-ace948622445",
                 IID_Q = "0ec870a6-5d7e-4c22-8cfc-5baae07616ed", IID_ALLOC = "6102dee4-af59-4b09-b999-b44d73f09b24",
                 IID_LIST = "5b160d0f-ac1b-4185-8ba8-b3ae42a5a455", IID_RES = "696442be-a72e-4059-bc79-5b5c98040fad",
                 IID_FENCE = "0a753dcf-c4d8-4b91-adf6-be5a60d95a76";

    static IntPtr s_dev, s_q, s_root, s_fence, s_pso; static int s_psoLen = -1; static ulong s_fv; static string s_name = "(none)";
    public static string Name => s_name;

    // q4 GEMM root sig/PSO — separate from the fp32 GEMM (5 params: RootConstants(5) b0, SRV t0 A, SRV t1 Bq, SRV t2 Scales, SRV t3 Zp, UAV u0 C).
    static IntPtr s_rootQ4, s_psoQ4; static int s_psoQ4Len = -1;

    static IntPtr MakeDevice()
    {
        IntPtr best = IntPtr.Zero; ulong bestVram = 0;
        int forceIdx = -1; var e = Environment.GetEnvironmentVariable("DPGPU_ADAPTER"); if (e != null) int.TryParse(e, out forceIdx);
        IntPtr fac;
        if (CreateDXGIFactory1(G(IID_FAC), out fac) == 0 && fac != IntPtr.Zero)
        {
            var en = Fn<DEnum>(fac, 12); uint i = 0; IntPtr ad;
            while (en(fac, i, out ad) == 0 && ad != IntPtr.Zero)
            {
                ADESC d; Fn<DDesc>(ad, 10)(ad, out d);
                if ((d.Flags & 2) == 0)
                {
                    IntPtr dv;
                    if (D3D12CreateDevice(ad, 0xc000, G(IID_DEV), out dv) == 0 && dv != IntPtr.Zero)
                    {
                        ulong vram = (ulong)d.DedVid.ToInt64();
                        if (forceIdx >= 0) { if ((int)i == forceIdx) { best = dv; s_name = d.Description; Marshal.Release(ad); break; } Marshal.Release(dv); }
                        else if (best == IntPtr.Zero || vram > bestVram) { if (best != IntPtr.Zero) Marshal.Release(best); best = dv; bestVram = vram; s_name = d.Description; }
                        else Marshal.Release(dv);
                    }
                }
                Marshal.Release(ad); i++;
            }
        }
        if (best != IntPtr.Zero) return best;
        IntPtr dev2; D3D12CreateDevice(IntPtr.Zero, 0xc000, G(IID_DEV), out dev2); s_name = "(default)"; return dev2;
    }

    public static void EnsureInit()
    {
        if (s_dev != IntPtr.Zero) return;
        s_dev = MakeDevice();
        QDESC qd = new QDESC { Type = 2 }; Fn<DCreateQueue>(s_dev, 8)(s_dev, ref qd, G(IID_Q), out s_q);
        ROOTPARAM[] rp = new ROOTPARAM[4];
        rp[0].ParameterType = 1; rp[0].Num32BitValues = 3;   // RootConstants(3) @ b0
        rp[1].ParameterType = 3; rp[1].ShaderRegister = 0;   // SRV t0 = A
        rp[2].ParameterType = 3; rp[2].ShaderRegister = 1;   // SRV t1 = B
        rp[3].ParameterType = 4; rp[3].ShaderRegister = 0;   // UAV u0 = C
        int psz = Marshal.SizeOf(typeof(ROOTPARAM)); IntPtr pa = Marshal.AllocHGlobal(psz * 4);
        for (int i = 0; i < 4; i++) Marshal.StructureToPtr(rp[i], (IntPtr)((byte*)pa + i * psz), false);
        ROOTSIGDESC rsd = new ROOTSIGDESC { NumParameters = 4, pParameters = pa };
        IntPtr blob, err; D3D12SerializeRootSignature(ref rsd, 1, out blob, out err);
        IntPtr bp = Fn<DBlobPtr>(blob, 3)(blob), bl = Fn<DBlobSize>(blob, 4)(blob);
        Fn<DCreateRS>(s_dev, 16)(s_dev, 0, bp, bl, G(IID_RS), out s_root);
        Fn<DCreateFence>(s_dev, 36)(s_dev, 0, 0, G(IID_FENCE), out s_fence);

        // q4 root sig: RootConstants(5) b0 {M,N,K,BlockSize,HasZp}; SRV t0=A, t1=Bq, t2=Scales, t3=Zp; UAV u0=C
        ROOTPARAM[] rq = new ROOTPARAM[6];
        rq[0].ParameterType = 1; rq[0].Num32BitValues = 5;
        rq[1].ParameterType = 3; rq[1].ShaderRegister = 0;   // SRV t0 = A
        rq[2].ParameterType = 3; rq[2].ShaderRegister = 1;   // SRV t1 = Bq
        rq[3].ParameterType = 3; rq[3].ShaderRegister = 2;   // SRV t2 = Scales
        rq[4].ParameterType = 3; rq[4].ShaderRegister = 3;   // SRV t3 = Zp
        rq[5].ParameterType = 4; rq[5].ShaderRegister = 0;   // UAV u0 = C
        int pszQ = Marshal.SizeOf(typeof(ROOTPARAM)); IntPtr paQ = Marshal.AllocHGlobal(pszQ * 6);
        for (int i = 0; i < 6; i++) Marshal.StructureToPtr(rq[i], (IntPtr)((byte*)paQ + i * pszQ), false);
        ROOTSIGDESC rsdQ = new ROOTSIGDESC { NumParameters = 6, pParameters = paQ };
        IntPtr blobQ, errQ; D3D12SerializeRootSignature(ref rsdQ, 1, out blobQ, out errQ);
        IntPtr bpQ = Fn<DBlobPtr>(blobQ, 3)(blobQ), blQ = Fn<DBlobSize>(blobQ, 4)(blobQ);
        Fn<DCreateRS>(s_dev, 16)(s_dev, 0, bpQ, blQ, G(IID_RS), out s_rootQ4);
    }

    static IntPtr MkBuf(byte[] resIID, int heap, ulong bytes, int state, int flags)
    {
        HEAP hp = new HEAP { Type = heap };
        RDESC rd = new RDESC { Dim = 1, Width = bytes, Height = 1, Depth = 1, Mip = 1, Layout = 1, Flags = flags, SCount = 1 };
        IntPtr b; Fn<DCreateRes>(s_dev, 27)(s_dev, ref hp, 0, ref rd, state, IntPtr.Zero, resIID, out b); return b;
    }

    public static int Gemm(float[] A, float[] B, float[] C, uint M, uint N, uint K, byte[] dxil, int dxilLen)
    {
        EnsureInit();
        if (s_pso == IntPtr.Zero || s_psoLen != dxilLen)
        {
            IntPtr dp = Marshal.AllocHGlobal(dxilLen); Marshal.Copy(dxil, 0, dp, dxilLen);
            CPSO pd = new CPSO { RS = s_root, CS = new BC { p = dp, len = (IntPtr)dxilLen } };
            if (Fn<DCreatePSO>(s_dev, 11)(s_dev, ref pd, G(IID_PSO), out s_pso) != 0) return -6;
            s_psoLen = dxilLen;
        }
        byte[] resIID = G(IID_RES);
        IntPtr alloc; Fn<DCreateAlloc>(s_dev, 9)(s_dev, 2, G(IID_ALLOC), out alloc);
        IntPtr list;  Fn<DCreateList>(s_dev, 12)(s_dev, 0, 2, alloc, s_pso, G(IID_LIST), out list);
        ulong aB = (ulong)M * K * 4, bB = (ulong)K * N * 4, cB = (ulong)M * N * 4;
        IntPtr aBuf = MkBuf(resIID, 2, aB, 2755, 0);   // UPLOAD, GENERIC_READ
        IntPtr bBuf = MkBuf(resIID, 2, bB, 2755, 0);
        IntPtr cBuf = MkBuf(resIID, 1, cB, 8, 4);      // DEFAULT, UNORDERED_ACCESS, ALLOW_UAV
        IntPtr rbBuf = MkBuf(resIID, 3, cB, 1024, 0);  // READBACK, COPY_DEST
        RANGE z = new RANGE(); void* p;
        Fn<DMap>(aBuf, 8)(aBuf, 0, ref z, out p); Marshal.Copy(A, 0, (IntPtr)p, (int)(M * K)); Fn<DUnmap>(aBuf, 9)(aBuf, 0, IntPtr.Zero);
        Fn<DMap>(bBuf, 8)(bBuf, 0, ref z, out p); Marshal.Copy(B, 0, (IntPtr)p, (int)(K * N)); Fn<DUnmap>(bBuf, 9)(bBuf, 0, IntPtr.Zero);
        long aVA = Fn<DGpuVA>(aBuf, 11)(aBuf), bVA = Fn<DGpuVA>(bBuf, 11)(bBuf), cVA = Fn<DGpuVA>(cBuf, 11)(cBuf);
        Fn<DSetPSO>(list, 25)(list, s_pso); Fn<DSetRS>(list, 29)(list, s_root);
        uint* gc = stackalloc uint[3]; gc[0] = M; gc[1] = N; gc[2] = K; Fn<DSet32>(list, 35)(list, 0, 3, gc, 0);
        Fn<DSetSRV>(list, 39)(list, 1, aVA); Fn<DSetSRV>(list, 39)(list, 2, bVA); Fn<DSetUAV>(list, 41)(list, 3, cVA);
        Fn<DDispatch>(list, 14)(list, (N + 15) / 16, (M + 15) / 16, 1);
        BAR bar = new BAR { Type = 0, Res = cBuf, Before = 8, After = 2048 }; Fn<DBarrier>(list, 26)(list, 1, ref bar);
        Fn<DCopy>(list, 15)(list, rbBuf, 0, cBuf, 0, cB);
        Fn<DClose>(list, 9)(list);
        IntPtr* lp = stackalloc IntPtr[1]; lp[0] = list; Fn<DExec>(s_q, 10)(s_q, 1, lp);
        ulong target = ++s_fv; Fn<DSignal>(s_q, 14)(s_q, s_fence, target);
        var gcv = Fn<DGetCompl>(s_fence, 8); while (gcv(s_fence) < target) System.Threading.Thread.SpinWait(64);
        RANGE rr = new RANGE { End = (IntPtr)(long)cB }; void* rp;
        Fn<DMap>(rbBuf, 8)(rbBuf, 0, ref rr, out rp); Marshal.Copy((IntPtr)rp, C, 0, (int)(M * N)); Fn<DUnmap>(rbBuf, 9)(rbBuf, 0, IntPtr.Zero);
        Marshal.Release(aBuf); Marshal.Release(bBuf); Marshal.Release(cBuf); Marshal.Release(rbBuf);
        Marshal.Release(list); Marshal.Release(alloc);
        return 0;
    }

    // GemmQ4: Y[M,N] = A[M,K] @ dequant(Bq)^T, q4 SEQUENTIAL-nibble packed weight, never expanded to fp32 on CPU.
    // A/Scales fp32, Bq/Zp raw packed uint8 nibbles (Zp may be empty when hasZp=false — dequant defaults zp=8 on GPU).
    public static int GemmQ4(float[] A, byte[] Bq, float[] Scales, byte[] Zp, float[] C, uint M, uint N, uint K, uint blockSize, bool hasZp, byte[] dxil, int dxilLen)
    {
        EnsureInit();
        if (s_psoQ4 == IntPtr.Zero || s_psoQ4Len != dxilLen)
        {
            IntPtr dp = Marshal.AllocHGlobal(dxilLen); Marshal.Copy(dxil, 0, dp, dxilLen);
            CPSO pd = new CPSO { RS = s_rootQ4, CS = new BC { p = dp, len = (IntPtr)dxilLen } };
            if (Fn<DCreatePSO>(s_dev, 11)(s_dev, ref pd, G(IID_PSO), out s_psoQ4) != 0) return -6;
            s_psoQ4Len = dxilLen;
        }
        byte[] resIID = G(IID_RES);
        IntPtr alloc; Fn<DCreateAlloc>(s_dev, 9)(s_dev, 2, G(IID_ALLOC), out alloc);
        IntPtr list;  Fn<DCreateList>(s_dev, 12)(s_dev, 0, 2, alloc, s_psoQ4, G(IID_LIST), out list);
        ulong aB = (ulong)M * K * 4, bqB = (ulong)Bq.Length, scB = (ulong)Scales.Length * 4, cB = (ulong)M * N * 4;
        ulong zpB = (ulong)Math.Max(1, Zp?.Length ?? 1);   // D3D12 disallows zero-size resources; alloc >=1 byte
        IntPtr aBuf  = MkBuf(resIID, 2, aB, 2755, 0);
        IntPtr bqBuf = MkBuf(resIID, 2, bqB, 2755, 0);
        IntPtr scBuf = MkBuf(resIID, 2, scB, 2755, 0);
        IntPtr zpBuf = MkBuf(resIID, 2, zpB, 2755, 0);
        IntPtr cBuf  = MkBuf(resIID, 1, cB, 8, 4);      // DEFAULT, UNORDERED_ACCESS, ALLOW_UAV
        IntPtr rbBuf = MkBuf(resIID, 3, cB, 1024, 0);   // READBACK, COPY_DEST
        RANGE z = new RANGE(); void* p;
        Fn<DMap>(aBuf, 8)(aBuf, 0, ref z, out p); Marshal.Copy(A, 0, (IntPtr)p, A.Length); Fn<DUnmap>(aBuf, 9)(aBuf, 0, IntPtr.Zero);
        Fn<DMap>(bqBuf, 8)(bqBuf, 0, ref z, out p); Marshal.Copy(Bq, 0, (IntPtr)p, Bq.Length); Fn<DUnmap>(bqBuf, 9)(bqBuf, 0, IntPtr.Zero);
        Fn<DMap>(scBuf, 8)(scBuf, 0, ref z, out p); Marshal.Copy(Scales, 0, (IntPtr)p, Scales.Length); Fn<DUnmap>(scBuf, 9)(scBuf, 0, IntPtr.Zero);
        if (hasZp && Zp != null && Zp.Length > 0) { Fn<DMap>(zpBuf, 8)(zpBuf, 0, ref z, out p); Marshal.Copy(Zp, 0, (IntPtr)p, Zp.Length); Fn<DUnmap>(zpBuf, 9)(zpBuf, 0, IntPtr.Zero); }
        long aVA = Fn<DGpuVA>(aBuf, 11)(aBuf), bqVA = Fn<DGpuVA>(bqBuf, 11)(bqBuf), scVA = Fn<DGpuVA>(scBuf, 11)(scBuf), zpVA = Fn<DGpuVA>(zpBuf, 11)(zpBuf), cVA = Fn<DGpuVA>(cBuf, 11)(cBuf);
        Fn<DSetPSO>(list, 25)(list, s_psoQ4); Fn<DSetRS>(list, 29)(list, s_rootQ4);
        uint* gc = stackalloc uint[5]; gc[0] = M; gc[1] = N; gc[2] = K; gc[3] = blockSize; gc[4] = hasZp ? 1u : 0u;
        Fn<DSet32>(list, 35)(list, 0, 5, gc, 0);
        Fn<DSetSRV>(list, 39)(list, 1, aVA); Fn<DSetSRV>(list, 39)(list, 2, bqVA); Fn<DSetSRV>(list, 39)(list, 3, scVA); Fn<DSetSRV>(list, 39)(list, 4, zpVA); Fn<DSetUAV>(list, 41)(list, 5, cVA);
        Fn<DDispatch>(list, 14)(list, (N + 15) / 16, (M + 15) / 16, 1);
        BAR bar = new BAR { Type = 0, Res = cBuf, Before = 8, After = 2048 }; Fn<DBarrier>(list, 26)(list, 1, ref bar);
        Fn<DCopy>(list, 15)(list, rbBuf, 0, cBuf, 0, cB);
        Fn<DClose>(list, 9)(list);
        IntPtr* lp = stackalloc IntPtr[1]; lp[0] = list; Fn<DExec>(s_q, 10)(s_q, 1, lp);
        ulong target = ++s_fv; Fn<DSignal>(s_q, 14)(s_q, s_fence, target);
        var gcv = Fn<DGetCompl>(s_fence, 8); while (gcv(s_fence) < target) System.Threading.Thread.SpinWait(64);
        RANGE rr = new RANGE { End = (IntPtr)(long)cB }; void* rp;
        Fn<DMap>(rbBuf, 8)(rbBuf, 0, ref rr, out rp); Marshal.Copy((IntPtr)rp, C, 0, (int)(M * N)); Fn<DUnmap>(rbBuf, 9)(rbBuf, 0, IntPtr.Zero);
        Marshal.Release(aBuf); Marshal.Release(bqBuf); Marshal.Release(scBuf); Marshal.Release(zpBuf); Marshal.Release(cBuf); Marshal.Release(rbBuf);
        Marshal.Release(list); Marshal.Release(alloc);
        return 0;
    }
}
