// LiteRt.cs — home-rolled FlatBuffers reader for `.tflite` graphs and the `.litertlm` container,
// the FlatBuffers sibling of OnnxProto.cs (which does the same for ONNX protobuf).
//
// Why: the only Gemma weights on the box are gemma-4-E2B-it.litertlm (a LITERTLM container of
// .tflite FlatBuffers). Rather than a second engine, we TRANSLATE a .tflite subgraph into the SAME
// Onnx.ModelProto IR so Interp + probe run unchanged. Coverage = the BuiltinOperator -> OpType map
// (what `dp-onnx probe <file>.litertlm` prints). No FlatBuffers lib, no LiteRT lib.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Onnx;

// Minimal FlatBuffers wire reader: random access over a buffer, vtable-aware. FlatBuffers layout —
// a uoffset (u32, points FORWARD from its own location) at buffer[0] locates the root table; a table
// stores a SIGNED soffset (i32) at its start, vtable = table - soffset; the vtable is
// [u16 vtableBytes][u16 tableBytes][u16 fieldOffset...] and field f (0-based) sits at table + that
// offset (0 = field absent, use default); strings/vectors/sub-tables are reached through a uoffset.
public readonly ref struct FlatReader
{
    readonly ReadOnlySpan<byte> _b;
    public FlatReader(ReadOnlySpan<byte> b) { _b = b; }
    public int Length => _b.Length;

    public byte U8(int o) => _b[o];
    public sbyte I8(int o) => (sbyte)_b[o];
    public ushort U16(int o) => (ushort)(_b[o] | _b[o + 1] << 8);
    public short I16(int o) => (short)U16(o);
    public uint U32(int o) => (uint)(_b[o] | _b[o + 1] << 8 | _b[o + 2] << 16 | _b[o + 3] << 24);
    public int I32(int o) => (int)U32(o);
    public ulong U64(int o) => U32(o) | (ulong)U32(o + 4) << 32;
    public long I64(int o) => (long)U64(o);
    public float F32(int o) => BitConverter.Int32BitsToSingle(I32(o));

    // Absolute offset of the root table (uoffset at the buffer start, points forward from 0).
    public int Root => (int)U32(0);

    // The 4-byte file_identifier at offset 4 (e.g. "TFL3" for tflite), "" if it isn't ASCII-printable.
    public string FileId => _b.Length >= 8 ? Encoding.ASCII.GetString(_b.Slice(4, 4)) : "";

    // Absolute data offset of field f within `table`, or 0 when the field is absent (use the default).
    public int Field(int table, int f)
    {
        int vt = table - I32(table);
        int vtBytes = U16(vt);
        int slot = 4 + 2 * f;
        if (slot >= vtBytes) return 0;
        int rel = U16(vt + slot);
        return rel == 0 ? 0 : table + rel;
    }

    // Follow a uoffset stored at absolute offset `o` (relative to its own location, points forward).
    public int Deref(int o) => o + (int)U32(o);

    // String field -> managed string (null when the field is absent). Layout: uoffset -> [u32 len][utf8].
    public string Str(int fieldLoc)
    {
        if (fieldLoc == 0) return null;
        int p = Deref(fieldLoc);
        int len = (int)U32(p);
        return Encoding.UTF8.GetString(_b.Slice(p + 4, len));
    }

    // Vector field -> (absolute offset of element 0, count). Scalar elements are inline at stride;
    // string/table elements are each a uoffset at start + i*4 (follow with Deref).
    public (int start, int count) Vector(int fieldLoc)
    {
        if (fieldLoc == 0) return (0, 0);
        int p = Deref(fieldLoc);
        return (p + 4, (int)U32(p));
    }

    // Sub-table field -> absolute table offset (0 when absent). The slot holds a uoffset to the table.
    public int Sub(int fieldLoc) => fieldLoc == 0 ? 0 : Deref(fieldLoc);

    // Scalar field reads with an explicit default (FlatBuffers omits fields equal to their default).
    public int FieldI32(int table, int f, int dflt = 0) { int o = Field(table, f); return o == 0 ? dflt : I32(o); }
    public uint FieldU32(int table, int f, uint dflt = 0) { int o = Field(table, f); return o == 0 ? dflt : U32(o); }
    public int FieldU8(int table, int f, int dflt = 0) { int o = Field(table, f); return o == 0 ? dflt : U8(o); }
    public long FieldI64(int table, int f, long dflt = 0) { int o = Field(table, f); return o == 0 ? dflt : I64(o); }
    public float FieldF32(int table, int f, float dflt = 0) { int o = Field(table, f); return o == 0 ? dflt : F32(o); }

    // Typed scalar-vector reads (copy out; absent/empty -> zero-length). fieldLoc = Field(table, f); elements are
    // inline at `start + k*stride`. Used by the .tflite -> ModelProto translator for shapes/indices/quant/weights.
    public int[] VecI32(int fieldLoc) { var (s, c) = Vector(fieldLoc); var a = new int[c]; for (int k = 0; k < c; k++) a[k] = I32(s + k * 4); return a; }
    public long[] VecI64(int fieldLoc) { var (s, c) = Vector(fieldLoc); var a = new long[c]; for (int k = 0; k < c; k++) a[k] = I64(s + k * 8); return a; }
    public float[] VecF32(int fieldLoc) { var (s, c) = Vector(fieldLoc); var a = new float[c]; for (int k = 0; k < c; k++) a[k] = F32(s + k * 4); return a; }
    public byte[] VecBytes(int fieldLoc) { var (s, c) = Vector(fieldLoc); return _b.Slice(s, c).ToArray(); }
}

