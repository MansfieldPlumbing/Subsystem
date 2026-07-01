// OnnxProto.cs — home-rolled ONNX protobuf (read + write) for the dp-onnx subset.
//
// Replaces Google.Protobuf + Grpc.Tools so dp-onnx folds into the in-proc Roslyn build
// (`ss build self`) with no codegen step and no NuGet dependency — own the artifact (CRQ143).
// Only the messages the interpreter actually touches are modeled; unknown wire fields are
// skipped on read and dropped on write. proto2 wire format: a tag is (field<<3)|wiretype;
// wiretype 0=varint, 1=fixed64, 2=length-delimited, 5=fixed32. ONNX int64 fields are plain
// varint (NOT zigzag/sint), so no zigzag decode is needed.
//
// Surface mirrors the generated types so Program.cs compiles with only `using Google.Protobuf;`
// removed: ModelProto/GraphProto/NodeProto/TensorProto/AttributeProto/ValueInfoProto/TypeProto,
// `T.Parser.ParseFrom(byte[])`, `t.ToByteArray()`, ByteString.{Span,ToStringUtf8}, repeated = List<T>.

using System;
using System.Collections.Generic;
using System.Text;

namespace Onnx;

// ── ByteString: the bytes-field type (proto `bytes`). Mirrors the Google.Protobuf surface used. ──
public sealed class ByteString
{
    private readonly byte[] _b;
    public ByteString(byte[] b) { _b = b ?? Array.Empty<byte>(); }
    public static readonly ByteString Empty = new(Array.Empty<byte>());
    public static ByteString CopyFrom(byte[] b) => new((byte[])b.Clone());
    public static ByteString CopyFromUtf8(string s) => new(Encoding.UTF8.GetBytes(s));
    public int Length => _b.Length;
    public ReadOnlySpan<byte> Span => _b;
    public byte[] ToByteArray() => _b;
    public string ToStringUtf8() => Encoding.UTF8.GetString(_b);
}

// Repeated field = List<T> plus the range-Add overload the generated RepeatedField<T> exposes
// (Program.cs calls e.g. Dims.Add(long[]), FloatData.Add(float[]), node.Input.Add(string[])).
public sealed class RepeatedField<T> : List<T>
{
    public void Add(IEnumerable<T> items) => AddRange(items);
}

// ── Low-level wire reader over a span (zero-copy; bytes fields copy out). ──
internal ref struct ProtoReader
{
    private ReadOnlySpan<byte> _s;
    private int _p;
    public ProtoReader(ReadOnlySpan<byte> s) { _s = s; _p = 0; }
    public bool Eof => _p >= _s.Length;

    public ulong ReadVarint()
    {
        ulong r = 0; int shift = 0;
        while (true)
        {
            byte b = _s[_p++];
            r |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return r;
            shift += 7;
            if (shift > 63) throw new FormatException("varint too long");
        }
    }

    public int ReadTag(out int field, out int wire)
    {
        if (Eof) { field = 0; wire = 0; return 0; }
        ulong tag = ReadVarint();
        field = (int)(tag >> 3);
        wire = (int)(tag & 0x7);
        return field;
    }

    public uint ReadFixed32() { uint v = (uint)_s[_p] | (uint)_s[_p + 1] << 8 | (uint)_s[_p + 2] << 16 | (uint)_s[_p + 3] << 24; _p += 4; return v; }
    public ulong ReadFixed64() { ulong lo = ReadFixed32(); ulong hi = ReadFixed32(); return lo | (hi << 32); }
    public float ReadFloat() => BitConverter.Int32BitsToSingle((int)ReadFixed32());
    public double ReadDouble() => BitConverter.Int64BitsToDouble((long)ReadFixed64());

    public ReadOnlySpan<byte> ReadLenDelimited()
    {
        int len = (int)ReadVarint();
        var slice = _s.Slice(_p, len);
        _p += len;
        return slice;
    }
    public byte[] ReadBytes() => ReadLenDelimited().ToArray();
    public string ReadString() => Encoding.UTF8.GetString(ReadLenDelimited());

    // Skip an unknown field by wire type.
    public void Skip(int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(); break;
            case 1: _p += 8; break;
            case 2: { int len = (int)ReadVarint(); _p += len; break; }
            case 5: _p += 4; break;
            default: throw new FormatException($"unknown wire type {wire}");
        }
    }

    // A sub-reader over a length-delimited embedded message.
    public ProtoReader ReadMessage() => new(ReadLenDelimited());
}

