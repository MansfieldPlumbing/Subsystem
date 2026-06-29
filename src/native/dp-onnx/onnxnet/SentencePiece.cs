// SentencePiece.cs — home-rolled SentencePiece (.spm/.model) reader + Unigram tokenizer: the textual sibling
// of OnnxProto.cs (ONNX protobuf) and LiteRt.cs (.tflite FlatBuffers). No sentencepiece lib, no protobuf lib —
// it parses the SentencePiece ModelProto by hand, then encodes with the Unigram best-path (Viterbi over piece
// scores, per-byte fallback) and detokenizes the inverse. Salvaged from the gemma-talking-layer wip (the only
// sovereign, engine-independent part of it); the model bytes / .spm path are supplied by the caller — nothing
// is hardcoded here. Receipt: tests/test.dp-onnx.sentencepiece.ps1.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Onnx;

// SentencePiece piece kinds (ModelProto.SentencePiece.Type): the closed enum the .spm stores per piece.
public enum SentencePieceType { Normal = 1, Unknown = 2, Control = 3, UserDefined = 4, Unused = 5, Byte = 6 }

public sealed class SentencePiece
{
    public string Piece { get; set; } = "";
    public float Score { get; set; }
    public SentencePieceType Type { get; set; } = SentencePieceType.Normal;
}

public sealed class NormalizerSpec
{
    public string Name { get; set; } = "";
    public byte[] PrecompiledCharsmap { get; set; } = Array.Empty<byte>();
    public bool AddDummyPrefix { get; set; } = true;
    public bool RemoveExtraWhitespaces { get; set; } = true;
    public bool EscapeWhitespaces { get; set; } = true;
}

// The parsed SentencePiece ModelProto: the piece table + the normalizer spec, read straight off the protobuf
// wire (field 1 = pieces, field 3 = normalizer) with no protobuf runtime.
public sealed class SpModelProto
{
    public List<SentencePiece> Pieces { get; } = new();
    public NormalizerSpec Normalizer { get; } = new();

    public static SpModelProto Parse(byte[] data)
    {
        var model = new SpModelProto();
        var r = new SpProtoReader(data);
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: model.Pieces.Add(ParsePiece(r.ReadMessage())); break;
                case 3: ParseNormalizer(r.ReadMessage(), model.Normalizer); break;
                default: r.Skip(w); break;
            }
        }
        return model;
    }

    private static SentencePiece ParsePiece(SpProtoReader r)
    {
        var p = new SentencePiece();
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: p.Piece = r.ReadString(); break;
                case 2: p.Score = r.ReadFloat(); break;
                case 3: p.Type = (SentencePieceType)r.ReadVarint(); break;
                default: r.Skip(w); break;
            }
        }
        return p;
    }

    private static void ParseNormalizer(SpProtoReader r, NormalizerSpec n)
    {
        while (r.ReadTag(out int f, out int w) != 0)
        {
            switch (f)
            {
                case 1: n.Name = r.ReadString(); break;
                case 2: n.PrecompiledCharsmap = r.ReadBytes(); break;
                case 3: n.AddDummyPrefix = r.ReadVarint() != 0; break;
                case 4: n.RemoveExtraWhitespaces = r.ReadVarint() != 0; break;
                case 5: n.EscapeWhitespaces = r.ReadVarint() != 0; break;
                default: r.Skip(w); break;
            }
        }
    }
}

// A minimal protobuf wire reader over a span — the SentencePiece-side twin of OnnxProto's ProtoReader.
public ref struct SpProtoReader
{
    private ReadOnlySpan<byte> _s;
    private int _p;
    public SpProtoReader(ReadOnlySpan<byte> s) { _s = s; _p = 0; }
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

    public ReadOnlySpan<byte> ReadLenDelimited()
    {
        int len = (int)ReadVarint();
        var slice = _s.Slice(_p, len);
        _p += len;
        return slice;
    }
    public byte[] ReadBytes() => ReadLenDelimited().ToArray();
    public string ReadString() => Encoding.UTF8.GetString(ReadLenDelimited());

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

    public SpProtoReader ReadMessage() => new(ReadLenDelimited());
}

// The Unigram tokenizer over a parsed SpModelProto: Encode runs the best-path Viterbi over UTF-8 byte positions
// (each piece's log-prob is its Score; a single-byte <0xNN> fallback covers anything unmatched), Detokenize is
// the inverse with the SentencePiece whitespace marker (U+2581) mapped back to a space.
public sealed class SentencePieceTokenizer
{
    private readonly SpModelProto _model;
    private readonly Dictionary<string, int> _pieceToId = new(StringComparer.Ordinal);
    private readonly string[] _idToPiece;
    private readonly float[] _idToScore;
    private readonly SentencePieceType[] _idToType;
    private readonly int[] _byteToId = new int[256];
    private readonly HashSet<int> _specialTokenIds = new();

    public SentencePieceTokenizer(SpModelProto model)
    {
        _model = model;
        int count = model.Pieces.Count;
        _idToPiece = new string[count];
        _idToScore = new float[count];
        _idToType = new SentencePieceType[count];

        for (int i = 0; i < count; i++)
        {
            var p = model.Pieces[i];
            _idToPiece[i] = p.Piece;
            _idToScore[i] = p.Score;
            _idToType[i] = p.Type;

            if (!_pieceToId.ContainsKey(p.Piece))
                _pieceToId[p.Piece] = i;

            if (p.Type == SentencePieceType.Byte && IsByteHex(p.Piece, out byte b))
                _byteToId[b] = i;

            if (p.Type == SentencePieceType.Control)
                _specialTokenIds.Add(i);
            else if (p.Type == SentencePieceType.UserDefined
                     && p.Piece.StartsWith("<", StringComparison.Ordinal) && p.Piece.EndsWith(">", StringComparison.Ordinal))
                _specialTokenIds.Add(i);
        }
    }