// LITERTLM container (google-ai-edge/LiteRT-LM, litertlm_header_schema.fbs): ASCII magic "LITERTLM",
// then a FlatBuffer LiteRTLMMetaData{ system_metadata[0], section_metadata[1] }; SectionMetadata{
// objects[0] }; SectionObject{ items[0], begin_offset[1]:ulong, end_offset[2]:ulong, data_type[3]:ubyte }.
// begin/end are ABSOLUTE file byte offsets. We stream — the file is >2GB so ReadAllBytes would overflow
// Array.MaxLength; the directory lives in the first block, the payloads are seeked.
public static class LiteRtLm
{
    public readonly record struct Section(int DataType, long Begin, long End);

    // AnySectionDataType enum (litertlm_header_schema.fbs).
    public static string TypeName(int t) => t switch
    {
        1 => "GenericBinary", 3 => "TFLiteModel", 4 => "SP_Tokenizer",
        5 => "LlmMetadata", 6 => "HF_Tokenizer_Zlib", 7 => "TFLiteWeights", _ => $"type{t}"
    };

    public static List<Section> ReadSections(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < 16) throw new InvalidDataException("not a LITERTLM file (too small)");
        int take = (int)Math.Min(fs.Length, 1 << 16);
        var hdr = new byte[take];
        ReadFull(fs, hdr, take);
        if (Encoding.ASCII.GetString(hdr, 0, 8) != "LITERTLM") throw new InvalidDataException("bad LITERTLM magic");
        // Metadata FlatBuffer base = past the fixed header (observed 32: magic[8]+u32 ver+u32+u64+u64 size).
        foreach (int b in new[] { 32, 24, 16, 40, 8, 0 })
            if (TryDir(hdr, b, fs.Length, out var secs)) return secs;
        throw new InvalidDataException("could not locate the LITERTLM section directory");
    }

    // Parse the section directory assuming the metadata FlatBuffer base is `b`. Structural validation
    // (root in range, sane count, offsets inside the file) rejects a wrong base; the real base parses clean.
    static bool TryDir(byte[] hdr, int b, long fileLen, out List<Section> secs)
    {
        secs = new();
        try
        {
            if (b + 4 > hdr.Length) return false;
            var fr = new FlatReader(hdr.AsSpan(b));
            int root = fr.Root;
            if (root <= 0 || root + 4 > hdr.Length - b) return false;
            int secMeta = fr.Sub(fr.Field(root, 1));
            if (secMeta == 0) return false;
            var (start, count) = fr.Vector(fr.Field(secMeta, 0));
            if (count <= 0 || count > 4096) return false;
            for (int i = 0; i < count; i++)
            {
                int ot = fr.Deref(start + i * 4);
                long begin = fr.FieldI64(ot, 1);
                long end = fr.FieldI64(ot, 2);
                int dt = fr.FieldU8(ot, 3);
                if (begin < 0 || end <= begin || end > fileLen) return false;
                secs.Add(new Section(dt, begin, end));
            }
            return secs.Count > 0;
        }
        catch { return false; }
    }

    // Seek+read one section's bytes (each section < 2GB; the GB bulk is split across TFLiteWeights).
    public static byte[] ReadSectionBytes(string path, Section s)
    {
        long len = s.End - s.Begin;
        if (len > Array.MaxLength) throw new InvalidDataException($"section {len} bytes > Array.MaxLength — needs chunked load");
        var buf = new byte[len];
        using var fs = File.OpenRead(path);
        fs.Seek(s.Begin, SeekOrigin.Begin);
        ReadFull(fs, buf, buf.Length);
        return buf;
    }

    static void ReadFull(Stream s, byte[] buf, int n) { int off = 0; while (off < n) { int r = s.Read(buf, off, n - off); if (r <= 0) break; off += r; } }
}

// .tflite graph reader (tensorflow lite schema.fbs). For the probe we enumerate the operator set; the
// full ModelProto translation (with weights) is the next slice. Field indices are positional vtable
// order from schema.fbs:
//   Model{ version[0], operator_codes[1], subgraphs[2], description[3], buffers[4], ... }
//   OperatorCode{ deprecated_builtin_code[0]:byte, custom_code[1]:string, version[2], builtin_code[3]:int }
//   SubGraph{ tensors[0], inputs[1], outputs[2], operators[3], name[4] }
//   Operator{ opcode_index[0]:uint, inputs[1], outputs[2], builtin_options[3], custom_options[4], ... }
public static class Tflite
{
    // BuiltinOperator code -> name (the LLM-relevant subset; unknown -> OP_<code>). builtin_code resolves
    // as max(deprecated_builtin_code, builtin_code) per the tflite GetBuiltinCode rule.
    static readonly Dictionary<int, string> Builtin = new()
    {
        [0] = "ADD", [2] = "CONCATENATION", [3] = "CONV_2D", [4] = "DEPTHWISE_CONV_2D", [6] = "DEQUANTIZE",
        [7] = "EMBEDDING_LOOKUP", [9] = "FULLY_CONNECTED", [14] = "LOGISTIC", [18] = "MUL", [22] = "RESHAPE",
        [25] = "SOFTMAX", [28] = "TANH", [32] = "CUSTOM", [34] = "PAD", [36] = "GATHER", [39] = "TRANSPOSE",
        [40] = "MEAN", [41] = "SUB", [42] = "DIV", [45] = "STRIDED_SLICE", [49] = "SPLIT", [53] = "CAST",
        [55] = "MAXIMUM", [58] = "LESS", [62] = "GREATER_EQUAL", [64] = "SELECT", [65] = "SLICE", [66] = "SIN",
        [69] = "TILE", [71] = "EQUAL", [72] = "NOT_EQUAL", [74] = "SUM", [76] = "RSQRT", [82] = "REDUCE_MAX",
        [83] = "PACK", [84] = "LOGICAL_OR", [85] = "ONE_HOT", [86] = "LOGICAL_AND", [87] = "LOGICAL_NOT",
        [88] = "UNPACK", [94] = "FILL", [95] = "FLOOR_MOD", [108] = "COS", [114] = "QUANTIZE", [123] = "SELECT_V2",
        [126] = "BATCH_MATMUL", [140] = "REDUCE_ALL", [150] = "GELU", [151] = "DYNAMIC_UPDATE_SLICE",
        [158] = "SIGN", [206] = "STABLEHLO_COMPOSITE",
    };