// ── Low-level wire writer. ──
internal sealed class ProtoWriter
{
    private readonly List<byte> _b = new();
    public byte[] ToArray() => _b.ToArray();
    public int Count => _b.Count;

    public void WriteRawVarint(ulong v) { while (v >= 0x80) { _b.Add((byte)(v | 0x80)); v >>= 7; } _b.Add((byte)v); }
    public void WriteTag(int field, int wire) => WriteRawVarint(((ulong)field << 3) | (ulong)wire);
    public void WriteFixed32(uint v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 24)); }
    public void WriteFixed64(ulong v) { WriteFixed32((uint)v); WriteFixed32((uint)(v >> 32)); }

    public void WriteVarintField(int field, long v) { WriteTag(field, 0); WriteRawVarint((ulong)v); }
    public void WriteFloatField(int field, float v) { WriteTag(field, 5); WriteFixed32((uint)BitConverter.SingleToInt32Bits(v)); }
    public void WriteDoubleField(int field, double v) { WriteTag(field, 1); WriteFixed64((ulong)BitConverter.DoubleToInt64Bits(v)); }

    public void WriteBytesField(int field, ReadOnlySpan<byte> data)
    {
        WriteTag(field, 2); WriteRawVarint((ulong)data.Length);
        foreach (var x in data) _b.Add(x);
    }
    public void WriteStringField(int field, string s) { if (s == null) return; WriteBytesField(field, Encoding.UTF8.GetBytes(s)); }

    public void WriteMessageField(int field, byte[] msg) { WriteTag(field, 2); WriteRawVarint((ulong)msg.Length); _b.AddRange(msg); }

    // Packed repeated scalars (the canonical ONNX encoding for dims/floats/ints).
    public void WritePackedVarints(int field, IReadOnlyList<long> vals)
    {
        if (vals == null || vals.Count == 0) return;
        var inner = new ProtoWriter();
        foreach (var v in vals) inner.WriteRawVarint((ulong)v);
        WriteMessageField(field, inner.ToArray());
    }
    public void WritePackedFloats(int field, IReadOnlyList<float> vals)
    {
        if (vals == null || vals.Count == 0) return;
        var inner = new ProtoWriter();
        foreach (var v in vals) inner.WriteFixed32((uint)BitConverter.SingleToInt32Bits(v));
        WriteMessageField(field, inner.ToArray());
    }
    public void WritePackedDoubles(int field, IReadOnlyList<double> vals)
    {
        if (vals == null || vals.Count == 0) return;
        var inner = new ProtoWriter();
        foreach (var v in vals) inner.WriteFixed64((ulong)BitConverter.DoubleToInt64Bits(v));
        WriteMessageField(field, inner.ToArray());
    }
}

// Helpers for reading a packed-OR-unpacked repeated scalar field.
internal static class ProtoRepeat
{
    public static void ReadPackedOrSingleVarint(ref ProtoReader r, int wire, List<long> dst)
    {
        if (wire == 2) { var sub = new ProtoReader(r.ReadLenDelimited()); while (!sub.Eof) dst.Add((long)sub.ReadVarint()); }
        else dst.Add((long)r.ReadVarint());
    }
    public static void ReadPackedOrSingleVarintInt(ref ProtoReader r, int wire, List<int> dst)
    {
        if (wire == 2) { var sub = new ProtoReader(r.ReadLenDelimited()); while (!sub.Eof) dst.Add((int)sub.ReadVarint()); }
        else dst.Add((int)r.ReadVarint());
    }
    public static void ReadPackedOrSingleFloat(ref ProtoReader r, int wire, List<float> dst)
    {
        if (wire == 2) { var sub = new ProtoReader(r.ReadLenDelimited()); while (!sub.Eof) dst.Add(sub.ReadFloat()); }
        else dst.Add(r.ReadFloat());
    }
    public static void ReadPackedOrSingleDouble(ref ProtoReader r, int wire, List<double> dst)
    {
        if (wire == 2) { var sub = new ProtoReader(r.ReadLenDelimited()); while (!sub.Eof) dst.Add(sub.ReadDouble()); }
        else dst.Add(r.ReadDouble());
    }
}

// A tiny parser facade so `T.Parser.ParseFrom(bytes)` keeps working.
public sealed class MessageParser<T>
{
    private readonly Func<ReadOnlySpan<byte>, T> _f;
    internal MessageParser(Func<ReadOnlySpan<byte>, T> f) { _f = f; }
    public T ParseFrom(byte[] data) => _f(data);
    public T ParseFrom(ReadOnlySpan<byte> data) => _f(data);
}

