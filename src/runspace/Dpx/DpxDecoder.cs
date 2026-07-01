using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Onnx;
using Subsystem.RuntimeBroker;
using Subsystem.Vom;
using VomClass = Subsystem.Vom.Vom;

namespace Subsystem.Dpx
{
    // DPX decode face: the host-side DECODE LOOP that turns a prompt into streamed
    // tokens by driving the in-proc dpx interpreter. Tensors and KV are VOM regions, off-GC.
    public sealed class DpxDecoder : Runtime
    {
        private readonly string? _modelPath;
        private readonly string? _spmPath;
        private readonly string _unitId;
        private readonly int _maxTokens;

        private ModelProto? _model;
        private SentencePieceTokenizer? _tokenizer;
        private Dp? _interp;

        private readonly object _gate = new();
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private volatile bool _ready;
        private RbFault? _initFault;
        private string _backendName = "DPX (uninitialized)";
        public bool? WorkerIsThreadPoolThread { get; private set; }

        // Constructor 1: Injected for testing
        public DpxDecoder(ModelProto model, SentencePieceTokenizer tokenizer, string unitId, int maxTokens = 4096)
        {
            _model = model;
            _tokenizer = tokenizer;
            _unitId = unitId;
            _maxTokens = maxTokens > 0 ? maxTokens : 4096;
            _backendName = "DP-ONNX";
            _interp = new Dp(_model);
            _ready = true;
        }

        // Constructor 2: File-based for production
        public DpxDecoder(string modelPath, string spmPath, string unitId, int maxTokens = 4096)
        {
            _modelPath = modelPath;
            _spmPath = spmPath;
            _unitId = unitId;
            _maxTokens = maxTokens > 0 ? maxTokens : 4096;
        }

        public bool IsAlive => _ready && _initFault == null;
        public string BackendName => _backendName;

        public RbFault? BringUp()
        {
            if (_ready) return null;
            if (_initFault != null) return _initFault;

            lock (_gate)
            {
                if (_ready) return null;
                if (_initFault != null) return _initFault;

                try
                {
                    if (_model == null)
                    {
                        if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
                        {
                            throw new FileNotFoundException($"Model file not found: {_modelPath}");
                        }
                        _model = ModelProto.Parser.ParseFrom(File.ReadAllBytes(_modelPath));
                    }

                    if (_tokenizer == null)
                    {
                        if (string.IsNullOrEmpty(_spmPath) || !File.Exists(_spmPath))
                        {
                            throw new FileNotFoundException($"Tokenizer file not found: {_spmPath}");
                        }
                        var spm = SpModelProto.Parse(File.ReadAllBytes(_spmPath));
                        _tokenizer = new SentencePieceTokenizer(spm);
                    }

                    _interp = new Dp(_model);
                    _backendName = "DPX";
                    _ready = true;
                    return null;
                }
                catch (Exception ex)
                {
                    _initFault = new RbFault(RbFaultClass.BringUpFailed, _unitId, "CPU/DPX", ex.Message);
                    return _initFault;
                }
            }
        }

        public IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, CancellationToken ct = default)
            => StreamTurnAsync(prompt, audioBytes, null, ct);

        public async IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, byte[]? imageBytes, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var fault = BringUp();
            if (fault != null)
            {
                yield return new AgentDelta(AgentDeltaKind.Error, fault.NativeDetail, Fault: fault);
                yield break;
            }

            var channel = Channel.CreateUnbounded<AgentDelta>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var owner = VomClass.CreateOwner($"\\Agent\\Dpx\\DpxDecoder\\{_unitId}");

            await _turnGate.WaitAsync(ct);
            try
            {
                using var ctReg = ct.Register(() =>
                {
                    try { VomClass.Terminate(owner); } catch (Exception ex) { Dg.Log("rb", $"Terminate owner failed: {ex.Message}"); }
                });

                VomClass.Spawn(owner, "worker", (childOwner) =>
                {
                    try
                    {
                        DecodeLoop(prompt, channel.Writer, childOwner, ct);
                    }
                    catch (OperationCanceledException ex)
                    {
                        Dg.Log("rb", $"Decode loop canceled: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        var rbFault = new RbFault(RbFaultClass.DecodeFaulted, _unitId, "DPX", ex.Message);
                        channel.Writer.TryWrite(new AgentDelta(AgentDeltaKind.Error, ex.Message, Fault: rbFault));
                    }
                    finally
                    {
                        channel.Writer.TryComplete();
                    }
                });

                await foreach (var delta in channel.Reader.ReadAllAsync(ct))
                {
                    yield return delta;
                }
            }
            finally
            {
                _turnGate.Release();
                VomClass.Terminate(owner);
            }
        }