    // tflite op name -> dp-onnx (ONNX) OpType when an existing kernel covers it; null = no mapping yet
    // (shows MISSING in the probe — the work the probe is meant to surface).
    static readonly Dictionary<string, string> ToOnnx = new()
    {
        ["ADD"] = "Add", ["SUB"] = "Sub", ["MUL"] = "Mul", ["DIV"] = "Div", ["CONCATENATION"] = "Concat",
        ["RESHAPE"] = "Reshape", ["TRANSPOSE"] = "Transpose", ["SOFTMAX"] = "Softmax", ["TANH"] = "Tanh",
        ["GATHER"] = "Gather", ["EMBEDDING_LOOKUP"] = "Gather", ["MEAN"] = "ReduceMean", ["CAST"] = "Cast",
        ["SLICE"] = "Slice", ["STRIDED_SLICE"] = "Slice", ["SPLIT"] = "Split", ["GELU"] = "Gelu",
        ["LOGISTIC"] = "Sigmoid", ["FULLY_CONNECTED"] = "Gemm", ["BATCH_MATMUL"] = "MatMul",
        ["CONV_2D"] = "Conv", ["PAD"] = "Pad", ["MAXIMUM"] = "Max", ["LESS"] = "Less", ["SIN"] = "Sin",
        ["GREATER_EQUAL"] = "GreaterOrEqual", ["SELECT"] = "Where", ["SELECT_V2"] = "Where", ["COS"] = "Cos",
        ["TILE"] = "Tile", ["EQUAL"] = "Equal", ["SUM"] = "ReduceSum", ["LOGICAL_OR"] = "Or", ["SIGN"] = "Sign",
        ["LOGICAL_AND"] = "And", ["LOGICAL_NOT"] = "Not", ["FLOOR_MOD"] = "Mod",
        ["RSQRT"] = "Rsqrt", ["NOT_EQUAL"] = "NotEqual", ["DEQUANTIZE"] = "DequantizeLinear", ["QUANTIZE"] = "QuantizeLinear",
        ["UNPACK"] = "Unpack", ["DYNAMIC_UPDATE_SLICE"] = "DynamicUpdateSlice", ["PACK"] = "Pack", ["FILL"] = "Fill", ["REDUCE_MAX"] = "ReduceMax",
    };

    public static string MapToOnnx(string tfliteName) => ToOnnx.TryGetValue(tfliteName, out var v) ? v : null;

    public static (Dictionary<string, int> hist, int subgraphs, int tensors, int ops) OpHistogram(byte[] tfl)
    {
        var fr = new FlatReader(tfl);
        int model = fr.Root;
        var (ocStart, ocCount) = fr.Vector(fr.Field(model, 1));   // operator_codes
        var names = new string[ocCount];
        for (int i = 0; i < ocCount; i++)
        {
            int oc = fr.Deref(ocStart + i * 4);
            int code = Math.Max(fr.FieldU8(oc, 0), fr.FieldI32(oc, 3));   // max(deprecated, builtin_code)
            names[i] = code == 32 ? "CUSTOM:" + (fr.Str(fr.Field(oc, 1)) ?? "?")
                                  : (Builtin.TryGetValue(code, out var nm) ? nm : "OP_" + code);
        }
        var hist = new Dictionary<string, int>();
        int subgraphs = 0, tensors = 0, ops = 0;
        var (sgStart, sgCount) = fr.Vector(fr.Field(model, 2));   // subgraphs
        for (int g = 0; g < sgCount; g++)
        {
            subgraphs++;
            int sg = fr.Deref(sgStart + g * 4);
            var (_, tCount) = fr.Vector(fr.Field(sg, 0));         // tensors
            tensors += tCount;
            var (opStart, opCount) = fr.Vector(fr.Field(sg, 3));  // operators
            for (int o = 0; o < opCount; o++)
            {
                int op = fr.Deref(opStart + o * 4);
                int ix = (int)fr.FieldU32(op, 0);                 // opcode_index
                string nm = (ix >= 0 && ix < names.Length) ? names[ix] : "OP_?";
                hist[nm] = hist.GetValueOrDefault(nm) + 1;
                ops++;
            }
        }
        return (hist, subgraphs, tensors, ops);
    }