    public int VocabSize => _idToPiece.Length;

    public int FindPieceId(string pieceName) => _pieceToId.TryGetValue(pieceName, out var id) ? id : -1;

    // A "<0xNN>" byte-fallback piece -> its byte value; false for anything else.
    private static bool IsByteHex(string piece, out byte value)
    {
        value = 0;
        return piece.Length == 6
            && piece.StartsWith("<0x", StringComparison.Ordinal) && piece.EndsWith(">", StringComparison.Ordinal)
            && byte.TryParse(piece.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public List<int> Encode(string text)
    {
        string normalized = text;
        if (_model.Normalizer.EscapeWhitespaces)
            normalized = normalized.Replace(" ", "▁");
        if (_model.Normalizer.AddDummyPrefix && (normalized.Length == 0 || normalized[0] != '▁'))
            normalized = "▁" + normalized;
        if (normalized.Length == 0)
            return new List<int>();

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(normalized);
        int len = utf8Bytes.Length;

        var backtraceScore = new float[len + 1];
        var prevNode = new int[len + 1];
        var prevLen = new int[len + 1];
        var tokenId = new int[len + 1];
        for (int i = 1; i <= len; i++) backtraceScore[i] = float.NegativeInfinity;
        backtraceScore[0] = 0;

        int maxPieceBytes = 0;
        foreach (var p in _model.Pieces)
        {
            int pBytes = Encoding.UTF8.GetByteCount(p.Piece);
            if (pBytes > maxPieceBytes) maxPieceBytes = pBytes;
        }
        if (maxPieceBytes == 0) maxPieceBytes = 32;

        var bytesToPiece = new Dictionary<ByteArrayKey, (int id, float score)>();
        foreach (var p in _model.Pieces)
        {
            if (p.Type == SentencePieceType.Control || p.Type == SentencePieceType.Byte) continue;
            byte[] pBytes = Encoding.UTF8.GetBytes(p.Piece);
            if (pBytes.Length > 0)
                bytesToPiece[new ByteArrayKey(pBytes)] = (_pieceToId[p.Piece], p.Score);
        }

        for (int pos = 0; pos < len; pos++)
        {
            byte b = utf8Bytes[pos];
            int fallbackId = _byteToId[b];
            float fallbackScore = _idToScore[fallbackId];

            float scoreWithFallback = backtraceScore[pos] + fallbackScore;
            if (scoreWithFallback > backtraceScore[pos + 1])
            {
                backtraceScore[pos + 1] = scoreWithFallback;
                prevNode[pos + 1] = pos;
                prevLen[pos + 1] = 1;
                tokenId[pos + 1] = fallbackId;
            }

            int limit = Math.Min(len - pos, maxPieceBytes);
            for (int l = 1; l <= limit; l++)
            {
                var key = new ByteArrayKey(utf8Bytes, pos, l);
                if (bytesToPiece.TryGetValue(key, out var pieceInfo))
                {
                    float scoreWithPiece = backtraceScore[pos] + pieceInfo.score;
                    int nextPos = pos + l;
                    if (scoreWithPiece > backtraceScore[nextPos])
                    {
                        backtraceScore[nextPos] = scoreWithPiece;
                        prevNode[nextPos] = pos;
                        prevLen[nextPos] = l;
                        tokenId[nextPos] = pieceInfo.id;
                    }
                }
            }
        }

        var resultIds = new List<int>();
        int curr = len;
        while (curr > 0)
        {
            if (backtraceScore[curr] == float.NegativeInfinity) break;
            resultIds.Add(tokenId[curr]);
            curr = prevNode[curr];
        }
        resultIds.Reverse();
        return resultIds;
    }

    public string Detokenize(IReadOnlyList<int> ids)
    {
        var bytes = new List<byte>();
        foreach (int id in ids)
        {
            if (id < 0 || id >= _idToPiece.Length) continue;
            var type = _idToType[id];
            var piece = _idToPiece[id];

            if (_specialTokenIds.Contains(id))
                continue;
            if (type == SentencePieceType.Byte && IsByteHex(piece, out byte hb))
                bytes.Add(hb);
            else
                bytes.AddRange(Encoding.UTF8.GetBytes(piece));
        }

        string result = Encoding.UTF8.GetString(bytes.ToArray());
        result = result.Replace("▁", " ");
        if (result.StartsWith(" ", StringComparison.Ordinal))
            result = result.Substring(1);
        return result;
    }
}

// A span-or-array slice usable as a dictionary key (value-equal over the byte range) — keeps Encode's piece
// lookup allocation-light while still hashing on content.
public readonly struct ByteArrayKey : IEquatable<ByteArrayKey>
{
    private readonly byte[] _array;
    private readonly int _offset;
    private readonly int _length;
    private readonly int _hashCode;

    public ByteArrayKey(byte[] array) : this(array, 0, array.Length) { }

    public ByteArrayKey(byte[] array, int offset, int length)
    {
        _array = array;
        _offset = offset;
        _length = length;
        int hash = 17;
        for (int i = 0; i < length; i++) hash = hash * 31 + array[offset + i];
        _hashCode = hash;
    }

    public bool Equals(ByteArrayKey other)
    {
        if (_length != other._length) return false;
        for (int i = 0; i < _length; i++)
            if (_array[_offset + i] != other._array[other._offset + i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ByteArrayKey other && Equals(other);
    public override int GetHashCode() => _hashCode;
}