// ───────────────────────────── messages ─────────────────────────────

public sealed class ModelProto
{
    public GraphProto Graph { get; set; }
    public static readonly MessageParser<ModelProto> Parser = new(Parse);
    private static ModelProto Parse(ReadOnlySpan<byte> data)
    {
        var m = new ModelProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 7: m.Graph = GraphProto.ParseSpan(r.ReadLenDelimited()); break;   // graph
                default: r.Skip(w); break;
            }
        }
        return m;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        if (Graph != null) p.WriteMessageField(7, Graph.ToByteArray());
        return p.ToArray();
    }
}

public sealed class GraphProto
{
    public RepeatedField<NodeProto> Node { get; set; } = new();
    public string Name { get; set; }
    public RepeatedField<TensorProto> Initializer { get; set; } = new();
    public RepeatedField<ValueInfoProto> Input { get; set; } = new();
    public RepeatedField<ValueInfoProto> Output { get; set; } = new();
    public RepeatedField<ValueInfoProto> ValueInfo { get; set; } = new();
    public static readonly MessageParser<GraphProto> Parser = new(d => ParseSpan(d));
    internal static GraphProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var g = new GraphProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1:  g.Node.Add(NodeProto.ParseSpan(r.ReadLenDelimited())); break;
                case 2:  g.Name = r.ReadString(); break;
                case 5:  g.Initializer.Add(TensorProto.ParseSpan(r.ReadLenDelimited())); break;
                case 11: g.Input.Add(ValueInfoProto.ParseSpan(r.ReadLenDelimited())); break;
                case 12: g.Output.Add(ValueInfoProto.ParseSpan(r.ReadLenDelimited())); break;
                case 13: g.ValueInfo.Add(ValueInfoProto.ParseSpan(r.ReadLenDelimited())); break;
                default: r.Skip(w); break;
            }
        }
        return g;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        foreach (var n in Node) p.WriteMessageField(1, n.ToByteArray());
        if (Name != null) p.WriteStringField(2, Name);
        foreach (var t in Initializer) p.WriteMessageField(5, t.ToByteArray());
        foreach (var v in Input) p.WriteMessageField(11, v.ToByteArray());
        foreach (var v in Output) p.WriteMessageField(12, v.ToByteArray());
        foreach (var v in ValueInfo) p.WriteMessageField(13, v.ToByteArray());
        return p.ToArray();
    }
}

public sealed class NodeProto
{
    public RepeatedField<string> Input { get; set; } = new();
    public RepeatedField<string> Output { get; set; } = new();
    public string Name { get; set; }
    public string OpType { get; set; }
    public string Domain { get; set; }
    public RepeatedField<AttributeProto> Attribute { get; set; } = new();
    public static readonly MessageParser<NodeProto> Parser = new(d => ParseSpan(d));
    internal static NodeProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var n = new NodeProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: n.Input.Add(r.ReadString()); break;
                case 2: n.Output.Add(r.ReadString()); break;
                case 3: n.Name = r.ReadString(); break;
                case 4: n.OpType = r.ReadString(); break;
                case 5: n.Attribute.Add(AttributeProto.ParseSpan(r.ReadLenDelimited())); break;
                case 7: n.Domain = r.ReadString(); break;
                default: r.Skip(w); break;
            }
        }
        return n;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        foreach (var s in Input) p.WriteStringField(1, s);
        foreach (var s in Output) p.WriteStringField(2, s);
        if (Name != null) p.WriteStringField(3, Name);
        if (OpType != null) p.WriteStringField(4, OpType);
        foreach (var a in Attribute) p.WriteMessageField(5, a.ToByteArray());
        if (Domain != null) p.WriteStringField(7, Domain);
        return p.ToArray();
    }
}

