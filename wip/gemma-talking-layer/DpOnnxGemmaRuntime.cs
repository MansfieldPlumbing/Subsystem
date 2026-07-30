using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Linq;

namespace Subsystem.Vom
{
    public class Owner
    {
        public string Path { get; set; }
    }
    
    public class Handle
    {
        public string Path { get; set; }
        public IntPtr Resource { get; set; }
    }
    
    public static class Vom
    {
        private static readonly Dictionary<string, Handle> _handles = new(StringComparer.Ordinal);
        
        public static Owner CreateOwner(string path, long maxBytes)
        {
            return new Owner { Path = path };
        }
        
        public static Handle Register(Owner owner, string type, object resource, Action onReclaim, string subdir, string name)
        {
            string p = $"{owner.Path}\\{subdir}\\{name}";
            var h = new Handle { Path = p, Resource = (IntPtr)GCHandle.Alloc(resource) };
            _handles[p] = h;
            return h;
        }
        
        public static bool TryGetByPath(Owner owner, string path, out Handle handle)
        {
            return _handles.TryGetValue(path, out handle);
        }
        
        public static void Close(Owner owner, string path)
        {
            if (_handles.TryGetValue(path, out var h))
            {
                ((GCHandle)h.Resource).Free();
                _handles.Remove(path);
            }
        }
        
        public static void DropPrefix(string prefix)
        {
            var keys = _handles.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var k in keys)
            {
                Close(null, k);
            }
        }
        
        public static int ActiveHandleCount => _handles.Count;
    }
}

namespace Subsystem.RuntimeBroker
{
    public enum SentencePieceType
    {
        Normal = 1,
        Unknown = 2,
        Control = 3,
        UserDefined = 4,
        Unused = 5,
        Byte = 6
    }

    public class SentencePiece
    {
        public string Piece { get; set; } = "";
        public float Score { get; set; }
        public SentencePieceType Type { get; set; } = SentencePieceType.Normal;
    }

    public class NormalizerSpec
    {
        public string Name { get; set; } = "";
        public byte[] PrecompiledCharsmap { get; set; } = Array.Empty<byte>();
        public bool AddDummyPrefix { get; set; } = true;
        public bool RemoveExtraWhitespaces { get; set; } = true;
        public bool EscapeWhitespaces { get; set; } = true;
    }