    // ── probe -> executable: translate ONE .tflite SubGraph into the same Onnx.ModelProto IR that Interp runs. ──
    // Field indices are positional vtable order from schema.fbs (extends the comment at the top of this class):
    //   Tensor{ shape[0]:[int], type[1]:byte(TensorType), buffer[2]:uint, name[3]:string, quantization[4]:QuantizationParameters }
    //   Buffer{ data[0]:[ubyte] }   (Model.buffers[4]; Tensor.buffer indexes it; buffer 0 = the empty/no-data buffer)
    //   QuantizationParameters{ min[0], max[1], scale[2]:[float], zero_point[3]:[long], details[4], quantized_dimension[5]:int }
    //   Operator (union): opcode_index[0], inputs[1]:[int], outputs[2]:[int], builtin_options_type[3]:byte, builtin_options[4]:table
    //   ReshapeOptions{ new_shape[0]:[int] }   GatherOptions{ axis[0]:int, batch_dims[1]:int }
    // Weights are inlined into TensorProto (Interp.FromProto has no external-data path). Interp.FromProto can't load
    // int8/uint8/int16 initializers, so those are widened to INT32 here; quantized weights then flow through a
    // synthesized DequantizeLinear (per the verified Interp contract: scale@input1, zero_point@input2, per-tensor
    // when scale is scalar). One op convention differs from ONNX and is fixed here: tflite EMBEDDING_LOOKUP is
    // (indices, table) but ONNX Gather is (data=table, indices) -> the two inputs are swapped.

    sealed class TT { public string Name; public int[] Shape; public int Type; public bool Const;
                      public float[] QScale; public long[] QZero; public int QDim; }

    // ONNX TensorProto.DataType: FLOAT=1, INT32=6, INT64=7, BOOL=9, FLOAT16=10, DOUBLE=11, BFLOAT16=16.
    static int OnnxElem(int tfliteType) => tfliteType switch
    {
        0 => 1, 1 => 10, 2 => 6, 4 => 7, 6 => 9, 10 => 11, 18 => 16,   // direct
        3 or 9 or 7 or 16 => 6,                                        // uint8/int8/int16/uint16 -> INT32 (Interp can't load these)
        15 or 12 => 7,                                                 // uint32/uint64 -> INT64
        17 or 19 => 1,                                                 // int4 (17) / packed-4bit (19) -> dequantized to FLOAT
        _ => throw new NotImplementedException($"tflite TensorType {tfliteType}")
    };

    // Sub-byte packed quantized weights (tflite TensorType 17/INT4, 19/packed) -> dequantized FLOAT initializer.
    // The bit width is derived from the buffer (gemma's tied embedding table [262144,1536] packs at 2 bits/elem =
    // 384 bytes/row). Layout assumed: little-endian sub-elements (low bits first), signed (q -= 2^bits when the top
    // bit is set), per-row scale along quantized_dimension 0 (zero_point 0). value = (q - zero) * scale[row].
    // Interp has no sub-byte tensor, so the table is materialized as float here (lazy-gather is a later slice);
    // exact numerical parity vs the litert oracle is verified in a later milestone — this is the executability path.
    static TensorProto DequantSubByte(string name, int[] shape, byte[] packed, float[] scale, long[] zero, int qdim)
    {
        long rows = shape.Length > 0 ? shape[0] : 1;
        long cols = 1; for (int k = 1; k < shape.Length; k++) cols *= shape[k];
        long total = rows * cols;
        if (total <= 0) throw new InvalidDataException($"sub-byte tensor {name}: empty shape [{string.Join(",", shape)}]");
        int bits = (int)((long)packed.Length * 8 / total);                  // 2 / 4 / 8
        if (bits is not (2 or 4 or 8)) throw new NotImplementedException($"sub-byte tensor {name}: {bits} bits/elem (bytes={packed.Length} elems={total})");
        int perByte = 8 / bits, mask = (1 << bits) - 1, half = 1 << (bits - 1);
        bool perRow = scale != null && scale.Length == rows && rows > 1;
        if (!perRow && scale != null && scale.Length > 1)
            throw new NotImplementedException($"sub-byte {name}: only per-row(axis0)/per-tensor quant (qdim={qdim} scaleLen={scale.Length})");
        var outb = new byte[total * 4];
        for (long r = 0; r < rows; r++)
        {
            float s = scale == null || scale.Length == 0 ? 1f : (perRow ? scale[r] : scale[0]);
            long z = zero != null && zero.Length > 0 ? zero[perRow ? r : 0] : 0;
            for (long c = 0; c < cols; c++)
            {
                long idx = r * cols + c;
                int q = (packed[(int)(idx / perByte)] >> (int)((idx % perByte) * bits)) & mask;
                if (q >= half) q -= (1 << bits);                 // signed
                int fb = BitConverter.SingleToInt32Bits((q - z) * s);
                int o = (int)(idx << 2);
                outb[o] = (byte)fb; outb[o + 1] = (byte)(fb >> 8); outb[o + 2] = (byte)(fb >> 16); outb[o + 3] = (byte)(fb >> 24);
            }
        }
        var t = new TensorProto { Name = name, DataType = 1, RawData = new ByteString(outb) };
        foreach (var d in shape) t.Dims.Add((long)d);
        return t;
    }

    static byte[] WidenToI32(byte[] data, int elemBytes, bool signed)
    {
        int n = data.Length / elemBytes;
        var o = new byte[n * 4];
        for (int k = 0; k < n; k++)
        {
            int v = elemBytes == 1 ? (signed ? (sbyte)data[k] : data[k])
                                   : (signed ? (short)(data[2 * k] | data[2 * k + 1] << 8) : (data[2 * k] | data[2 * k + 1] << 8));
            o[4 * k] = (byte)v; o[4 * k + 1] = (byte)(v >> 8); o[4 * k + 2] = (byte)(v >> 16); o[4 * k + 3] = (byte)(v >> 24);
        }
        return o;
    }

