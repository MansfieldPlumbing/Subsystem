using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Subsystem.Dpx
{
    // DpxQnn: the QNN device projection. Builds a graph through the QnnGraph C API, finalizes it on
    // the device (the HTP runtime performs the SoC-specific prepare), and emits the context binary via
    // contextGetBinary. model.db -> .bin is a VOM Project (carrier-by-mode == projection-by-target,
    // CRQ158/144): no python, no qairt-converter, no Hexagon SDK, no closed Qualcomm generator.
    //
    // Verified byte-identical to the C++ reference on a Snapdragon v73 (md5 match; CRQ158 EOS). The two
    // device-found invariants are baked in below: sizeof(Qnn_Tensor_t)=144 (node input ARRAYS stride
    // there) and float QuantizeParams must read UNDEFINED, not zero (HTP rejects zero-init as
    // SCALE_OFFSET scale=0 with 0x1775). The proof graph is one MatMul; broader op coverage and the
    // model.db source ride on this seam.
    //
    // Data plane is native (off-GC) here; VOM-region backing + deterministic teardown (the QNN runtime
    // is a mounted guest, cascade-killed) is CRQ164. Project leaves the QNN handles to the runtime and
    // frees only its own scratch.
    public sealed unsafe class DpxQnn
    {
        const int TypeAppWrite = 0, TypeAppRead = 1, TypeStatic = 4;
        const int DataTypeFloat32 = 0x0232;
        const int DataFormatFlat = 0;
        const int MemTypeRaw = 0;
        const int TensorVersion1 = 1, OpConfigVersion1 = 1;
        const int EncodingUndefined = 0x7FFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        struct ClientBuffer { public IntPtr Data; public uint DataSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct QuantizeParams { public int EncodingDefinition; public int Encoding; public long U0, U1, U2, U3; }
        [StructLayout(LayoutKind.Sequential)]
        struct TensorV1
        {
            public uint Id; public IntPtr Name; public int Type; public uint DataFormat; public int DataType;
            public QuantizeParams Quant; public uint Rank; public IntPtr Dimensions; public int MemType; public ClientBuffer Buf;
        }
        // Qnn_Tensor_t = version(8 w/pad) + union(max(v1=112, v2=136)) = 144. The struct MUST be the full
        // size or node-input-array striding reads the wrong offsets (the v73 0x1775 failure).
        [StructLayout(LayoutKind.Sequential, Size = 144)]
        struct Tensor { public int Version; public TensorV1 V1; }
        [StructLayout(LayoutKind.Sequential)]
        struct OpConfigV1
        {
            public IntPtr Name, PackageName, TypeName;
            public uint NumParams; public IntPtr Params;
            public uint NumInputs; public IntPtr Inputs;
            public uint NumOutputs; public IntPtr Outputs;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct OpConfig { public int Version; public OpConfigV1 V1; }

        // QnnInterface table indices (the function table sits at offset 40 in QnnInterface_t).
        const int FnBackendCreate = 1, FnContextCreate = 9, FnContextGetBinarySize = 11, FnContextGetBinary = 12,
                  FnGraphCreate = 15, FnGraphAddNode = 18, FnGraphFinalize = 19, FnGraphExecute = 21,
                  FnTensorCreateGraphTensor = 24;

        static IntPtr AllocCString(string s) => Marshal.StringToHGlobalAnsi(s);

        static Tensor CreateTensor(string name, int type, uint* dims, IntPtr data, uint bytes)
        {
            var t = new Tensor { Version = TensorVersion1 };
            t.V1.Name = AllocCString(name); t.V1.Type = type; t.V1.DataFormat = DataFormatFlat; t.V1.DataType = DataTypeFloat32;
            t.V1.Quant.EncodingDefinition = EncodingUndefined; t.V1.Quant.Encoding = EncodingUndefined;
            t.V1.Rank = 2; t.V1.Dimensions = (IntPtr)dims; t.V1.MemType = MemTypeRaw;
            t.V1.Buf.Data = data; t.V1.Buf.DataSize = bytes;
            return t;
        }

        // Build the proof MatMul (y = x @ W), finalize on the chosen backend, emit the context binary,
        // execute, and verify against an in-method reference. weights/input default to a deterministic
        // synthetic fill (the original CRQ158 proof-of-plumbing); pass a REAL dequantized tile (e.g. a
        // gemma4-e2b MatMulNBits weight, decoded via the sequential-nibble layout in
        // test.dpx.q4-packing-order.ps1) to carry actual model data through the HTP instead. Returns a
        // one-line receipt.
        public string Project(string backendLibrary, string outputBinaryPath, uint featureK = 256, uint featureN = 256,
                               float[] weights = null, float[] input = null)
        {
            IntPtr h = NativeLibrary.Load(backendLibrary);
            var getProviders = (delegate* unmanaged<IntPtr*, uint*, ulong>)NativeLibrary.GetExport(h, "QnnInterface_getProviders");
            IntPtr listPtr; uint providerCount;
            if (getProviders(&listPtr, &providerCount) != 0 || providerCount < 1) return "DpxQnn: no QNN provider";
            byte* table = (byte*)((IntPtr*)listPtr)[0] + 40;
            IntPtr* fn = (IntPtr*)table;

            IntPtr backend, context, graph; ulong rc;
            if ((rc = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, ulong>)fn[FnBackendCreate])(IntPtr.Zero, IntPtr.Zero, &backend)) != 0) return $"DpxQnn: backendCreate 0x{rc:x}";
            if ((rc = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, ulong>)fn[FnContextCreate])(backend, IntPtr.Zero, IntPtr.Zero, &context)) != 0) return $"DpxQnn: contextCreate 0x{rc:x}";
            if ((rc = ((delegate* unmanaged<IntPtr, byte*, IntPtr, IntPtr*, ulong>)fn[FnGraphCreate])(context, (byte*)AllocCString("matmul"), IntPtr.Zero, &graph)) != 0) return $"DpxQnn: graphCreate 0x{rc:x}";

            if (weights == null)
            {
                weights = new float[featureK * featureN];
                ulong state = 88172645463325252UL;
                for (int i = 0; i < weights.Length; i++) { state ^= state << 13; state ^= state >> 7; state ^= state << 17; weights[i] = (float)(((double)(state % 2000000) / 1000000.0 - 1.0) * 0.05); }
            }
            if (input == null)
            {
                input = new float[featureK];
                ulong state = 42;
                for (int i = 0; i < input.Length; i++) { state ^= state << 13; state ^= state >> 7; state ^= state << 17; input[i] = (float)((double)(state % 2000000) / 1000000.0 - 1.0); }
            }
            if (weights.Length != featureK * featureN) return $"DpxQnn: weights.Length={weights.Length} != featureK*featureN={featureK * featureN}";
            if (input.Length != featureK) return $"DpxQnn: input.Length={input.Length} != featureK={featureK}";
            var reference = new float[featureN]; var output = new float[featureN];
            for (int n = 0; n < featureN; n++) { double a = 0; for (int k = 0; k < featureK; k++) a += (double)input[k] * weights[k * featureN + n]; reference[n] = (float)a; }

            uint* dimsX = (uint*)Marshal.AllocHGlobal(8); dimsX[0] = 1; dimsX[1] = featureK;
            uint* dimsW = (uint*)Marshal.AllocHGlobal(8); dimsW[0] = featureK; dimsW[1] = featureN;
            uint* dimsY = (uint*)Marshal.AllocHGlobal(8); dimsY[0] = 1; dimsY[1] = featureN;
            IntPtr weightMemory = Marshal.AllocHGlobal((int)(weights.Length * 4)); Marshal.Copy(weights, 0, weightMemory, weights.Length);

            Tensor tx = CreateTensor("x", TypeAppWrite, dimsX, IntPtr.Zero, 0);
            Tensor tw = CreateTensor("W", TypeStatic, dimsW, weightMemory, (uint)(weights.Length * 4));
            Tensor ty = CreateTensor("y", TypeAppRead, dimsY, IntPtr.Zero, 0);
            var createTensor = (delegate* unmanaged<IntPtr, void*, ulong>)fn[FnTensorCreateGraphTensor];
            if ((rc = createTensor(graph, &tx)) != 0) return $"DpxQnn: tensor x 0x{rc:x}";
            if ((rc = createTensor(graph, &tw)) != 0) return $"DpxQnn: tensor W 0x{rc:x}";
            if ((rc = createTensor(graph, &ty)) != 0) return $"DpxQnn: tensor y 0x{rc:x}";

            var inputs = (Tensor*)Marshal.AllocHGlobal(sizeof(Tensor) * 2); inputs[0] = tx; inputs[1] = tw;
            var outputs = (Tensor*)Marshal.AllocHGlobal(sizeof(Tensor)); outputs[0] = ty;
            var op = new OpConfig { Version = OpConfigVersion1 };
            op.V1.Name = AllocCString("mm0"); op.V1.PackageName = AllocCString("qti.aisw"); op.V1.TypeName = AllocCString("MatMul");
            op.V1.NumInputs = 2; op.V1.Inputs = (IntPtr)inputs; op.V1.NumOutputs = 1; op.V1.Outputs = (IntPtr)outputs;
            if ((rc = ((delegate* unmanaged<IntPtr, OpConfig, ulong>)fn[FnGraphAddNode])(graph, op)) != 0) return $"DpxQnn: addNode 0x{rc:x}";
            if ((rc = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, ulong>)fn[FnGraphFinalize])(graph, IntPtr.Zero, IntPtr.Zero)) != 0) return $"DpxQnn: finalize 0x{rc:x}";

            long emitted = 0;
            if (!string.IsNullOrEmpty(outputBinaryPath))
            {
                ulong size;
                if (((delegate* unmanaged<IntPtr, ulong*, ulong>)fn[FnContextGetBinarySize])(context, &size) == 0 && size > 0)
                {
                    IntPtr buffer = Marshal.AllocHGlobal((int)size); ulong written;
                    if (((delegate* unmanaged<IntPtr, void*, ulong, ulong*, ulong>)fn[FnContextGetBinary])(context, (void*)buffer, size, &written) == 0)
                    {
                        var bytes = new byte[written]; Marshal.Copy(buffer, bytes, 0, (int)written);
                        File.WriteAllBytes(outputBinaryPath, bytes); emitted = (long)written;
                    }
                    Marshal.FreeHGlobal(buffer);
                }
            }

            IntPtr inputMemory = Marshal.AllocHGlobal((int)(featureK * 4)); Marshal.Copy(input, 0, inputMemory, (int)featureK);
            IntPtr outputMemory = Marshal.AllocHGlobal((int)(featureN * 4));
            tx.V1.Buf.Data = inputMemory; tx.V1.Buf.DataSize = featureK * 4;
            ty.V1.Buf.Data = outputMemory; ty.V1.Buf.DataSize = featureN * 4;
            var executeInputs = (Tensor*)Marshal.AllocHGlobal(sizeof(Tensor)); executeInputs[0] = tx;
            var executeOutputs = (Tensor*)Marshal.AllocHGlobal(sizeof(Tensor)); executeOutputs[0] = ty;
            if ((rc = ((delegate* unmanaged<IntPtr, void*, uint, void*, uint, IntPtr, IntPtr, ulong>)fn[FnGraphExecute])(graph, executeInputs, 1, executeOutputs, 1, IntPtr.Zero, IntPtr.Zero)) != 0) return $"DpxQnn: execute 0x{rc:x}";
            Marshal.Copy(outputMemory, output, 0, (int)featureN);

            double maxAbs = 0, referenceMax = 0;
            for (int n = 0; n < featureN; n++) { double d = Math.Abs((double)output[n] - reference[n]); if (d > maxAbs) maxAbs = d; if (Math.Abs(reference[n]) > referenceMax) referenceMax = Math.Abs(reference[n]); }
            double maxRelative = maxAbs / (referenceMax > 1e-9 ? referenceMax : 1.0);

            Marshal.FreeHGlobal((IntPtr)dimsX); Marshal.FreeHGlobal((IntPtr)dimsW); Marshal.FreeHGlobal((IntPtr)dimsY);
            Marshal.FreeHGlobal(weightMemory); Marshal.FreeHGlobal((IntPtr)inputs); Marshal.FreeHGlobal((IntPtr)outputs);
            Marshal.FreeHGlobal(inputMemory); Marshal.FreeHGlobal(outputMemory);
            Marshal.FreeHGlobal((IntPtr)executeInputs); Marshal.FreeHGlobal((IntPtr)executeOutputs);

            string verdict = maxRelative < 2e-2 ? "PASS" : "FAIL";
            return $"DpxQnn.Project: {backendLibrary} K={featureK} N={featureN} bin={emitted}B maxRel={maxRelative:F5} {verdict}";
        }
    }
}