public sealed class AttributeProto
{
    public static class Types
    {
        public enum AttributeType
        {
            Undefined = 0, Float = 1, Int = 2, String = 3, Tensor = 4, Graph = 5,
            SparseTensor = 11, TypeProto = 13,
            Floats = 6, Ints = 7, Strings = 8, Tensors = 9, Graphs = 10,
            SparseTensors = 12, TypeProtos = 14
        }
    }
    public string Name { get; set; }
    public Types.AttributeType Type { get; set; }
    public float F { get; set; }
    public long I { get; set; }
    public ByteString S { get; set; }
    public TensorProto T { get; set; }
    public GraphProto G { get; set; }
    public RepeatedField<float> Floats { get; set; } = new();
    public RepeatedField<long> Ints { get; set; } = new();
    public RepeatedField<ByteString> Strings { get; set; } = new();
    public RepeatedField<TensorProto> Tensors { get; set; } = new();
    public static readonly MessageParser<AttributeProto> Parser = new(d => ParseSpan(d));
    internal static AttributeProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var a = new AttributeProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1:  a.Name = r.ReadString(); break;
                case 2:  a.F = r.ReadFloat(); break;
                case 3:  a.I = (long)r.ReadVarint(); break;
                case 4:  a.S = new ByteString(r.ReadBytes()); break;
                case 5:  a.T = TensorProto.ParseSpan(r.ReadLenDelimited()); break;
                case 6:  a.G = GraphProto.ParseSpan(r.ReadLenDelimited()); break;
                case 7:  ProtoRepeat.ReadPackedOrSingleFloat(ref r, w, a.Floats); break;
                case 8:  ProtoRepeat.ReadPackedOrSingleVarint(ref r, w, a.Ints); break;
                case 9:  a.Strings.Add(new ByteString(r.ReadBytes())); break;
                case 10: a.Tensors.Add(TensorProto.ParseSpan(r.ReadLenDelimited())); break;
                case 20: a.Type = (Types.AttributeType)(int)r.ReadVarint(); break;
                default: r.Skip(w); break;
            }
        }
        return a;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        if (Name != null) p.WriteStringField(1, Name);
        if (Type == Types.AttributeType.Float) p.WriteFloatField(2, F);
        if (Type == Types.AttributeType.Int) p.WriteVarintField(3, I);
        if (S != null) p.WriteBytesField(4, S.Span);
        if (T != null) p.WriteMessageField(5, T.ToByteArray());
        if (G != null) p.WriteMessageField(6, G.ToByteArray());
        p.WritePackedFloats(7, Floats);
        p.WritePackedVarints(8, Ints);
        foreach (var s in Strings) p.WriteBytesField(9, s.Span);
        foreach (var t in Tensors) p.WriteMessageField(10, t.ToByteArray());
        if (Type != Types.AttributeType.Undefined) p.WriteVarintField(20, (long)Type);
        return p.ToArray();
    }
}

public sealed class TensorProto
{
    public RepeatedField<long> Dims { get; set; } = new();
    public int DataType { get; set; }
    public RepeatedField<float> FloatData { get; set; } = new();
    public RepeatedField<int> Int32Data { get; set; } = new();
    public RepeatedField<ByteString> StringData { get; set; } = new();
    public RepeatedField<long> Int64Data { get; set; } = new();
    public string Name { get; set; }
    public ByteString RawData { get; set; } = ByteString.Empty;
    public RepeatedField<double> DoubleData { get; set; } = new();
    public RepeatedField<StringStringEntryProto> ExternalData { get; set; } = new();
    public int DataLocation { get; set; }
    public static readonly MessageParser<TensorProto> Parser = new(d => ParseSpan(d));
    internal static TensorProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var t = new TensorProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1:  ProtoRepeat.ReadPackedOrSingleVarint(ref r, w, t.Dims); break;
                case 2:  t.DataType = (int)r.ReadVarint(); break;
                case 4:  ProtoRepeat.ReadPackedOrSingleFloat(ref r, w, t.FloatData); break;
                case 5:  ProtoRepeat.ReadPackedOrSingleVarintInt(ref r, w, t.Int32Data); break;
                case 6:  t.StringData.Add(new ByteString(r.ReadBytes())); break;
                case 7:  ProtoRepeat.ReadPackedOrSingleVarint(ref r, w, t.Int64Data); break;
                case 8:  t.Name = r.ReadString(); break;
                case 9:  t.RawData = new ByteString(r.ReadBytes()); break;
                case 10: ProtoRepeat.ReadPackedOrSingleDouble(ref r, w, t.DoubleData); break;
                case 13: t.ExternalData.Add(StringStringEntryProto.ParseSpan(r.ReadLenDelimited())); break;
                case 14: t.DataLocation = (int)r.ReadVarint(); break;
                default: r.Skip(w); break;
            }
        }
        return t;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        p.WritePackedVarints(1, Dims);
        if (DataType != 0) p.WriteVarintField(2, DataType);
        p.WritePackedFloats(4, FloatData);
        if (Int32Data.Count > 0) { var inner = new ProtoWriter(); foreach (var v in Int32Data) inner.WriteRawVarint((ulong)(uint)v); p.WriteMessageField(5, inner.ToArray()); }
        foreach (var s in StringData) p.WriteBytesField(6, s.Span);
        p.WritePackedVarints(7, Int64Data);
        if (Name != null) p.WriteStringField(8, Name);
        if (RawData != null && RawData.Length > 0) p.WriteBytesField(9, RawData.Span);
        p.WritePackedDoubles(10, DoubleData);
        foreach (var e in ExternalData) p.WriteMessageField(13, e.ToByteArray());
        if (DataLocation != 0) p.WriteVarintField(14, DataLocation);
        return p.ToArray();
    }
}