        private void DecodeLoop(string prompt, ChannelWriter<AgentDelta> writer, Owner owner, CancellationToken ct)
        {
            WorkerIsThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            // Gemma chat template: <start_of_turn>user\n{prompt}<end_of_turn>\n<start_of_turn>model\n
            string formattedPrompt = $"<start_of_turn>user\n{prompt}<end_of_turn>\n<start_of_turn>model\n";
            var tokenIds = _tokenizer!.Encode(formattedPrompt);

            int bosId = _tokenizer.FindPieceId("<bos>");
            if (bosId < 0) bosId = _tokenizer.FindPieceId("<s>");
            if (bosId < 0) bosId = 2; // Default Gemma BOS

            var fullTokenIds = new List<int>();
            if (bosId >= 0) fullTokenIds.Add(bosId);
            fullTokenIds.AddRange(tokenIds);

            int eosId = _tokenizer.FindPieceId("<eos>");
            if (eosId < 0) eosId = _tokenizer.FindPieceId("</s>");
            if (eosId < 0) eosId = 1; // Default Gemma EOS
            
            int endOfTurnId = _tokenizer.FindPieceId("<end_of_turn>");

            var graphInputs = _model!.Graph.Input.Where(i => !_model.Graph.Initializer.Any(init => init.Name == i.Name)).ToList();
            string? mainInputName = graphInputs.FirstOrDefault(i => !i.Name.Contains("past"))?.Name;
            if (string.IsNullOrEmpty(mainInputName))
            {
                mainInputName = graphInputs.FirstOrDefault()?.Name ?? "input_ids";
            }

            string? mainOutputName = _model.Graph.Output.FirstOrDefault(o => !o.Name.Contains("present"))?.Name ?? "logits";

            var kvCacheHandles = new Dictionary<string, DpTensor>(StringComparer.OrdinalIgnoreCase);
            var pastKvInputs = graphInputs.Where(i => i.Name.Contains("past") || i.Name.Contains("key_values")).ToList();

            int step = 0;
            var currentTokens = new List<int>(fullTokenIds);

            while (step < _maxTokens)
            {
                ct.ThrowIfCancellationRequested();
                if (owner.Token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(owner.Token);
                }

                var feed = new Dictionary<string, Tensor>();

                if (pastKvInputs.Count > 0 && step > 0)
                {
                    // Feeding only the last token
                    var inputTensor = Tensor.I(new[] { (long)currentTokens.Last() }, 1, 1);
                    feed[mainInputName] = inputTensor;

                    foreach (var kvInput in pastKvInputs)
                    {
                        if (kvCacheHandles.TryGetValue(kvInput.Name, out var kv))
                        {
                            // Zero-copy: alias the persisted VOM region directly as the feed tensor's
                            // native backing (Dp.Run only reads it) - no managed array, no Marshal.Copy.
                            int[] shape = GetKvInputShape(kvInput, step);
                            unsafe { feed[kvInput.Name] = Tensor.F((float*)kv.Data.Resource, shape); }
                        }
                        else
                        {
                            int[] shape = GetKvInputShape(kvInput, 0);
                            feed[kvInput.Name] = Tensor.F(Array.Empty<float>(), shape);
                        }
                    }
                }
                else
                {
                    // Prefill step: feed all accumulated tokens
                    var inputTensor = Tensor.I(currentTokens.Select(t => (long)t).ToArray(), 1, currentTokens.Count);
                    feed[mainInputName] = inputTensor;

                    foreach (var kvInput in pastKvInputs)
                    {
                        int[] shape = GetKvInputShape(kvInput, 0);
                        feed[kvInput.Name] = Tensor.F(Array.Empty<float>(), shape);
                    }
                }

                var outputs = _interp!.Run(feed);
                if (!outputs.TryGetValue(mainOutputName, out var logitsTensor))
                {
                    throw new KeyNotFoundException($"Graph output not found: {mainOutputName}");
                }

                Span<float> logits = logitsTensor.AsF();
                int vocabSize = logitsTensor.Shape.Last();
                int lastTokenIndex = (logitsTensor.Shape.Length > 1) ? (int)(logitsTensor.Count / vocabSize - 1) : 0;

                int nextTokenId = 0;
                float maxVal = float.NegativeInfinity;
                for (int v = 0; v < vocabSize; v++)
                {
                    float val = logits[lastTokenIndex * vocabSize + v];
                    if (val > maxVal)
                    {
                        maxVal = val;
                        nextTokenId = v;
                    }
                }

                currentTokens.Add(nextTokenId);

                if (nextTokenId == eosId || (endOfTurnId >= 0 && nextTokenId == endOfTurnId))
                {
                    break;
                }

                string text = _tokenizer.Detokenize(new[] { nextTokenId });
                if (!string.IsNullOrEmpty(text))
                {
                    writer.TryWrite(new AgentDelta(AgentDeltaKind.Token, text));
                }

                if (pastKvInputs.Count > 0)
                {
                    var newCache = new Dictionary<string, DpTensor>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kvOutput in outputs.Keys.Where(name => name.Contains("present")))
                    {
                        string pastName = kvOutput.Replace("present", "past");
                        var tensor = outputs[kvOutput];

                        // Single copy straight into the persisted VOM region (source -> VOM), no
                        // managed-array intermediate (was source -> GC array -> VOM, two copies).
                        var kv = DpTensor.Alloc(owner, tensor.Shape, VomFormat.Float32, subdir: "Objects", name: pastName);
                        tensor.AsF().CopyTo(kv.ReadF32());

                        newCache[pastName] = kv;
                    }

                    // Close old KV-cache regions
                    foreach (var kv in kvCacheHandles.Values)
                    {
                        kv.Close();
                    }

                    kvCacheHandles = newCache;
                }

                step++;
            }

            // Clean up KV cache handles
            foreach (var kv in kvCacheHandles.Values)
            {
                kv.Close();
            }
        }

        private int[] GetKvInputShape(ValueInfoProto kvInput, int seqLen)
        {
            var dims = kvInput.Type?.TensorType?.Shape?.Dim;
            if (dims == null) return new[] { 1, 1, seqLen, 1 }; // default fallback layout

            int[] shape = new int[dims.Count];
            for (int i = 0; i < dims.Count; i++)
            {
                long val = dims[i].DimValue;
                if (i == 2 || val <= 0 || !string.IsNullOrEmpty(dims[i].DimParam))
                {
                    shape[i] = seqLen;
                }
                else
                {
                    shape[i] = (int)val;
                }
            }
            return shape;
        }

        public void Dispose()
        {
            // Transient runtime references are GC managed.
        }
    }
}
