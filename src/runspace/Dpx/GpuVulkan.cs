#nullable enable
// GpuVulkan.cs — pure-C# Vulkan GEMM dispatch for dpx. NO C++, NO MSVC. vulkan-1.dll (Windows) / libvulkan.so
// (Android) via flat-C P/Invoke + precompiled SPIR-V. Same naive fp32 GEMM as the D3D12 path, so bit-parity holds.
// Cross-platform spine: this exact code + gemm.spv run on the Adreno/Mali. Persistent instance/device/queue/
// descriptor-layout/pipeline-layout/pipeline; per-call buffers/descriptors/command-buffer/fence destroyed.
using System;
using System.Runtime.InteropServices;

namespace Subsystem.Dpx;

unsafe static class GpuVulkan
{
    const string VK = "vulkan-1.dll";

    // Android has no file named vulkan-1.dll — the loader ships libvulkan.so. Scoped to Android only (the
    // Windows head shares this assembly with LiteRtRuntime's own resolver, which owns litert-lm.dll and
    // falls through for anything else — vulkan-1.dll already resolves there via normal OS probing, so a
    // second resolver on Windows would only risk racing that one for no reason). Same guarded-registration
    // idiom as MainActivity.cs's libpsl resolver (one resolver per assembly per process; OnCreate can rerun).
    static GpuVulkan()
    {
        if (OperatingSystem.IsAndroid())
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(GpuVulkan).Assembly, (name, asm, path) =>
                    name == VK && NativeLibrary.TryLoad("libvulkan.so", asm, path, out var h) ? h : IntPtr.Zero);
            }
            catch (InvalidOperationException ex) { Subsystem.Dg.Log("dpx", $"GpuVulkan resolver already set: {ex.Message}"); }
        }
    }
    [DllImport(VK)] static extern int  vkCreateInstance(ref InstCI ci, IntPtr a, out IntPtr inst);
    [DllImport(VK)] static extern int  vkEnumeratePhysicalDevices(IntPtr inst, ref uint c, [Out] IntPtr[] d);
    [DllImport(VK)] static extern void vkGetPhysicalDeviceProperties(IntPtr pd, IntPtr props);
    [DllImport(VK)] static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr pd, ref uint c, IntPtr props);
    [DllImport(VK)] static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr pd, IntPtr props);
    [DllImport(VK)] static extern int  vkCreateDevice(IntPtr pd, ref DevCI ci, IntPtr a, out IntPtr dev);
    [DllImport(VK)] static extern void vkGetDeviceQueue(IntPtr dev, uint qf, uint qi, out IntPtr q);
    [DllImport(VK)] static extern int  vkCreateBuffer(IntPtr dev, ref BufCI ci, IntPtr a, out IntPtr buf);
    [DllImport(VK)] static extern void vkGetBufferMemoryRequirements(IntPtr dev, IntPtr buf, out MemReq r);
    [DllImport(VK)] static extern int  vkAllocateMemory(IntPtr dev, ref MemAI ci, IntPtr a, out IntPtr mem);
    [DllImport(VK)] static extern int  vkBindBufferMemory(IntPtr dev, IntPtr buf, IntPtr mem, ulong off);
    [DllImport(VK)] static extern int  vkMapMemory(IntPtr dev, IntPtr mem, ulong off, ulong sz, uint f, out IntPtr p);
    [DllImport(VK)] static extern void vkUnmapMemory(IntPtr dev, IntPtr mem);
    [DllImport(VK)] static extern int  vkCreateDescriptorSetLayout(IntPtr dev, ref DSLCI ci, IntPtr a, out IntPtr l);
    [DllImport(VK)] static extern int  vkCreatePipelineLayout(IntPtr dev, ref PLCI ci, IntPtr a, out IntPtr l);
    [DllImport(VK)] static extern int  vkCreateShaderModule(IntPtr dev, ref SMCI ci, IntPtr a, out IntPtr m);
    [DllImport(VK)] static extern int  vkCreateComputePipelines(IntPtr dev, IntPtr cache, uint cnt, ref CPCI ci, IntPtr a, out IntPtr p);
    [DllImport(VK)] static extern int  vkCreateDescriptorPool(IntPtr dev, ref DPCI ci, IntPtr a, out IntPtr p);
    [DllImport(VK)] static extern int  vkAllocateDescriptorSets(IntPtr dev, ref DSAI ci, out IntPtr s);
    [DllImport(VK)] static extern void vkUpdateDescriptorSets(IntPtr dev, uint wc, [In] WDS[] w, uint cc, IntPtr c);
    [DllImport(VK)] static extern int  vkCreateCommandPool(IntPtr dev, ref CPoolCI ci, IntPtr a, out IntPtr p);
    [DllImport(VK)] static extern int  vkAllocateCommandBuffers(IntPtr dev, ref CBAI ci, out IntPtr cb);
    [DllImport(VK)] static extern int  vkBeginCommandBuffer(IntPtr cb, ref CBBI bi);
    [DllImport(VK)] static extern void vkCmdBindPipeline(IntPtr cb, int bp, IntPtr p);
    [DllImport(VK)] static extern void vkCmdBindDescriptorSets(IntPtr cb, int bp, IntPtr lay, uint fs, uint sc, ref IntPtr sets, uint dc, IntPtr dofs);
    [DllImport(VK)] static extern void vkCmdPushConstants(IntPtr cb, IntPtr lay, uint sf, uint off, uint sz, uint[] vals);
    [DllImport(VK)] static extern void vkCmdDispatch(IntPtr cb, uint x, uint y, uint z);
    [DllImport(VK)] static extern int  vkEndCommandBuffer(IntPtr cb);
    [DllImport(VK)] static extern int  vkCreateFence(IntPtr dev, ref FenceCI ci, IntPtr a, out IntPtr f);
    [DllImport(VK)] static extern int  vkQueueSubmit(IntPtr q, uint c, ref SubmitI si, IntPtr fence);
    [DllImport(VK)] static extern int  vkWaitForFences(IntPtr dev, uint c, ref IntPtr f, uint all, ulong timeout);
    [DllImport(VK)] static extern void vkDestroyBuffer(IntPtr dev, IntPtr b, IntPtr a);
    [DllImport(VK)] static extern void vkFreeMemory(IntPtr dev, IntPtr m, IntPtr a);
    [DllImport(VK)] static extern IntPtr vkGetDeviceProcAddr(IntPtr dev, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(VK)] static extern int vkEnumerateDeviceExtensionProperties(IntPtr pd, IntPtr layer, ref uint count, IntPtr props);
    [DllImport(VK)] static extern void vkDestroyDescriptorPool(IntPtr dev, IntPtr p, IntPtr a);
    [DllImport(VK)] static extern void vkDestroyCommandPool(IntPtr dev, IntPtr p, IntPtr a);
    [DllImport(VK)] static extern void vkDestroyFence(IntPtr dev, IntPtr f, IntPtr a);

    [StructLayout(LayoutKind.Sequential)] struct AppI   { public int sType; public IntPtr pNext; public IntPtr pAppName; public uint appVer; public IntPtr pEng; public uint engVer; public uint apiVer; }
    [StructLayout(LayoutKind.Sequential)] struct InstCI { public int sType; public IntPtr pNext; public uint flags; public IntPtr pApp; public uint lc; public IntPtr ppL; public uint ec; public IntPtr ppE; }
    [StructLayout(LayoutKind.Sequential)] struct QueueCI{ public int sType; public IntPtr pNext; public uint flags; public uint qfi; public uint qc; public IntPtr pPri; }
    [StructLayout(LayoutKind.Sequential)] struct DevCI  { public int sType; public IntPtr pNext; public uint flags; public uint qcic; public IntPtr pQci; public uint lc; public IntPtr ppL; public uint ec; public IntPtr ppE; public IntPtr pFeat; }
    [StructLayout(LayoutKind.Sequential)] struct BufCI  { public int sType; public IntPtr pNext; public uint flags; public ulong size; public uint usage; public uint sharing; public uint qfic; public IntPtr pQfi; }
    [StructLayout(LayoutKind.Sequential)] struct MemReq { public ulong size; public ulong align; public uint typeBits; }
    [StructLayout(LayoutKind.Sequential)] struct MemAI  { public int sType; public IntPtr pNext; public ulong size; public uint typeIdx; }
    [StructLayout(LayoutKind.Sequential)] struct DSLB   { public uint binding; public uint type; public uint count; public uint stage; public IntPtr pImm; }
    [StructLayout(LayoutKind.Sequential)] struct DSLCI  { public int sType; public IntPtr pNext; public uint flags; public uint bc; public IntPtr pB; }
    [StructLayout(LayoutKind.Sequential)] struct PCR    { public uint stage; public uint off; public uint size; }
    [StructLayout(LayoutKind.Sequential)] struct PLCI   { public int sType; public IntPtr pNext; public uint flags; public uint slc; public IntPtr pSL; public uint pcrc; public IntPtr pPCR; }
    [StructLayout(LayoutKind.Sequential)] struct SMCI   { public int sType; public IntPtr pNext; public uint flags; public IntPtr codeSize; public IntPtr pCode; }
    [StructLayout(LayoutKind.Sequential)] struct PSSCI  { public int sType; public IntPtr pNext; public uint flags; public uint stage; public IntPtr module; public IntPtr pName; public IntPtr pSpec; }
    [StructLayout(LayoutKind.Sequential)] struct CPCI   { public int sType; public IntPtr pNext; public uint flags; public PSSCI stage; public IntPtr layout; public IntPtr baseH; public int baseI; }
    [StructLayout(LayoutKind.Sequential)] struct DPS    { public uint type; public uint count; }
    [StructLayout(LayoutKind.Sequential)] struct DPCI   { public int sType; public IntPtr pNext; public uint flags; public uint maxSets; public uint psc; public IntPtr pPS; }
    [StructLayout(LayoutKind.Sequential)] struct DSAI   { public int sType; public IntPtr pNext; public IntPtr pool; public uint sc; public IntPtr pSL; }
    [StructLayout(LayoutKind.Sequential)] struct DBI    { public IntPtr buffer; public ulong off; public ulong range; }
    [StructLayout(LayoutKind.Sequential)] struct WDS    { public int sType; public IntPtr pNext; public IntPtr dstSet; public uint dstBind; public uint dstArr; public uint count; public uint type; public IntPtr pImg; public IntPtr pBuf; public IntPtr pTex; }
    [StructLayout(LayoutKind.Sequential)] struct CPoolCI{ public int sType; public IntPtr pNext; public uint flags; public uint qfi; }
    [StructLayout(LayoutKind.Sequential)] struct CBAI   { public int sType; public IntPtr pNext; public IntPtr pool; public uint level; public uint count; }
    [StructLayout(LayoutKind.Sequential)] struct CBBI   { public int sType; public IntPtr pNext; public uint flags; public IntPtr pInh; }
    [StructLayout(LayoutKind.Sequential)] struct SubmitI{ public int sType; public IntPtr pNext; public uint wsc; public IntPtr pWS; public IntPtr pWDSM; public uint cbc; public IntPtr pCB; public uint ssc; public IntPtr pSS; }
    [StructLayout(LayoutKind.Sequential)] struct FenceCI{ public int sType; public IntPtr pNext; public uint flags; }

    // --- AHardwareBuffer import (VK_ANDROID_external_memory_android_hardware_buffer) ---
    // sType values are the spec constants; handleTypes 0x400 = ..._ANDROID_HARDWARE_BUFFER_BIT_ANDROID.
    [StructLayout(LayoutKind.Sequential)] struct ExtMemBufCI { public int sType; public IntPtr pNext; public uint handleTypes; }            // 1000072004
    [StructLayout(LayoutKind.Sequential)] struct AhbProps    { public int sType; public IntPtr pNext; public ulong allocationSize; public uint memoryTypeBits; } // 1000129003
    [StructLayout(LayoutKind.Sequential)] struct ImportAhbAI { public int sType; public IntPtr pNext; public IntPtr buffer; }               // 1000129002
    [StructLayout(LayoutKind.Sequential)] struct DedicatedAI { public int sType; public IntPtr pNext; public IntPtr image; public IntPtr buffer; } // 1000127001
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int PfnGetAhbProps(IntPtr dev, IntPtr ahb, ref AhbProps props);

    static IntPtr Pin<T>(T s){ IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T))); Marshal.StructureToPtr(s, p, false); return p; }
    static IntPtr PinArr<T>(T[] a){ int sz = Marshal.SizeOf(typeof(T)); IntPtr p = Marshal.AllocHGlobal(sz*a.Length); for(int i=0;i<a.Length;i++) Marshal.StructureToPtr(a[i], (IntPtr)((byte*)p + i*sz), false); return p; }
    static IntPtr PinH(IntPtr h){ IntPtr p = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(p, h); return p; }

    static IntPtr s_inst, s_pd, s_dev, s_queue, s_memProps, s_dsl, s_playout, s_pipe;
    static uint s_qfi, s_memTypeCount; static int s_spvLen = -1; static string s_name = "(none)";
    public static string Name => s_name;

    // q4 GEMM: separate descriptor-set layout (5 bindings: A, Bq, Scales, Zp, C), pipeline layout (push {M,N,K,BlockSize,HasZp}), pipeline.
    static IntPtr s_dslQ4, s_playoutQ4, s_pipeQ4; static int s_spvQ4Len = -1;
    // The GEMV variant rung (M==1 decode, one workgroup per row) — same layout/push interface, its own
    // pipeline. Absent .spv = the naive rung stands (fallback-by-absence, the D3D12 variant discipline).
    static IntPtr s_pipeQ4Gemv; static int s_spvQ4GemvLen = -1;

    public static void EnsureInit()
    {
        if (s_dev != IntPtr.Zero) return;
        AppI ai = new AppI(); ai.sType = 0; ai.apiVer = 0x400000;
        InstCI ici = new InstCI(); ici.sType = 1; ici.pApp = Pin(ai);
        vkCreateInstance(ref ici, IntPtr.Zero, out s_inst);
        uint dn = 0; vkEnumeratePhysicalDevices(s_inst, ref dn, null);
        IntPtr[] pds = new IntPtr[dn]; vkEnumeratePhysicalDevices(s_inst, ref dn, pds); s_pd = pds[0];
        IntPtr props = Marshal.AllocHGlobal(1024); vkGetPhysicalDeviceProperties(s_pd, props);
        s_name = Marshal.PtrToStringAnsi((IntPtr)((byte*)props + 20));
        s_memProps = Marshal.AllocHGlobal(1024); vkGetPhysicalDeviceMemoryProperties(s_pd, s_memProps);
        s_memTypeCount = (uint)Marshal.ReadInt32(s_memProps, 0);
        // first queue family with COMPUTE
        uint qn = 0; vkGetPhysicalDeviceQueueFamilyProperties(s_pd, ref qn, IntPtr.Zero);
        IntPtr qbuf = Marshal.AllocHGlobal((int)qn * 24); vkGetPhysicalDeviceQueueFamilyProperties(s_pd, ref qn, qbuf);
        s_qfi = 0; for (uint i = 0; i < qn; i++) { uint fl = (uint)Marshal.ReadInt32(qbuf, (int)i * 24); if ((fl & 0x2) != 0) { s_qfi = i; break; } }
        float[] pri = new float[] { 1.0f };
        QueueCI qci = new QueueCI(); qci.sType = 2; qci.qfi = s_qfi; qci.qc = 1; qci.pPri = PinArr(pri);
        DevCI dci = new DevCI(); dci.sType = 3; dci.qcic = 1; dci.pQci = Pin(qci);
        // Android: enable the AHardwareBuffer-import extension set, presence-probed against the device's own
        // extension list (absence = the import path never engages; the transient upload rung stays — inv-9).
        // Windows keeps ec=0 — the Android ext names must not be requested off-platform.
        if (OperatingSystem.IsAndroid())
        {
            string[] want = {
                "VK_ANDROID_external_memory_android_hardware_buffer",
                "VK_KHR_external_memory", "VK_KHR_sampler_ycbcr_conversion", "VK_EXT_queue_family_foreign",
                "VK_KHR_bind_memory2", "VK_KHR_get_memory_requirements2", "VK_KHR_maintenance1",
                "VK_KHR_dedicated_allocation", "VK_KHR_external_memory_capabilities",
            };
            uint xc = 0; vkEnumerateDeviceExtensionProperties(s_pd, IntPtr.Zero, ref xc, IntPtr.Zero);
            var have = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (xc > 0)
            {
                IntPtr xbuf = Marshal.AllocHGlobal((int)xc * 260);   // VkExtensionProperties: char[256] + uint
                vkEnumerateDeviceExtensionProperties(s_pd, IntPtr.Zero, ref xc, xbuf);
                for (uint i = 0; i < xc; i++)
                {
                    string? nm = Marshal.PtrToStringAnsi((IntPtr)((byte*)xbuf + i * 260));
                    if (nm != null) have.Add(nm);
                }
                Marshal.FreeHGlobal(xbuf);
            }
            var enable = new System.Collections.Generic.List<IntPtr>();
            foreach (var w in want) if (have.Contains(w)) enable.Add(Marshal.StringToHGlobalAnsi(w));
            if (enable.Count > 0 && have.Contains("VK_ANDROID_external_memory_android_hardware_buffer"))
            {
                IntPtr names = Marshal.AllocHGlobal(IntPtr.Size * enable.Count);
                for (int i = 0; i < enable.Count; i++) Marshal.WriteIntPtr(names, i * IntPtr.Size, enable[i]);
                dci.ec = (uint)enable.Count; dci.ppE = names;
            }
        }
        vkCreateDevice(s_pd, ref dci, IntPtr.Zero, out s_dev);
        vkGetDeviceQueue(s_dev, s_qfi, 0, out s_queue);
        // descriptor set layout: 3 storage buffers (A,B,C), stage COMPUTE
        DSLB[] binds = new DSLB[3];
        for (uint i = 0; i < 3; i++) { binds[i].binding = i; binds[i].type = 7; binds[i].count = 1; binds[i].stage = 0x20; }
        DSLCI dslci = new DSLCI(); dslci.sType = 32; dslci.bc = 3; dslci.pB = PinArr(binds);
        vkCreateDescriptorSetLayout(s_dev, ref dslci, IntPtr.Zero, out s_dsl);
        PCR pcr = new PCR(); pcr.stage = 0x20; pcr.off = 0; pcr.size = 12;   // push {M,N,K}
        PLCI plci = new PLCI(); plci.sType = 30; plci.slc = 1; plci.pSL = PinH(s_dsl); plci.pcrc = 1; plci.pPCR = Pin(pcr);
        vkCreatePipelineLayout(s_dev, ref plci, IntPtr.Zero, out s_playout);

        // q4 descriptor set layout: 5 storage buffers (A, Bq, Scales, Zp, C), stage COMPUTE
        DSLB[] bindsQ = new DSLB[5];
        for (uint i = 0; i < 5; i++) { bindsQ[i].binding = i; bindsQ[i].type = 7; bindsQ[i].count = 1; bindsQ[i].stage = 0x20; }
        DSLCI dslciQ = new DSLCI(); dslciQ.sType = 32; dslciQ.bc = 5; dslciQ.pB = PinArr(bindsQ);
        vkCreateDescriptorSetLayout(s_dev, ref dslciQ, IntPtr.Zero, out s_dslQ4);
        PCR pcrQ = new PCR(); pcrQ.stage = 0x20; pcrQ.off = 0; pcrQ.size = 20;   // push {M,N,K,BlockSize,HasZp}
        PLCI plciQ = new PLCI(); plciQ.sType = 30; plciQ.slc = 1; plciQ.pSL = PinH(s_dslQ4); plciQ.pcrc = 1; plciQ.pPCR = Pin(pcrQ);
        vkCreatePipelineLayout(s_dev, ref plciQ, IntPtr.Zero, out s_playoutQ4);
    }

    static void EnsurePipeline(byte[] spv)
    {
        if (s_pipe != IntPtr.Zero && s_spvLen == spv.Length) return;
        IntPtr spvPtr = Marshal.AllocHGlobal(spv.Length); Marshal.Copy(spv, 0, spvPtr, spv.Length);
        SMCI smci = new SMCI(); smci.sType = 16; smci.codeSize = (IntPtr)spv.Length; smci.pCode = spvPtr;
        IntPtr shader; vkCreateShaderModule(s_dev, ref smci, IntPtr.Zero, out shader);
        CPCI cpci = new CPCI(); cpci.sType = 29;
        cpci.stage = new PSSCI(); cpci.stage.sType = 18; cpci.stage.stage = 0x20; cpci.stage.module = shader; cpci.stage.pName = Marshal.StringToHGlobalAnsi("main");
        cpci.layout = s_playout;
        vkCreateComputePipelines(s_dev, IntPtr.Zero, 1, ref cpci, IntPtr.Zero, out s_pipe);
        s_spvLen = spv.Length;
    }

    static void EnsurePipelineQ4(byte[] spv)
    {
        if (s_pipeQ4 != IntPtr.Zero && s_spvQ4Len == spv.Length) return;
        IntPtr spvPtr = Marshal.AllocHGlobal(spv.Length); Marshal.Copy(spv, 0, spvPtr, spv.Length);
        SMCI smci = new SMCI(); smci.sType = 16; smci.codeSize = (IntPtr)spv.Length; smci.pCode = spvPtr;
        IntPtr shader; vkCreateShaderModule(s_dev, ref smci, IntPtr.Zero, out shader);
        CPCI cpci = new CPCI(); cpci.sType = 29;
        cpci.stage = new PSSCI(); cpci.stage.sType = 18; cpci.stage.stage = 0x20; cpci.stage.module = shader; cpci.stage.pName = Marshal.StringToHGlobalAnsi("main");
        cpci.layout = s_playoutQ4;
        vkCreateComputePipelines(s_dev, IntPtr.Zero, 1, ref cpci, IntPtr.Zero, out s_pipeQ4);
        s_spvQ4Len = spv.Length;
    }

    static void EnsurePipelineQ4Gemv(byte[] spv)
    {
        if (s_pipeQ4Gemv != IntPtr.Zero && s_spvQ4GemvLen == spv.Length) return;
        IntPtr spvPtr = Marshal.AllocHGlobal(spv.Length); Marshal.Copy(spv, 0, spvPtr, spv.Length);
        SMCI smci = new SMCI(); smci.sType = 16; smci.codeSize = (IntPtr)spv.Length; smci.pCode = spvPtr;
        IntPtr shader; vkCreateShaderModule(s_dev, ref smci, IntPtr.Zero, out shader);
        CPCI cpci = new CPCI(); cpci.sType = 29;
        cpci.stage = new PSSCI(); cpci.stage.sType = 18; cpci.stage.stage = 0x20; cpci.stage.module = shader; cpci.stage.pName = Marshal.StringToHGlobalAnsi("main");
        cpci.layout = s_playoutQ4;   // same 5-binding layout + push range — the variant shares the seam
        vkCreateComputePipelines(s_dev, IntPtr.Zero, 1, ref cpci, IntPtr.Zero, out s_pipeQ4Gemv);
        s_spvQ4GemvLen = spv.Length;
    }

    static int FindMem(uint typeBits)
    {
        for (int i = 0; i < s_memTypeCount; i++)
        {
            uint pf = (uint)Marshal.ReadInt32(s_memProps, 4 + i * 8);
            if (((typeBits >> i) & 1) != 0 && (pf & 0x2) != 0 && (pf & 0x4) != 0) return i;   // HOST_VISIBLE|COHERENT
        }
        return -1;
    }

    static void MkBuf(ulong bytes, out IntPtr buf, out IntPtr mem)
    {
        BufCI bci = new BufCI(); bci.sType = 12; bci.size = bytes; bci.usage = 0x20; bci.sharing = 0;   // STORAGE_BUFFER
        vkCreateBuffer(s_dev, ref bci, IntPtr.Zero, out buf);
        MemReq req; vkGetBufferMemoryRequirements(s_dev, buf, out req);
        MemAI mai = new MemAI(); mai.sType = 5; mai.size = req.size; mai.typeIdx = (uint)FindMem(req.typeBits);
        vkAllocateMemory(s_dev, ref mai, IntPtr.Zero, out mem);
        vkBindBufferMemory(s_dev, buf, mem, 0);
    }

    public static int Gemm(float[] A, float[] B, float[] C, uint M, uint N, uint K, byte[] spv)
    {
        EnsureInit();
        EnsurePipeline(spv);
        ulong aB = (ulong)M * K * 4, bB = (ulong)K * N * 4, cB = (ulong)M * N * 4;
        IntPtr aBuf, aMem, bBuf, bMem, cBuf, cMem;
        MkBuf(aB, out aBuf, out aMem); MkBuf(bB, out bBuf, out bMem); MkBuf(cB, out cBuf, out cMem);
        IntPtr p;
        vkMapMemory(s_dev, aMem, 0, aB, 0, out p); Marshal.Copy(A, 0, p, (int)(M * K)); vkUnmapMemory(s_dev, aMem);
        vkMapMemory(s_dev, bMem, 0, bB, 0, out p); Marshal.Copy(B, 0, p, (int)(K * N)); vkUnmapMemory(s_dev, bMem);

        DPS[] ps = new DPS[] { new DPS { type = 7, count = 3 } };
        DPCI dpci = new DPCI(); dpci.sType = 33; dpci.maxSets = 1; dpci.psc = 1; dpci.pPS = PinArr(ps);
        IntPtr pool; vkCreateDescriptorPool(s_dev, ref dpci, IntPtr.Zero, out pool);
        DSAI dsai = new DSAI(); dsai.sType = 34; dsai.pool = pool; dsai.sc = 1; dsai.pSL = PinH(s_dsl);
        IntPtr dset; vkAllocateDescriptorSets(s_dev, ref dsai, out dset);
        DBI ia = new DBI { buffer = aBuf, off = 0, range = aB }, ib = new DBI { buffer = bBuf, off = 0, range = bB }, ic = new DBI { buffer = cBuf, off = 0, range = cB };
        WDS[] w = new WDS[3];
        w[0].sType = 35; w[0].dstSet = dset; w[0].dstBind = 0; w[0].count = 1; w[0].type = 7; w[0].pBuf = Pin(ia);
        w[1].sType = 35; w[1].dstSet = dset; w[1].dstBind = 1; w[1].count = 1; w[1].type = 7; w[1].pBuf = Pin(ib);
        w[2].sType = 35; w[2].dstSet = dset; w[2].dstBind = 2; w[2].count = 1; w[2].type = 7; w[2].pBuf = Pin(ic);
        vkUpdateDescriptorSets(s_dev, 3, w, 0, IntPtr.Zero);

        CPoolCI cpci2 = new CPoolCI(); cpci2.sType = 39; cpci2.qfi = s_qfi;
        IntPtr cpool; vkCreateCommandPool(s_dev, ref cpci2, IntPtr.Zero, out cpool);
        CBAI cbai = new CBAI(); cbai.sType = 40; cbai.pool = cpool; cbai.level = 0; cbai.count = 1;
        IntPtr cb; vkAllocateCommandBuffers(s_dev, ref cbai, out cb);
        CBBI bi = new CBBI(); bi.sType = 42; bi.flags = 1;
        vkBeginCommandBuffer(cb, ref bi);
        vkCmdBindPipeline(cb, 1, s_pipe);
        IntPtr setH = dset; vkCmdBindDescriptorSets(cb, 1, s_playout, 0, 1, ref setH, 0, IntPtr.Zero);
        vkCmdPushConstants(cb, s_playout, 0x20, 0, 12, new uint[] { M, N, K });
        vkCmdDispatch(cb, (N + 15) / 16, (M + 15) / 16, 1);
        vkEndCommandBuffer(cb);

        FenceCI fci = new FenceCI(); fci.sType = 8; IntPtr fence; vkCreateFence(s_dev, ref fci, IntPtr.Zero, out fence);
        SubmitI si = new SubmitI(); si.sType = 4; si.cbc = 1; si.pCB = PinH(cb);
        vkQueueSubmit(s_queue, 1, ref si, fence);
        vkWaitForFences(s_dev, 1, ref fence, 1, ulong.MaxValue);

        vkMapMemory(s_dev, cMem, 0, cB, 0, out p); Marshal.Copy(p, C, 0, (int)(M * N)); vkUnmapMemory(s_dev, cMem);

        vkDestroyFence(s_dev, fence, IntPtr.Zero);
        vkDestroyCommandPool(s_dev, cpool, IntPtr.Zero);
        vkDestroyDescriptorPool(s_dev, pool, IntPtr.Zero);
        vkDestroyBuffer(s_dev, aBuf, IntPtr.Zero); vkFreeMemory(s_dev, aMem, IntPtr.Zero);
        vkDestroyBuffer(s_dev, bBuf, IntPtr.Zero); vkFreeMemory(s_dev, bMem, IntPtr.Zero);
        vkDestroyBuffer(s_dev, cBuf, IntPtr.Zero); vkFreeMemory(s_dev, cMem, IntPtr.Zero);
        return 0;
    }

    // GemmQ4: Y[M,N] = A[M,K] @ dequant(Bq)^T on the q4 SEQUENTIAL-nibble packed weight — same layout/formula as
    // GpuD3D12.GemmQ4. Never materializes the dequantized fp32 weight on the CPU; dequant happens per-k on GPU.
    // Import an AHB-backed weight as a VkBuffer bound to imported VkDeviceMemory (dedicated alloc — the
    // spec requires it for AHB import). One import per weight, cached ON the tensor; false on any VkResult
    // failure (the caller falls back to the transient-upload rung and clears AhbWeight so it never retries).
    static bool TryImportAhbWeight(Tensor bWeight)
    {
        if (bWeight == null || bWeight.AhbWeight == 0) return false;
        if (bWeight.VkWeightBuf != 0) return true;   // already imported
        IntPtr pfn = vkGetDeviceProcAddr(s_dev, "vkGetAndroidHardwareBufferPropertiesANDROID");
        if (pfn == IntPtr.Zero) return false;        // extension not enabled/present — transient rung stays
        var getProps = Marshal.GetDelegateForFunctionPointer<PfnGetAhbProps>(pfn);
        AhbProps props = new AhbProps { sType = 1000129003 };
        if (getProps(s_dev, bWeight.AhbWeight, ref props) != 0 || props.allocationSize == 0) return false;

        AhbNative.AHardwareBuffer_describe(bWeight.AhbWeight, out var desc);
        ulong byteLen = desc.Width;                  // BLOB: width = byte length

        ExtMemBufCI ext = new ExtMemBufCI { sType = 1000072004, handleTypes = 0x400 };
        BufCI bci = new BufCI { sType = 12, pNext = Pin(ext), size = byteLen, usage = 0x20, sharing = 0 };
        if (vkCreateBuffer(s_dev, ref bci, IntPtr.Zero, out IntPtr buf) != 0) return false;

        DedicatedAI ded = new DedicatedAI { sType = 1000127001, buffer = buf };
        ImportAhbAI imp = new ImportAhbAI { sType = 1000129002, pNext = Pin(ded), buffer = bWeight.AhbWeight };
        uint typeIdx = 0; for (int i = 0; i < 32; i++) if (((props.memoryTypeBits >> i) & 1) != 0) { typeIdx = (uint)i; break; }
        MemAI mai = new MemAI { sType = 5, pNext = Pin(imp), size = props.allocationSize, typeIdx = typeIdx };
        if (vkAllocateMemory(s_dev, ref mai, IntPtr.Zero, out IntPtr mem) != 0)
        { vkDestroyBuffer(s_dev, buf, IntPtr.Zero); return false; }
        if (vkBindBufferMemory(s_dev, buf, mem, 0) != 0)
        { vkDestroyBuffer(s_dev, buf, IntPtr.Zero); vkFreeMemory(s_dev, mem, IntPtr.Zero); return false; }

        bWeight.SetVkWeight(buf, mem, byteLen);
        return true;
    }

    public static int GemmQ4(float[] A, byte[] Bq, float[] Scales, byte[] Zp, float[] C, uint M, uint N, uint K, uint blockSize, bool hasZp, byte[] spv, Tensor bWeight = null, byte[] spvGemv = null)
    {
        EnsureInit();
        EnsurePipelineQ4(spv);
        // GEMV rung: M==1, no zero-point plane, variant shipped. One workgroup per row (dispatch N,1,1).
        bool gemv = M == 1 && !hasZp && spvGemv != null && spvGemv.Length > 0;
        if (gemv) EnsurePipelineQ4Gemv(spvGemv);
        // Resident rung: the weight's AHB imports once and the GPU reads the SAME memory the CPU filled at
        // model load — no per-call weight map/copy (the churn that killed the razr). Import failure clears
        // AhbWeight so the transient rung stands permanently for that weight (inv-9, fault latches once).
        bool resident = false;
        if (bWeight != null && bWeight.AhbWeight != 0)
        {
            resident = TryImportAhbWeight(bWeight);
            if (!resident) bWeight.SetAhbWeight(0);
        }
        // Transient Bq source: the caller may pass an empty Bq when the weight is AHB-backed; if the import
        // just failed, read the bytes from the AHB's CPU map (ReadRawb) so the fallback still computes.
        ulong aB = (ulong)A.Length * 4, scB = (ulong)Scales.Length * 4, cB = (ulong)M * N * 4;
        ulong bqB = resident ? bWeight.VkWeightBytes
                  : (ulong)(Bq.Length > 0 ? Bq.Length : (bWeight != null ? bWeight.ReadRawb().Length : 0));
        ulong zpB = (ulong)Math.Max(1, Zp?.Length ?? 1);
        IntPtr aBuf, aMem, bqBuf = IntPtr.Zero, bqMem = IntPtr.Zero, scBuf, scMem, zpBuf, zpMem, cBuf, cMem;
        MkBuf(aB, out aBuf, out aMem); MkBuf(scB, out scBuf, out scMem);
        MkBuf(zpB, out zpBuf, out zpMem); MkBuf(cB, out cBuf, out cMem);
        if (!resident) MkBuf(bqB, out bqBuf, out bqMem);
        IntPtr p;
        vkMapMemory(s_dev, aMem, 0, aB, 0, out p); Marshal.Copy(A, 0, p, A.Length); vkUnmapMemory(s_dev, aMem);
        if (!resident)
        {
            vkMapMemory(s_dev, bqMem, 0, bqB, 0, out p);
            if (Bq.Length > 0) Marshal.Copy(Bq, 0, p, Bq.Length);
            else if (bWeight != null) bWeight.ReadRawb().CopyTo(new Span<byte>((void*)p, (int)bqB));
            vkUnmapMemory(s_dev, bqMem);
        }
        vkMapMemory(s_dev, scMem, 0, scB, 0, out p); Marshal.Copy(Scales, 0, p, Scales.Length); vkUnmapMemory(s_dev, scMem);
        if (hasZp && Zp != null && Zp.Length > 0) { vkMapMemory(s_dev, zpMem, 0, zpB, 0, out p); Marshal.Copy(Zp, 0, p, Zp.Length); vkUnmapMemory(s_dev, zpMem); }

        DPS[] ps = new DPS[] { new DPS { type = 7, count = 5 } };
        DPCI dpci = new DPCI(); dpci.sType = 33; dpci.maxSets = 1; dpci.psc = 1; dpci.pPS = PinArr(ps);
        IntPtr pool; vkCreateDescriptorPool(s_dev, ref dpci, IntPtr.Zero, out pool);
        DSAI dsai = new DSAI(); dsai.sType = 34; dsai.pool = pool; dsai.sc = 1; dsai.pSL = PinH(s_dslQ4);
        IntPtr dset; vkAllocateDescriptorSets(s_dev, ref dsai, out dset);
        DBI ia = new DBI { buffer = aBuf, off = 0, range = aB },
            ib = new DBI { buffer = resident ? bWeight.VkWeightBuf : bqBuf, off = 0, range = bqB },
            isc = new DBI { buffer = scBuf, off = 0, range = scB }, iz = new DBI { buffer = zpBuf, off = 0, range = zpB },
            ic = new DBI { buffer = cBuf, off = 0, range = cB };
        WDS[] w = new WDS[5];
        w[0].sType = 35; w[0].dstSet = dset; w[0].dstBind = 0; w[0].count = 1; w[0].type = 7; w[0].pBuf = Pin(ia);
        w[1].sType = 35; w[1].dstSet = dset; w[1].dstBind = 1; w[1].count = 1; w[1].type = 7; w[1].pBuf = Pin(ib);
        w[2].sType = 35; w[2].dstSet = dset; w[2].dstBind = 2; w[2].count = 1; w[2].type = 7; w[2].pBuf = Pin(isc);
        w[3].sType = 35; w[3].dstSet = dset; w[3].dstBind = 3; w[3].count = 1; w[3].type = 7; w[3].pBuf = Pin(iz);
        w[4].sType = 35; w[4].dstSet = dset; w[4].dstBind = 4; w[4].count = 1; w[4].type = 7; w[4].pBuf = Pin(ic);
        vkUpdateDescriptorSets(s_dev, 5, w, 0, IntPtr.Zero);

        CPoolCI cpci2 = new CPoolCI(); cpci2.sType = 39; cpci2.qfi = s_qfi;
        IntPtr cpool; vkCreateCommandPool(s_dev, ref cpci2, IntPtr.Zero, out cpool);
        CBAI cbai = new CBAI(); cbai.sType = 40; cbai.pool = cpool; cbai.level = 0; cbai.count = 1;
        IntPtr cb; vkAllocateCommandBuffers(s_dev, ref cbai, out cb);
        CBBI bi = new CBBI(); bi.sType = 42; bi.flags = 1;
        vkBeginCommandBuffer(cb, ref bi);
        vkCmdBindPipeline(cb, 1, gemv ? s_pipeQ4Gemv : s_pipeQ4);
        IntPtr setH = dset; vkCmdBindDescriptorSets(cb, 1, s_playoutQ4, 0, 1, ref setH, 0, IntPtr.Zero);
        vkCmdPushConstants(cb, s_playoutQ4, 0x20, 0, 20, new uint[] { M, N, K, blockSize, hasZp ? 1u : 0u });
        if (gemv) vkCmdDispatch(cb, N, 1, 1);
        else vkCmdDispatch(cb, (N + 15) / 16, (M + 15) / 16, 1);
        vkEndCommandBuffer(cb);

        FenceCI fci = new FenceCI(); fci.sType = 8; IntPtr fence; vkCreateFence(s_dev, ref fci, IntPtr.Zero, out fence);
        SubmitI si = new SubmitI(); si.sType = 4; si.cbc = 1; si.pCB = PinH(cb);
        vkQueueSubmit(s_queue, 1, ref si, fence);
        vkWaitForFences(s_dev, 1, ref fence, 1, ulong.MaxValue);

        vkMapMemory(s_dev, cMem, 0, cB, 0, out p); Marshal.Copy(p, C, 0, (int)(M * N)); vkUnmapMemory(s_dev, cMem);

        vkDestroyFence(s_dev, fence, IntPtr.Zero);
        vkDestroyCommandPool(s_dev, cpool, IntPtr.Zero);
        vkDestroyDescriptorPool(s_dev, pool, IntPtr.Zero);
        vkDestroyBuffer(s_dev, aBuf, IntPtr.Zero); vkFreeMemory(s_dev, aMem, IntPtr.Zero);
        // A resident weight's VkBuffer/VkDeviceMemory ride the tensor for the model's lifetime — never
        // destroyed per call (that per-call churn was the whole disease).
        if (!resident) { vkDestroyBuffer(s_dev, bqBuf, IntPtr.Zero); vkFreeMemory(s_dev, bqMem, IntPtr.Zero); }
        vkDestroyBuffer(s_dev, scBuf, IntPtr.Zero); vkFreeMemory(s_dev, scMem, IntPtr.Zero);
        vkDestroyBuffer(s_dev, zpBuf, IntPtr.Zero); vkFreeMemory(s_dev, zpMem, IntPtr.Zero);
        vkDestroyBuffer(s_dev, cBuf, IntPtr.Zero); vkFreeMemory(s_dev, cMem, IntPtr.Zero);
        return 0;
    }
}