    static TensorProto MakeInit(string name, int[] shape, int ttype, byte[] data)
    {
        var t = new TensorProto { Name = name, DataType = OnnxElem(ttype) };
        foreach (var d in shape) t.Dims.Add((long)d);
        byte[] raw = ttype switch
        {
            3 => WidenToI32(data, 1, false),   // UINT8
            9 => WidenToI32(data, 1, true),    // INT8
            7 => WidenToI32(data, 2, true),    // INT16
            16 => WidenToI32(data, 2, false),  // UINT16
            _ => data                          // direct dtypes keep their little-endian bytes
        };
        t.RawData = new ByteString(raw);
        return t;
    }

    static AttributeProto IntAttr(string name, long v) =>
        new() { Name = name, Type = AttributeProto.Types.AttributeType.Int, I = v };

    static TensorProto I64Init(string name, long[] vals)
    { var t = new TensorProto { Name = name, DataType = 7 }; t.Dims.Add((long)vals.Length); foreach (var v in vals) t.Int64Data.Add(v); return t; }

    // Read a constant tensor's integer values (INT32/INT64 buffer) at translation time — for paddings, perm,
    // begin/size that tflite carries as a constant input but ONNX wants as an attribute or a reshaped initializer.
    static long[] ConstInts(byte[] tfl, int tStart, int bufStart, int bufCount, int ix)
    {
        var fr = new FlatReader(tfl);
        int tn = fr.Deref(tStart + ix * 4);
        int type = fr.FieldU8(tn, 1);
        int bufIx = (int)fr.FieldU32(tn, 2);
        if (bufIx <= 0 || bufIx >= bufCount) return Array.Empty<long>();
        int df = fr.Field(fr.Deref(bufStart + bufIx * 4), 0);
        if (df == 0) return Array.Empty<long>();
        var b = fr.VecBytes(df);
        if (type == 2) { var o = new long[b.Length / 4]; for (int k = 0; k < o.Length; k++) o[k] = BitConverter.ToInt32(b, k * 4); return o; }
        if (type == 4) { var o = new long[b.Length / 8]; for (int k = 0; k < o.Length; k++) o[k] = BitConverter.ToInt64(b, k * 8); return o; }
        return Array.Empty<long>();
    }

    // Synthesize ONNX scale/zero_point initializers from a tflite tensor's QuantizationParameters; returns the
    // initializer names + per-axis axis (or -1 when per-tensor / unquantized). scale=FLOAT, zero_point=INT64.
    static (string scale, string zero, int axis) SynthQuant(TT t, GraphProto g)
    {
        if (t.QScale == null || t.QScale.Length == 0) return (null, null, -1);
        bool perAxis = t.QScale.Length > 1;
        var sc = new TensorProto { Name = t.Name + "_scale", DataType = 1 };
        if (perAxis) sc.Dims.Add(t.QScale.Length);
        sc.FloatData.Add(t.QScale);
        var zp = new TensorProto { Name = t.Name + "_zp", DataType = 7 };
        long[] zarr = t.QZero != null && t.QZero.Length == t.QScale.Length ? t.QZero : new long[t.QScale.Length];
        if (perAxis) zp.Dims.Add(zarr.Length);
        zp.Int64Data.Add(zarr);
        g.Initializer.Add(sc); g.Initializer.Add(zp);
        return (sc.Name, zp.Name, perAxis ? t.QDim : -1);
    }

    // Emit a Transpose of `input`'s last two dims (BATCH_MATMUL adj_x/adj_y; ONNX MatMul has no transpose flag).
    static string EmitLastTwoTranspose(GraphProto g, int rank, string input, string outName)
    {
        if (rank < 2) return input;
        var perm = new long[rank]; for (int i = 0; i < rank; i++) perm[i] = i;
        (perm[rank - 2], perm[rank - 1]) = (perm[rank - 1], perm[rank - 2]);
        var tn = new NodeProto { OpType = "Transpose", Name = outName + "_node" };
        tn.Input.Add(input);
        var pa = new AttributeProto { Name = "perm", Type = AttributeProto.Types.AttributeType.Ints };
        foreach (var p in perm) pa.Ints.Add(p);
        tn.Attribute.Add(pa);
        tn.Output.Add(outName);
        g.Node.Add(tn);
        return outName;
    }