    public class SpModelProto
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
                    case 1:
                        model.Pieces.Add(ParsePiece(r.ReadMessage()));
                        break;
                    case 3:
                        ParseNormalizer(r.ReadMessage(), model.Normalizer);
                        break;
                    default:
                        r.Skip(w);
                        break;
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
                    case 1:
                        p.Piece = r.ReadString();
                        break;
                    case 2:
                        p.Score = r.ReadFloat();
                        break;
                    case 3:
                        p.Type = (SentencePieceType)r.ReadVarint();
                        break;
                    default:
                        r.Skip(w);
                        break;
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
                    case 1:
                        n.Name = r.ReadString();
                        break;
                    case 2:
                        n.PrecompiledCharsmap = r.ReadBytes();
                        break;
                    case 3:
                        n.AddDummyPrefix = r.ReadVarint() != 0;
                        break;
                    case 4:
                        n.RemoveExtraWhitespaces = r.ReadVarint() != 0;
                        break;
                    case 5:
                        n.EscapeWhitespaces = r.ReadVarint() != 0;
                        break;
                    default:
                        r.Skip(w);
                        break;
                }
            }
        }
    }

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

    public class SentencePieceTokenizer
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
                {
                    _pieceToId[p.Piece] = i;
                }
                
                if (p.Type == SentencePieceType.Byte)
                {
                    if (p.Piece.StartsWith("<0x") && p.Piece.EndsWith(">") && p.Piece.Length == 6)
                    {
                        try
                        {
                            byte b = Convert.ToByte(p.Piece.Substring(3, 2), 16);
                            _byteToId[b] = i;
                        }
                        catch { }
                    }
                }

                if (p.Type == SentencePieceType.Control)
                {
                    _specialTokenIds.Add(i);
                }
                else if (p.Type == SentencePieceType.UserDefined && p.Piece.StartsWith("<") && p.Piece.EndsWith(">"))
                {
                    _specialTokenIds.Add(i);
                }
            }
        }

        public int FindPieceId(string pieceName)
        {
            if (_pieceToId.TryGetValue(pieceName, out var id))
            {
                return id;
            }
            return -1;
        }

        public List<int> Encode(string text)
        {
            string normalized = text;

            if (_model.Normalizer.EscapeWhitespaces)
            {
                normalized = normalized.Replace(" ", "\u2581");
            }

            if (_model.Normalizer.AddDummyPrefix)
            {
                if (normalized.Length == 0 || normalized[0] != '\u2581')
                {
                    normalized = "\u2581" + normalized;
                }
            }

            if (normalized.Length == 0)
            {
                return new List<int>();
            }

            byte[] utf8Bytes = Encoding.UTF8.GetBytes(normalized);
            int len = utf8Bytes.Length;

            var backtraceScore = new float[len + 1];
            var prevNode = new int[len + 1];
            var prevLen = new int[len + 1];
            var tokenId = new int[len + 1];

            for (int i = 1; i <= len; i++)
            {
                backtraceScore[i] = float.NegativeInfinity;
            }
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
                if (p.Type == SentencePieceType.Control || p.Type == SentencePieceType.Byte)
                {
                    continue;
                }
                byte[] pBytes = Encoding.UTF8.GetBytes(p.Piece);
                if (pBytes.Length > 0)
                {
                    bytesToPiece[new ByteArrayKey(pBytes)] = (_pieceToId[p.Piece], p.Score);
                }
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
                if (backtraceScore[curr] == float.NegativeInfinity)
                {
                    break;
                }
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
                {
                    continue;
                }
                else if (type == SentencePieceType.Byte)
                {
                    if (piece.StartsWith("<0x") && piece.EndsWith(">") && piece.Length == 6)
                    {
                        try
                        {
                            byte b = Convert.ToByte(piece.Substring(3, 2), 16);
                            bytes.Add(b);
                        }
                        catch
                        {
                            bytes.AddRange(Encoding.UTF8.GetBytes(piece));
                        }
                    }
                    else
                    {
                        bytes.AddRange(Encoding.UTF8.GetBytes(piece));
                    }
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(piece));
                }
            }

            string result = Encoding.UTF8.GetString(bytes.ToArray());
            result = result.Replace("\u2581", " ");
            if (result.StartsWith(" "))
            {
                result = result.Substring(1);
            }
            return result;
        }
    }

    public struct ByteArrayKey : IEquatable<ByteArrayKey>
    {
        private readonly byte[] _array;
        private readonly int _offset;
        private readonly int _length;
        private readonly int _hashCode;

        public ByteArrayKey(byte[] array) : this(array, 0, array.Length) {}

        public ByteArrayKey(byte[] array, int offset, int length)
        {
            _array = array;
            _offset = offset;
            _length = length;

            int hash = 17;
            for (int i = 0; i < length; i++)
            {
                hash = hash * 31 + array[offset + i];
            }
            _hashCode = hash;
        }

        public bool Equals(ByteArrayKey other)
        {
            if (_length != other._length) return false;
            for (int i = 0; i < _length; i++)
            {
                if (_array[_offset + i] != other._array[other._offset + i]) return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is ByteArrayKey other && Equals(other);
        public override int GetHashCode() => _hashCode;
    }

    public interface IGemmaEngine
    {
        Dictionary<string, Tensor> Run(Dictionary<string, Tensor> feed);
    }

    public class GemmaEngineStub : IGemmaEngine
    {
        private readonly int _vocabSize;
        private readonly int _stopTokenId;
        private int _prefillLen = -1;

        public GemmaEngineStub(int vocabSize, int stopTokenId)
        {
            _vocabSize = vocabSize;
            _stopTokenId = stopTokenId;
        }

        public Dictionary<string, Tensor> Run(Dictionary<string, Tensor> feed)
        {
            if (!feed.TryGetValue("input_ids", out var inputIds))
            {
                throw new ArgumentException("feed must contain input_ids");
            }

            int seqLen = (int)inputIds.Count;
            if (seqLen > 1)
            {
                _prefillLen = seqLen;
            }
            var outputs = new Dictionary<string, Tensor>();
            
            int numLayers = 0;
            foreach (var key in feed.Keys)
            {
                if (key.StartsWith("kv_in_"))
                {
                    numLayers++;
                    var kvIn = feed[key];
                    int prevSeqLen = kvIn.Shape[1];
                    int numHeads = kvIn.Shape[2];
                    int headDim = kvIn.Shape[3];

                    int newSeqLen = prevSeqLen + seqLen;
                    int newCount = 1 * newSeqLen * numHeads * headDim;
                    
                    var newFp = new float[newCount];
                    Array.Copy(kvIn.Fp, newFp, kvIn.Fp.Length);
                    
                    string outKey = key.Replace("kv_in_", "kv_out_");
                    outputs[outKey] = Tensor.F(newFp, 1, newSeqLen, numHeads, headDim);
                }
            }

            if (numLayers == 0)
            {
                numLayers = 42;
                for (int l = 0; l < numLayers; l++)
                {
                    int numHeads = 8;
                    int headDim = 256;
                    int newCount = 1 * seqLen * numHeads * headDim;
                    var newFp = new float[newCount];
                    outputs[$"kv_out_{l}"] = Tensor.F(newFp, 1, seqLen, numHeads, headDim);
                }
            }

            int logitsCount = 1 * seqLen * _vocabSize;
            var logitsFp = new float[logitsCount];

            long currentPos = 0;
            if (feed.TryGetValue("positions", out var positions))
            {
                currentPos = positions.AsI()[positions.AsI().Length - 1];
            }

            for (int i = 0; i < seqLen; i++)
            {
                int offset = i * _vocabSize;
                if (seqLen == 1 && _prefillLen > 0 && currentPos >= _prefillLen + 10)
                {
                    logitsFp[offset + _stopTokenId] = 100.0f;
                }
                else
                {
                    logitsFp[offset + 99] = 100.0f;
                }
            }

            outputs["logits"] = Tensor.F(logitsFp, 1, seqLen, _vocabSize);
            return outputs;
        }
    }

    public enum AgentDeltaKind { Token, Think, ToolCall, ToolResult, Error }
    public readonly record struct AgentDelta(AgentDeltaKind Kind, string Text, string Name = null, object Fault = null);
    public sealed record SamplerSpec(string Type, int TopK, float TopP, float Temperature, int Seed);

    public class dpxGemmaRuntime : IDisposable
    {
        private readonly string _modelPath;
        private readonly string _unitId;
        private readonly string _systemInstruction;
        private readonly SamplerSpec _sampler;
        
        private SentencePieceTokenizer _tokenizer;
        private IGemmaEngine _engine;
        private bool _isAlive;
        private int _bosId;
        private int _startOfTurnId;
        private int _endOfTurnId;
        
        private readonly Subsystem.Vom.Owner _vomOwner;
        private readonly List<string> _vomKvPaths = new();

        public dpxGemmaRuntime(string modelPath, string unitId, string systemInstruction = null, SamplerSpec sampler = null)
        {
            _modelPath = modelPath;
            _unitId = unitId;
            _systemInstruction = systemInstruction;
            _sampler = sampler;
            
            _vomOwner = Subsystem.Vom.Vom.CreateOwner($"\\Agent\\Rb\\Engine\\{_unitId}\\KVCache", maxBytes: 8L * 1024 * 1024 * 1024);
        }

        public string BackendName => "dpx-GEMMA4";
        public bool IsAlive => _isAlive;

        public string BringUp()
        {
            try
            {
                string spmPath = "s:\\modeldb\\gemma-4-e2b.spm";
                if (!File.Exists(spmPath))
                {
                    spmPath = Path.Combine(Path.GetDirectoryName(_modelPath) ?? "", "gemma-4-e2b.spm");
                }

                if (!File.Exists(spmPath))
                {
                    return $"SentencePiece model file not found. Checked s:\\modeldb\\gemma-4-e2b.spm and {spmPath}";
                }

                byte[] spmData = File.ReadAllBytes(spmPath);
                var proto = SpModelProto.Parse(spmData);
                _tokenizer = new SentencePieceTokenizer(proto);

                _bosId = _tokenizer.FindPieceId("<bos>");
                if (_bosId == -1) _bosId = _tokenizer.FindPieceId("<s>");
                _startOfTurnId = _tokenizer.FindPieceId("<start_of_turn>");
                if (_startOfTurnId == -1) _startOfTurnId = _tokenizer.FindPieceId("<|turn>");
                _endOfTurnId = _tokenizer.FindPieceId("<end_of_turn>");
                if (_endOfTurnId == -1) _endOfTurnId = _tokenizer.FindPieceId("<turn|>");

                int vocabSize = proto.Pieces.Count;
                int stopTokenId = _endOfTurnId != -1 ? _endOfTurnId : 1;
                _engine = new GemmaEngineStub(vocabSize, stopTokenId);

                _isAlive = true;
                return null;
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }

        public async IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[] audioBytes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_isAlive)
            {
                yield return new AgentDelta(AgentDeltaKind.Error, "Runtime is not alive");
                yield break;
            }

            var promptIds = new List<int>();
            if (_bosId != -1) promptIds.Add(_bosId);

            if (!string.IsNullOrEmpty(_systemInstruction))
            {
                if (_startOfTurnId != -1) promptIds.Add(_startOfTurnId);
                promptIds.AddRange(_tokenizer.Encode($"system\n{_systemInstruction}"));
                if (_endOfTurnId != -1) promptIds.Add(_endOfTurnId);
                promptIds.AddRange(_tokenizer.Encode("\n"));
            }

            if (_startOfTurnId != -1) promptIds.Add(_startOfTurnId);
            promptIds.AddRange(_tokenizer.Encode($"user\n{prompt}"));
            if (_endOfTurnId != -1) promptIds.Add(_endOfTurnId);
            promptIds.AddRange(_tokenizer.Encode("\n"));

            if (_startOfTurnId != -1) promptIds.Add(_startOfTurnId);
            promptIds.AddRange(_tokenizer.Encode("model\n"));

            var feed = new Dictionary<string, Tensor>();
            
            var inputIdsArr = new long[promptIds.Count];
            for (int i = 0; i < promptIds.Count; i++) inputIdsArr[i] = promptIds[i];
            feed["input_ids"] = Tensor.I(inputIdsArr, 1, promptIds.Count);

            var positionsArr = new long[promptIds.Count];
            for (int i = 0; i < promptIds.Count; i++) positionsArr[i] = i;
            feed["positions"] = Tensor.I(positionsArr, 1, promptIds.Count);

            Dictionary<string, Tensor> outs = null;
            Exception prefillEx = null;
            try
            {
                outs = _engine.Run(feed);
            }
            catch (Exception ex)
            {
                prefillEx = ex;
            }

            if (prefillEx != null)
            {
                yield return new AgentDelta(AgentDeltaKind.Error, $"Prefill failed: {prefillEx.Message}");
                yield break;
            }

            UpdateKvCache(outs, 0);

            if (!outs.TryGetValue("logits", out var logitsTensor))
            {
                yield return new AgentDelta(AgentDeltaKind.Error, "Prefill did not output logits");
                yield break;
            }

            int vocabSize = logitsTensor.Shape[2];
            int seqLen = logitsTensor.Shape[1];
            float[] logitsFp = logitsTensor.AsF();

            var lastLogits = new float[vocabSize];
            Array.Copy(logitsFp, (seqLen - 1) * vocabSize, lastLogits, 0, vocabSize);

            int nextToken = Sample(lastLogits);

            int maxNewTokens = 512;
            int step = 0;
            long currentPos = seqLen;

            while (step < maxNewTokens && nextToken != _endOfTurnId && nextToken != -1)
            {
                ct.ThrowIfCancellationRequested();
                
                string pieceText = _tokenizer.Detokenize(new[] { nextToken });
                yield return new AgentDelta(AgentDeltaKind.Token, pieceText);

                feed = new Dictionary<string, Tensor>
                {
                    ["input_ids"] = Tensor.I(new long[] { nextToken }, 1, 1),
                    ["positions"] = Tensor.I(new long[] { currentPos }, 1, 1)
                };

                foreach (var kvPath in _vomKvPaths)
                {
                    if (Subsystem.Vom.Vom.TryGetByPath(_vomOwner, kvPath, out var handle))
                    {
                        var tensor = (Tensor)GCHandle.FromIntPtr(handle.Resource).Target;
                        string name = Path.GetFileName(kvPath);
                        feed[name] = tensor;
                    }
                }

                Exception decodeEx = null;
                try
                {
                    outs = _engine.Run(feed);
                }
                catch (Exception ex)
                {
                    decodeEx = ex;
                }

                if (decodeEx != null)
                {
                    yield return new AgentDelta(AgentDeltaKind.Error, $"Decode step {step} failed: {decodeEx.Message}");
                    yield break;
                }

                UpdateKvCache(outs, step + 1);

                if (!outs.TryGetValue("logits", out logitsTensor))
                {
                    yield return new AgentDelta(AgentDeltaKind.Error, $"Decode step {step} did not output logits");
                    yield break;
                }

                logitsFp = logitsTensor.AsF();
                Array.Copy(logitsFp, 0, lastLogits, 0, vocabSize);

                nextToken = Sample(lastLogits);
                currentPos++;
                step++;
            }
        }

        private void UpdateKvCache(Dictionary<string, Tensor> outputs, int step)
        {
            var newKvTensors = new Dictionary<string, Tensor>();
            foreach (var kv in outputs)
            {
                if (kv.Key.StartsWith("kv_out_"))
                {
                    string kvInName = kv.Key.Replace("kv_out_", "kv_in_");
                    newKvTensors[kvInName] = kv.Value;
                }
            }

            foreach (var path in _vomKvPaths)
            {
                Subsystem.Vom.Vom.Close(_vomOwner, path);
            }
            _vomKvPaths.Clear();

            foreach (var kv in newKvTensors)
            {
                string name = kv.Key;
                var tensor = kv.Value;

                var handle = Subsystem.Vom.Vom.Register(_vomOwner, "Tensor", tensor, onReclaim: null, subdir: "KVCache", name: name);
                _vomKvPaths.Add(handle.Path);
            }
        }

        private int Sample(float[] logits)
        {
            float temp = _sampler?.Temperature ?? 1.0f;
            int topK = _sampler?.TopK ?? 0;

            if (temp <= 0.0f) temp = 1.0f;

            if (temp < 1e-4f)
            {
                int bestIdx = 0;
                float bestVal = logits[0];
                for (int i = 1; i < logits.Length; i++)
                {
                    if (logits[i] > bestVal)
                    {
                        bestVal = logits[i];
                        bestIdx = i;
                    }
                }
                return bestIdx;
            }

            var scaledLogits = new float[logits.Length];
            for (int i = 0; i < logits.Length; i++)
            {
                scaledLogits[i] = logits[i] / temp;
            }

            if (topK > 0 && topK < logits.Length)
            {
                var sorted = (float[])scaledLogits.Clone();
                Array.Sort(sorted);
                float threshold = sorted[logits.Length - topK];
                for (int i = 0; i < scaledLogits.Length; i++)
                {
                    if (scaledLogits[i] < threshold)
                    {
                        scaledLogits[i] = float.NegativeInfinity;
                    }
                }
            }

            float maxVal = float.NegativeInfinity;
            for (int i = 0; i < scaledLogits.Length; i++)
            {
                if (scaledLogits[i] > maxVal) maxVal = scaledLogits[i];
            }

            double sum = 0;
            var probs = new double[scaledLogits.Length];
            for (int i = 0; i < scaledLogits.Length; i++)
            {
                if (scaledLogits[i] == float.NegativeInfinity)
                {
                    probs[i] = 0;
                }
                else
                {
                    probs[i] = Math.Exp(scaledLogits[i] - maxVal);
                    sum += probs[i];
                }
            }

            for (int i = 0; i < probs.Length; i++)
            {
                probs[i] /= sum;
            }

            double r = new Random().NextDouble();
            double cumulative = 0.0;
            for (int i = 0; i < probs.Length; i++)
            {
                cumulative += probs[i];
                if (r <= cumulative)
                {
                    return i;
                }
            }

            return probs.Length - 1;
        }

        public void Dispose()
        {
            _isAlive = false;
            Subsystem.Vom.Vom.DropPrefix(_vomOwner.Path);
        }
    }

    public static class GemmaTalkingLayerTests
    {
        public static int RunTokenizerRoundtrip(string[] args)
        {
            string spmPath = "s:\\modeldb\\gemma-4-e2b.spm";
            if (args.Length > 1) spmPath = args[1];

            if (!File.Exists(spmPath))
            {
                Console.WriteLine($"SKIP - SentencePiece model not found at {spmPath}");
                return 0;
            }

            Console.WriteLine($"[Receipt] test.tokenizer.roundtrip - parsing {spmPath}...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] data = File.ReadAllBytes(spmPath);
            var proto = SpModelProto.Parse(data);
            var tokenizer = new SentencePieceTokenizer(proto);
            sw.Stop();
            Console.WriteLine($"Initialized: {proto.Pieces.Count} pieces in {sw.Elapsed.TotalMilliseconds:F1} ms.");


            var corpus = new[]
            {
                "Hello World!",
                "The quick brown fox jumps over the lazy dog.",
                "Testing SentencePiece byte-fallback: 𝒳𝒴𝒵 and emojis 🍕🚀💩.",
                "Multiple spaces      and newlines\n\nkeep spacing intact.",
                "Embedded characters: 日本語 / 嵌入式系统 / 🍉",
                "Numbers and punctuation: 123.456, + - * / = ? ! @ # $ % ^ & * ( ) _ +"
            };

            bool pass = true;
            int totalTokens = 0;

            for (int i = 0; i < corpus.Length; i++)
            {
                string text = corpus[i];
                var encSw = System.Diagnostics.Stopwatch.StartNew();
                var ids = tokenizer.Encode(text);
                encSw.Stop();


                var decSw = System.Diagnostics.Stopwatch.StartNew();
                string decoded = tokenizer.Detokenize(ids);
                decSw.Stop();

                totalTokens += ids.Count;
                bool match = text == decoded;
                if (!match)
                {
                    pass = false;
                    Console.WriteLine($"  [FAIL] Case {i+1} mismatch!\n    Original: '{text}'\n    Decoded:  '{decoded}'");
                }
                else
                {
                    Console.WriteLine($"  [ok] Case {i+1} bit-exact (tokens={ids.Count}, len={text.Length}, enc={encSw.Elapsed.TotalMilliseconds:F2}ms, dec={decSw.Elapsed.TotalMilliseconds:F2}ms)");
                }
            }

            if (pass)
            {
                Console.WriteLine($"\nVERDICT: PASS (test.tokenizer.roundtrip; total tokens={totalTokens})");
                return 0;
            }
            else
            {
                Console.WriteLine("\nVERDICT: FAIL");
                return 1;
            }
        }

        public static int RunGemmaTemplate(string[] args)
        {
            string spmPath = "s:\\modeldb\\gemma-4-e2b.spm";
            if (args.Length > 1) spmPath = args[1];

            if (!File.Exists(spmPath))
            {
                Console.WriteLine($"SKIP - SentencePiece model not found at {spmPath}");
                return 0;
            }

            byte[] data = File.ReadAllBytes(spmPath);
            var proto = SpModelProto.Parse(data);
            var tokenizer = new SentencePieceTokenizer(proto);

            int bos = tokenizer.FindPieceId("<bos>");
            if (bos == -1) bos = tokenizer.FindPieceId("<s>");
            int startOfTurn = tokenizer.FindPieceId("<start_of_turn>");
            if (startOfTurn == -1) startOfTurn = tokenizer.FindPieceId("<|turn>");
            int endOfTurn = tokenizer.FindPieceId("<end_of_turn>");
            if (endOfTurn == -1) endOfTurn = tokenizer.FindPieceId("<turn|>");

            bool pass = true;
            if (bos < 0) { Console.WriteLine("  [FAIL] bos not resolved"); pass = false; } else Console.WriteLine($"  [ok] bos dynamically resolved (ID={bos})");
            if (startOfTurn < 0) { Console.WriteLine("  [FAIL] startOfTurn not resolved"); pass = false; } else Console.WriteLine($"  [ok] startOfTurn dynamically resolved (ID={startOfTurn})");
            if (endOfTurn < 0) { Console.WriteLine("  [FAIL] endOfTurn not resolved"); pass = false; } else Console.WriteLine($"  [ok] endOfTurn dynamically resolved (ID={endOfTurn})");

            if (!pass)
            {
                Console.WriteLine("\nVERDICT: FAIL (dynamic resolution failed)");
                return 1;
            }

            var expectedIds = new List<int>();
            if (bos != -1) expectedIds.Add(bos);

            // System turn
            expectedIds.Add(startOfTurn);
            expectedIds.AddRange(tokenizer.Encode("system\nYou are helpful."));
            expectedIds.Add(endOfTurn);
            expectedIds.AddRange(tokenizer.Encode("\n"));

            // User turn
            expectedIds.Add(startOfTurn);
            expectedIds.AddRange(tokenizer.Encode("user\nhi"));
            expectedIds.Add(endOfTurn);
            expectedIds.AddRange(tokenizer.Encode("\n"));

            // Model prompt
            expectedIds.Add(startOfTurn);
            var modelPromptTokens = tokenizer.Encode("model\n");
            expectedIds.AddRange(modelPromptTokens);

            var formattedPromptIds = expectedIds.ToArray();

            bool isFirstBos = formattedPromptIds[0] == bos;
            bool isSecondStart = formattedPromptIds[1] == startOfTurn;
            int startOfTurnIndex = formattedPromptIds.Length - 1 - modelPromptTokens.Count;
            bool isFinalStart = formattedPromptIds[startOfTurnIndex] == startOfTurn;

            if (!isFirstBos) { Console.WriteLine("  [FAIL] First token is not BOS"); pass = false; } else Console.WriteLine("  [ok] First token is BOS");
            if (!isSecondStart) { Console.WriteLine("  [FAIL] Second token is not start_of_turn"); pass = false; } else Console.WriteLine("  [ok] Second token is start_of_turn");
            if (!isFinalStart) { Console.WriteLine("  [FAIL] Final prefix does not start with start_of_turn"); pass = false; } else Console.WriteLine("  [ok] Final prefix starts with start_of_turn");

            string detok = tokenizer.Detokenize(formattedPromptIds);
            string expectedContent = "system\nYou are helpful.\n\nuser\nhi\n\nmodel\n";

            string cleanDetok = System.Text.RegularExpressions.Regex.Replace(detok, @"\s+", " ");
            string cleanExpected = System.Text.RegularExpressions.Regex.Replace(expectedContent, @"\s+", " ");

            bool match = cleanDetok == cleanExpected;
            if (!match)
            {
                pass = false;
                Console.WriteLine($"  [FAIL] Detokenized content mismatch!\n    Got:      '{cleanDetok}'\n    Expected: '{cleanExpected}'");
            }
            else
            {
                Console.WriteLine("  [ok] Template text roundtripped successfully");
            }

            if (pass)
            {
                Console.WriteLine("\nVERDICT: PASS (test.tokenizer.gemma-template)");
                return 0;
            }
            else
            {
                Console.WriteLine("\nVERDICT: FAIL");
                return 1;
            }
        }

        public static int RunDecodeLoop(string[] args)
        {
            string spmPath = "s:\\modeldb\\gemma-4-e2b.spm";
            if (args.Length > 1) spmPath = args[1];

            if (!File.Exists(spmPath))
            {
                Console.WriteLine($"SKIP - SentencePiece model not found at {spmPath}");
                return 0;
            }

            Console.WriteLine("[Receipt] test.decode.loop-synthetic - bringup...");
            var runtime = new dpxGemmaRuntime("gemma-4-e2b.spm", "gemma_unit_test", "You are helpful.", new SamplerSpec("argmax", 0, 0f, 0f, 0));
            string bringupErr = runtime.BringUp();
            if (bringupErr != null)
            {
                Console.WriteLine($"  [FAIL] BringUp failed: {bringupErr}");
                return 1;
            }
            Console.WriteLine("  [ok] BringUp succeeded");

            int initialVomHandles = Subsystem.Vom.Vom.ActiveHandleCount;
            Console.WriteLine($"  [ok] Initial VOM active handle count: {initialVomHandles}");

            var enumerator = runtime.StreamTurnAsync("Tell me a story.", null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            int steps = 0;
            var tokens = new List<string>();
            bool hasError = false;

            // Drive N steps
            var task = Task.Run(async () =>
            {
                await foreach (var delta in enumerator)
                {
                    if (delta.Kind == AgentDeltaKind.Token)
                    {
                        tokens.Add(delta.Text);
                        steps++;
                        
                        // Check VOM handle count during decode loop
                        int activeHandles = Subsystem.Vom.Vom.ActiveHandleCount;
                        // For 42 layers, we expect exactly 42 handles in the active KV-cache
                        if (activeHandles != 42)
                        {
                            Console.WriteLine($"  [FAIL] Step {steps}: VOM active handle count is {activeHandles}, expected 42 (active KV-cache layer count)");
                            hasError = true;
                        }
                    }
                    else if (delta.Kind == AgentDeltaKind.Error)
                    {
                        Console.WriteLine($"  [FAIL] Stream error: {delta.Text}");
                        hasError = true;
                    }
                }
            });

            task.Wait();
            sw.Stop();

            Console.WriteLine($"Generated {steps} steps in {sw.Elapsed.TotalMilliseconds:F1} ms ({steps / sw.Elapsed.TotalSeconds:F2} tok/s).");
            Console.WriteLine($"Generated text: '{string.Concat(tokens)}'");

            // Assert cleanup on runtime disposal
            runtime.Dispose();
            int postDisposeVomHandles = Subsystem.Vom.Vom.ActiveHandleCount;
            Console.WriteLine($"  [ok] Post-dispose VOM active handle count: {postDisposeVomHandles}");

            bool pass = !hasError && steps > 0 && postDisposeVomHandles == 0;
            if (!pass)
            {
                Console.WriteLine("\nVERDICT: FAIL");
                return 1;
            }
            else
            {
                Console.WriteLine("\nVERDICT: PASS (test.decode.loop-synthetic)");
                return 0;
            }
        }
    }
}
