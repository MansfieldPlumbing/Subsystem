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
}