    // Spawn-SubGraph: inline decomposition subgraph `sgIx` into `g`. Child inputs -> parentIns, child
    // outputs -> parentOuts (positional); every other value/initializer name is prefixed `pfx` to stay
    // unique across the many composite expansions. ToModelProto allocates the child fresh, so we consume
    // (rename + move) its nodes/initializers -- no clone. Recursive: a decomposition may itself hold composites.
    static void SpawnSubGraph(byte[] tfl, int sgIx, List<string> parentIns, List<string> parentOuts, GraphProto g, string pfx)
    {
        var cg = ToModelProto(tfl, sgIx, out _).Graph;
        // Inner graph-inputs -> parent operands (positional); every other value/init name -> prefixed (unique).
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < cg.Input.Count && i < parentIns.Count; i++) rename[cg.Input[i].Name] = parentIns[i];
        string Map(string nm) => string.IsNullOrEmpty(nm) ? nm : (rename.TryGetValue(nm, out var r) ? r : pfx + nm);
        foreach (var init in cg.Initializer) { init.Name = Map(init.Name); g.Initializer.Add(init); }
        foreach (var nd in cg.Node)
        {
            nd.Name = pfx + nd.Name;
            var ri = nd.Input.Select(Map).ToList(); var ro = nd.Output.Select(Map).ToList();
            nd.Input.Clear(); foreach (var s in ri) nd.Input.Add(s);
            nd.Output.Clear(); foreach (var s in ro) nd.Output.Add(s);
            g.Node.Add(nd);
        }
        // Bind each composite result to the decomposition's i-th output via Identity -- robust to a passthrough
        // output (output == an input, no producing node) that a direct rename would leave dangling.
        for (int i = 0; i < parentOuts.Count && i < cg.Output.Count; i++)
        {
            if (string.IsNullOrEmpty(parentOuts[i])) continue;
            var id = new NodeProto { OpType = "Identity", Name = pfx + "bind" + i };
            id.Input.Add(Map(cg.Output[i].Name));
            id.Output.Add(parentOuts[i]);
            g.Node.Add(id);
        }
    }

    // Translate subgraph `sgIx` of a .tflite model to ModelProto. `summary` carries a structural digest for the receipt.
    public static ModelProto ToModelProto(byte[] tfl, int sgIx, out string summary)
    {
        var fr = new FlatReader(tfl);
        int model = fr.Root;

        var (ocStart, ocCount) = fr.Vector(fr.Field(model, 1));      // operator_codes
        var opNames = new string[ocCount];
        for (int i = 0; i < ocCount; i++)
        {
            int oc = fr.Deref(ocStart + i * 4);
            int code = Math.Max(fr.FieldU8(oc, 0), fr.FieldI32(oc, 3));
            opNames[i] = code == 32 ? "CUSTOM:" + (fr.Str(fr.Field(oc, 1)) ?? "?")
                                    : (Builtin.TryGetValue(code, out var nm) ? nm : "OP_" + code);
        }

        var (sgStart, sgCount) = fr.Vector(fr.Field(model, 2));      // subgraphs
        if (sgIx < 0 || sgIx >= sgCount) throw new ArgumentOutOfRangeException(nameof(sgIx), $"subgraph {sgIx} of {sgCount}");
        int sg = fr.Deref(sgStart + sgIx * 4);
        var (bufStart, bufCount) = fr.Vector(fr.Field(model, 4));    // buffers

        // tensors -> TT[] + initializers from non-empty buffers
        var (tStart, tCount) = fr.Vector(fr.Field(sg, 0));
        var ts = new TT[tCount];
        var g = new GraphProto { Name = $"tflite_sg{sgIx}" };
        int initCount = 0, extWeightTensors = 0;
        for (int i = 0; i < tCount; i++)
        {
            int tn = fr.Deref(tStart + i * 4);
            var info = new TT {
                Shape = fr.VecI32(fr.Field(tn, 0)),
                Type = fr.FieldU8(tn, 1),
                Name = fr.Str(fr.Field(tn, 3)) ?? $"t{i}",
            };
            int bufIx = (int)fr.FieldU32(tn, 2);
            int q = fr.Sub(fr.Field(tn, 4));
            if (q != 0) { info.QScale = fr.VecF32(fr.Field(q, 2)); info.QZero = fr.VecI64(fr.Field(q, 3)); info.QDim = fr.FieldI32(q, 5); }
            byte[] data = null;
            if (bufIx > 0 && bufIx < bufCount)
            {
                int buf = fr.Deref(bufStart + bufIx * 4);
                int dataField = fr.Field(buf, 0);
                if (dataField != 0) data = fr.VecBytes(dataField);
                else if (fr.Field(buf, 1) != 0) extWeightTensors++;   // offset/size external buffer (not inline) — unsupported here
            }
            if (data != null && data.Length > 0)
            {
                info.Const = true;
                g.Initializer.Add(info.Type is 17 or 19
                    ? DequantSubByte(info.Name, info.Shape, data, info.QScale, info.QZero, info.QDim)
                    : MakeInit(info.Name, info.Shape, info.Type, data));
                initCount++;
            }
            ts[i] = info;
        }

        int[] sgIn = fr.VecI32(fr.Field(sg, 1));
        int[] sgOut = fr.VecI32(fr.Field(sg, 2));
        string TName(int ix) => ix < 0 ? "" : ts[ix].Name;
        ValueInfoProto VI(int ix)
        {
            var t = ts[ix];
            var sh = new TensorShapeProto();
            foreach (var d in t.Shape) sh.Dim.Add(new TensorShapeProto.Dimension { DimValue = d });
            return new ValueInfoProto { Name = t.Name, Type = new TypeProto { TensorType = new TypeProto.Types.Tensor { ElemType = OnnxElem(t.Type), Shape = sh } } };
        }
        foreach (int ix in sgIn) if (ix >= 0 && ix < tCount && !ts[ix].Const) g.Input.Add(VI(ix));
        foreach (int ix in sgOut) if (ix >= 0 && ix < tCount) g.Output.Add(VI(ix));

        // operators -> nodes
        var (opStart, opCount) = fr.Vector(fr.Field(sg, 3));
        var sb = new StringBuilder();
        for (int o = 0; o < opCount; o++)
        {
            int op = fr.Deref(opStart + o * 4);
            int ocix = (int)fr.FieldU32(op, 0);
            string tf = (ocix >= 0 && ocix < opNames.Length) ? opNames[ocix] : "OP_?";
            int[] inIx = fr.VecI32(fr.Field(op, 1));
            int[] outIx = fr.VecI32(fr.Field(op, 2));

            // Spawn-SubGraph (CRQ144): a STABLEHLO_COMPOSITE carries a decomposition_subgraph_index in
            // builtin_options_2; spawn that subgraph as a child and splice it inline (no node for the
            // composite itself). The Sub-VOM-is-a-VOM move applied to the graph: recursion, not a fused kernel.
            if (tf == "STABLEHLO_COMPOSITE")
            {
                int bo2 = fr.Sub(fr.Field(op, 12));                 // builtin_options_2 = StableHLOCompositeOptions
                int decompIx = bo2 != 0 ? fr.FieldI32(bo2, 1) : -1; // [1] = decomposition_subgraph_index
                if (decompIx < 0 || decompIx >= sgCount)
                    throw new NotImplementedException($"op[{o}] STABLEHLO_COMPOSITE: bad decomposition_subgraph_index={decompIx} (sgCount={sgCount})");
                var pins = new List<string>(); foreach (var ix in inIx) pins.Add(TName(ix));
                var pouts = new List<string>(); foreach (var ix in outIx) pouts.Add(TName(ix));
                SpawnSubGraph(tfl, decompIx, pins, pouts, g, $"c{sgIx}_{o}_");
                sb.Append($"  op[{o,3}] STABLEHLO_COMPOSITE -> Spawn-SubGraph[{decompIx}] in[{string.Join(",", inIx)}] out[{string.Join(",", outIx)}]\n");
                continue;
            }

            string onnx = MapToOnnx(tf) ?? throw new NotImplementedException($"op[{o}] {tf}: no dp-onnx kernel mapping (subgraph not fully covered)");
            int boType = fr.FieldU8(op, 3);                 // builtin_options_type (union discriminator)
            int boVal = fr.Sub(fr.Field(op, 4));            // builtin_options value table
            var node = new NodeProto { OpType = onnx, Name = $"{tf}_{o}" };
            var ins = new List<string>(); foreach (var ix in inIx) ins.Add(TName(ix));
            var outs = new List<string>(); foreach (var ix in outIx) outs.Add(TName(ix));

            switch (tf)
            {
                case "EMBEDDING_LOOKUP":   // tflite (indices, table) -> ONNX Gather(data=table, indices), axis 0
                    node.Input.Add(ins[1]); node.Input.Add(ins[0]); node.Attribute.Add(IntAttr("axis", 0)); break;
                case "GATHER":
                    foreach (var s in ins) node.Input.Add(s);
                    node.Attribute.Add(IntAttr("axis", boVal != 0 ? fr.FieldI32(boVal, 0) : 0)); break;
                case "RESHAPE":
                    node.Input.Add(ins[0]);
                    if (ins.Count >= 2) node.Input.Add(ins[1]);     // shape carried as input tensor (an initializer)
                    else {                                          // shape only in ReshapeOptions.new_shape -> synth INT64 initializer
                        int[] ns = boVal != 0 ? fr.VecI32(fr.Field(boVal, 0)) : Array.Empty<int>();
                        var st = new TensorProto { Name = node.Name + "_shape", DataType = 7 };
                        st.Dims.Add(ns.Length); foreach (var d in ns) st.Int64Data.Add((long)d);
                        g.Initializer.Add(st); node.Input.Add(st.Name);
                    }
                    break;
                case "DEQUANTIZE": {        // input is a quantized tensor carrying scale/zp -> DequantizeLinear(x, scale, zp)
                    var (scl, zr, ax) = SynthQuant(ts[inIx[0]], g);
                    node.Input.Add(ins[0]); if (scl != null) { node.Input.Add(scl); node.Input.Add(zr); if (ax >= 0) node.Attribute.Add(IntAttr("axis", ax)); }
                    break; }
                case "QUANTIZE": {          // output tensor carries scale/zp -> QuantizeLinear(x, scale, zp)
                    var (scl, zr, ax) = SynthQuant(ts[outIx[0]], g);
                    node.Input.Add(ins[0]); if (scl != null) { node.Input.Add(scl); node.Input.Add(zr); if (ax >= 0) node.Attribute.Add(IntAttr("axis", ax)); }
                    break; }
                case "SUM": case "MEAN": case "REDUCE_MAX":   // ReduceSum/Mean/Max: tflite axes=input[1]; keep_dims=ReducerOptions[0]
                    foreach (var s in ins) node.Input.Add(s);
                    node.Attribute.Add(IntAttr("keepdims", boVal != 0 ? fr.FieldU8(boVal, 0) : 0));
                    break;
                case "FULLY_CONNECTED":    // y = input . weights^T (+ bias) -> Gemm(input, weights, bias), transB=1
                    foreach (var s in ins) if (!string.IsNullOrEmpty(s)) node.Input.Add(s);
                    node.Attribute.Add(IntAttr("transB", 1));
                    { int act = boVal != 0 ? fr.FieldU8(boVal, 0) : 0;     // FullyConnectedOptions.fused_activation_function[0]
                      if (act != 0) throw new NotImplementedException($"op[{o}] FULLY_CONNECTED fused_activation={act} unmapped"); }
                    break;
                case "PAD": {              // tflite paddings [r,2] (begin,end per axis) -> ONNX pads [all begins, all ends]
                    node.Input.Add(ins[0]);
                    long[] pp = ConstInts(tfl, tStart, bufStart, bufCount, inIx[1]);
                    int rr = pp.Length / 2; var onnxPads = new long[rr * 2];
                    for (int d = 0; d < rr; d++) { onnxPads[d] = pp[2 * d]; onnxPads[rr + d] = pp[2 * d + 1]; }
                    var pt = I64Init(node.Name + "_pads", onnxPads); g.Initializer.Add(pt); node.Input.Add(pt.Name);
                    break; }
                case "TRANSPOSE": {        // perm is tflite input[1]; ONNX Transpose carries perm as an attribute
                    node.Input.Add(ins[0]);
                    long[] perm = ConstInts(tfl, tStart, bufStart, bufCount, inIx[1]);
                    var pa = new AttributeProto { Name = "perm", Type = AttributeProto.Types.AttributeType.Ints };
                    foreach (var p in perm) pa.Ints.Add(p);
                    node.Attribute.Add(pa);
                    break; }
                case "CONCATENATION":      // ConcatenationOptions.axis[0] (fused_activation[1] assumed NONE)
                    foreach (var s in ins) node.Input.Add(s);
                    node.Attribute.Add(IntAttr("axis", boVal != 0 ? fr.FieldI32(boVal, 0) : 0));
                    break;
                case "SLICE": {            // tflite SLICE(input, begin, size) -> ONNX Slice(input, starts, ends, axes)
                    node.Input.Add(ins[0]);
                    long[] begin = ConstInts(tfl, tStart, bufStart, bufCount, inIx[1]);
                    long[] size = ConstInts(tfl, tStart, bufStart, bufCount, inIx[2]);
                    int rr = begin.Length; var ends = new long[rr]; var axes = new long[rr];
                    for (int d = 0; d < rr; d++) { ends[d] = size[d] < 0 ? long.MaxValue : begin[d] + size[d]; axes[d] = d; }
                    g.Initializer.Add(I64Init(node.Name + "_starts", begin)); node.Input.Add(node.Name + "_starts");
                    g.Initializer.Add(I64Init(node.Name + "_ends", ends));    node.Input.Add(node.Name + "_ends");
                    g.Initializer.Add(I64Init(node.Name + "_axes", axes));    node.Input.Add(node.Name + "_axes");
                    break; }
                case "BATCH_MATMUL": {     // BatchMatMulOptions.adj_x[0], adj_y[1]; ONNX MatMul has no transpose flag -> insert Transpose on the last two dims
                    int adjX = boVal != 0 ? fr.FieldU8(boVal, 0) : 0, adjY = boVal != 0 ? fr.FieldU8(boVal, 1) : 0;
                    string aN = adjX != 0 ? EmitLastTwoTranspose(g, ts[inIx[0]].Shape.Length, ins[0], node.Name + "_aT") : ins[0];
                    string bN = adjY != 0 ? EmitLastTwoTranspose(g, ts[inIx[1]].Shape.Length, ins[1], node.Name + "_bT") : ins[1];
                    node.Input.Add(aN); node.Input.Add(bN);
                    break; }
                case "UNPACK":             // tflite UnpackOptions{ num[0], axis[1] }: N outputs along axis, axis removed
                    node.Input.Add(ins[0]);
                    node.Attribute.Add(IntAttr("axis", boVal != 0 ? fr.FieldI32(boVal, 1) : 0));
                    break;
                case "DYNAMIC_UPDATE_SLICE":   // (operand, update, start_0..start_{r-1}); all inputs pass through to the kernel
                    foreach (var s in ins) node.Input.Add(s);
                    break;
                case "PACK":               // PackOptions{ values_count[0], axis[1] }: stack N inputs along a new axis
                    foreach (var s in ins) node.Input.Add(s);
                    node.Attribute.Add(IntAttr("axis", boVal != 0 ? fr.FieldI32(boVal, 1) : 0));
                    break;
                case "FILL":               // (dims, value) -> tensor of shape dims filled with value
                    foreach (var s in ins) node.Input.Add(s);
                    break;
                case "GELU":               // GeluOptions{ approximate }; default ONNX Gelu approximate=none (exact erf)
                    node.Input.Add(ins[0]);
                    break;
                default:
                    if (!AttrFreeSafe.Contains(tf))
                        throw new NotImplementedException($"op[{o}] {tf} -> {onnx}: attribute lowering not implemented (slice 2 targets the attribute-free section ops)");
                    foreach (var s in ins) node.Input.Add(s);
                    break;
            }
            foreach (var s in outs) node.Output.Add(s);
            g.Node.Add(node);
            sb.Append($"  op[{o,3}] {tf,-18} -> {onnx,-16} in[{string.Join(",", inIx)}] out[{string.Join(",", outIx)}]\n");
        }

        summary = $"subgraph[{sgIx}] tensors={tCount} ops={opCount} initializers={initCount} inputs={g.Input.Count} outputs={g.Output.Count}"
                + (extWeightTensors > 0 ? $" extWeightTensors={extWeightTensors}(UNSUPPORTED-external-buffer)" : "") + "\n"
                + "  inputs:  " + string.Join("  ", g.Input.Select(v => $"{v.Name}{ShapeStr(v)}")) + "\n"
                + "  outputs: " + string.Join("  ", g.Output.Select(v => $"{v.Name}{ShapeStr(v)}")) + "\n"
                + sb;
        return new ModelProto { Graph = g };
    }

    static string ShapeStr(ValueInfoProto v) =>
        "[" + string.Join(",", v.Type.TensorType.Shape.Dim.Select(d => d.DimValue)) + "]";

    // ONNX ops whose dp-onnx kernels need no attributes for a faithful translation (broadcast/elementwise/select/cast).
    static readonly HashSet<string> AttrFreeSafe = new()
    {
        "ADD","SUB","MUL","DIV","TANH","LOGISTIC","SIN","COS","SIGN","RSQRT","SOFTMAX",
        "EQUAL","NOT_EQUAL","LESS","GREATER_EQUAL","LOGICAL_AND","LOGICAL_OR","LOGICAL_NOT",
        "SELECT","SELECT_V2","MAXIMUM","FLOOR_MOD","CAST",
    };
}