public sealed class ValueInfoProto
{
    public string Name { get; set; }
    public TypeProto Type { get; set; }
    public static readonly MessageParser<ValueInfoProto> Parser = new(d => ParseSpan(d));
    internal static ValueInfoProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var v = new ValueInfoProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: v.Name = r.ReadString(); break;
                case 2: v.Type = TypeProto.ParseSpan(r.ReadLenDelimited()); break;
                default: r.Skip(w); break;
            }
        }
        return v;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        if (Name != null) p.WriteStringField(1, Name);
        if (Type != null) p.WriteMessageField(2, Type.ToByteArray());
        return p.ToArray();
    }
}

public sealed class TypeProto
{
    public static class Types
    {
        public sealed class Tensor
        {
            public int ElemType { get; set; }
            public TensorShapeProto Shape { get; set; }
            internal static Tensor ParseSpan(ReadOnlySpan<byte> data)
            {
                var t = new Tensor(); var r = new ProtoReader(data);
                while (r.ReadTag(out int f, out int w) != 0)
                {
                    switch (f)
                    {
                        case 1: t.ElemType = (int)r.ReadVarint(); break;
                        case 2: t.Shape = TensorShapeProto.ParseSpan(r.ReadLenDelimited()); break;
                        default: r.Skip(w); break;
                    }
                }
                return t;
            }
            public byte[] ToByteArray()
            {
                var p = new ProtoWriter();
                if (ElemType != 0) p.WriteVarintField(1, ElemType);
                if (Shape != null) p.WriteMessageField(2, Shape.ToByteArray());
                return p.ToArray();
            }
        }
    }
    public Types.Tensor TensorType { get; set; }
    public static readonly MessageParser<TypeProto> Parser = new(d => ParseSpan(d));
    internal static TypeProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var tp = new TypeProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: tp.TensorType = Types.Tensor.ParseSpan(r.ReadLenDelimited()); break;
                default: r.Skip(w); break;
            }
        }
        return tp;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        if (TensorType != null) p.WriteMessageField(1, TensorType.ToByteArray());
        return p.ToArray();
    }
}

public sealed class TensorShapeProto
{
    public sealed class Dimension
    {
        public long DimValue { get; set; }
        public string DimParam { get; set; }
        internal static Dimension ParseSpan(ReadOnlySpan<byte> data)
        {
            var d = new Dimension(); var r = new ProtoReader(data);
            while (r.ReadTag(out int f, out int w) != 0)
            {
                switch (f)
                {
                    case 1: d.DimValue = (long)r.ReadVarint(); break;
                    case 2: d.DimParam = r.ReadString(); break;
                    default: r.Skip(w); break;
                }
            }
            return d;
        }
        public byte[] ToByteArray()
        {
            var p = new ProtoWriter();
            if (DimParam != null) p.WriteStringField(2, DimParam);
            else p.WriteVarintField(1, DimValue);
            return p.ToArray();
        }
    }
    public RepeatedField<Dimension> Dim { get; set; } = new();
    internal static TensorShapeProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var s = new TensorShapeProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: s.Dim.Add(Dimension.ParseSpan(r.ReadLenDelimited())); break;
                default: r.Skip(w); break;
            }
        }
        return s;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        foreach (var d in Dim) p.WriteMessageField(1, d.ToByteArray());
        return p.ToArray();
    }
}

public sealed class StringStringEntryProto
{
    public string Key { get; set; }
    public string Value { get; set; }
    internal static StringStringEntryProto ParseSpan(ReadOnlySpan<byte> data)
    {
        var e = new StringStringEntryProto(); var r = new ProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: e.Key = r.ReadString(); break;
                case 2: e.Value = r.ReadString(); break;
                default: r.Skip(w); break;
            }
        }
        return e;
    }
    public byte[] ToByteArray()
    {
        var p = new ProtoWriter();
        if (Key != null) p.WriteStringField(1, Key);
        if (Value != null) p.WriteStringField(2, Value);
        return p.ToArray();
    }
}
