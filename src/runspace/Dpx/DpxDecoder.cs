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
        private readonly string? _embedModelPath;
        private readonly string? _spmPath;
        private readonly string _unitId;
        private readonly int _maxTokens;
        private readonly bool _split;

        private ModelProto? _model;
        private ModelProto? _embedModel;
        private SentencePieceTokenizer? _tokenizer;
        private Dp? _interp;
        private Dp? _embedInterp;

        private readonly object _gate = new();
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private volatile bool _ready;
        private RbFault? _initFault;
        private string _backendName = "DPX (uninitialized)";
        public bool? WorkerIsThreadPoolThread { get; private set; }
        public bool Verbose { get; set; }
        public int PromptTokensCount { get; private set; }

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

        // Constructor 2: File-based for production (single fused graph, raw .onnx on disk)
        public DpxDecoder(string modelPath, string spmPath, string unitId, int maxTokens = 4096)
        {
            _modelPath = modelPath;
            _spmPath = spmPath;
            _unitId = unitId;
            _maxTokens = maxTokens > 0 ? maxTokens : 4096;
        }

        // Constructor 3: split embed+decoder .db pair — the real gemma4-e2b q4 export shape (CRQ166).
        // Both graphs load via ModelDb.LoadGraphFromDb (the SQLite model store, not a raw .onnx file).
        public DpxDecoder(string embedDbPath, string decoderDbPath, string spmPath, string unitId, int maxTokens = 4096)
        {
            _embedModelPath = embedDbPath;
            _modelPath = decoderDbPath;
            _spmPath = spmPath;
            _unitId = unitId;
            _maxTokens = maxTokens > 0 ? maxTokens : 4096;
            _split = true;
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
                        // Split mode loads from the SQLite model store (the q4 export); fused mode loads a raw .onnx.
                        _model = _split ? ModelDb.LoadGraphFromDb(0, _modelPath) : ModelProto.Parser.ParseFrom(File.ReadAllBytes(_modelPath));
                    }

                    if (_split && _embedModel == null)
                    {
                        if (string.IsNullOrEmpty(_embedModelPath) || !File.Exists(_embedModelPath))
                        {
                            throw new FileNotFoundException($"Embed model file not found: {_embedModelPath}");
                        }
                        _embedModel = ModelDb.LoadGraphFromDb(0, _embedModelPath);
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

                    _interp = new Dp(_model) { Verbose = this.Verbose };
                    if (_split) _embedInterp = new Dp(_embedModel!) { Verbose = this.Verbose };
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

        public IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, byte[]? imageBytes, CancellationToken ct = default)
        {
            var fault = BringUp();
            if (fault != null)
            {
                return new SingleFaultEnumerable(fault);
            }

            var queue = new System.Collections.Concurrent.BlockingCollection<AgentDelta>();
            var owner = VomClass.CreateOwner($"\\Agent\\Dpx\\DpxDecoder\\{_unitId}");

            _turnGate.Wait(ct);
            try
            {
                var ctReg = ct.Register(() =>
                {
                    try { VomClass.Terminate(owner); } catch (Exception ex) { Dg.Log("rb", $"Terminate owner failed: {ex.Message}"); }
                });

                VomClass.Spawn(owner, "worker", (childOwner) =>
                {
                    try
                    {
                        DecodeLoop(prompt, delta => queue.Add(delta), childOwner, ct);
                    }
                    catch (OperationCanceledException ex)
                    {
                        Dg.Log("rb", $"Decode loop canceled: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Dg.Log("rb", $"Decode loop faulted: {ex}");
                        var rbFault = new RbFault(RbFaultClass.DecodeFaulted, _unitId, "DPX", ex.Message);
                        queue.Add(new AgentDelta(AgentDeltaKind.Error, ex.Message, Fault: rbFault));
                    }
                    finally
                    {
                        queue.CompleteAdding();
                        ctReg.Dispose();
                    }
                });

                return new BlockingTurnStream<AgentDelta>(queue, _turnGate, owner);
            }
            catch
            {
                _turnGate.Release();
                VomClass.Terminate(owner);
                throw;
            }
        }

        private void DecodeLoop(string prompt, Action<AgentDelta> writer, Owner owner, CancellationToken ct)
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
            PromptTokensCount = fullTokenIds.Count;

            int eosId = _tokenizer.FindPieceId("<eos>");
            if (eosId < 0) eosId = _tokenizer.FindPieceId("</s>");
            if (eosId < 0) eosId = 1; // Default Gemma EOS
            
            int endOfTurnId = _tokenizer.FindPieceId("<end_of_turn>");

            if (_split)
            {
                DecodeLoopSplit(fullTokenIds, eosId, endOfTurnId, writer, owner, ct);
                return;
            }

            var graphInputs = _model!.Graph.Input.Where(i => !_model.Graph.Initializer.Any(init => init.Name == i.Name)).ToList();
            string? mainInputName = graphInputs.FirstOrDefault(i => !i.Name.Contains("past"))?.Name;
            if (string.IsNullOrEmpty(mainInputName))
            {
                mainInputName = graphInputs.FirstOrDefault()?.Name ?? "input_ids";
            }

            string? mainOutputName = _model.Graph.Output.FirstOrDefault(o => !o.Name.Contains("present"))?.Name ?? "logits";

            var kvCacheHandles = new Dictionary<string, DpTensor>(StringComparer.OrdinalIgnoreCase);
            var pastKvInputs = graphInputs.Where(i => i.Name.Contains("past") || i.Name.Contains("key_values")).ToList();
            // present-output -> past-input name mapping: derive the real prefix ("past_key_values" for the
            // gemma q4 export, not "past") from the graph's own declared input names instead of assuming it.
            string pastPrefix = pastKvInputs.Count > 0 && pastKvInputs[0].Name.StartsWith("past_key_values") ? "past_key_values" : "past";

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
                    writer(new AgentDelta(AgentDeltaKind.Token, text));
                }

                if (pastKvInputs.Count > 0)
                {
                    var newCache = new Dictionary<string, DpTensor>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kvOutput in outputs.Keys.Where(name => name.Contains("present")))
                    {
                        string pastName = kvOutput.Replace("present", pastPrefix);
                        var tensor = outputs[kvOutput];

                        // Single copy straight into the persisted VOM region (source -> VOM), no
                        // managed-array intermediate (was source -> GC array -> VOM, two copies).
                        var kv = DpTensor.Alloc(owner, tensor.Shape, VomFormat.Float32, subdir: "Objects", name: pastName);
                        tensor.AsF().CopyTo(kv.ReadF32());

                        newCache[pastName] = kv;
                        if (Verbose)
                        {
                            Console.Error.WriteLine($"[DEBUG CACHE] Allocated new cache: key={pastName}, shape=[{string.Join(",", tensor.Shape)}], pointer=0x{kv.Data.Resource:X}");
                        }
                    }

                    // Close old KV-cache regions
                    foreach (var kv in kvCacheHandles.Values)
                    {
                        if (Verbose)
                        {
                            Console.Error.WriteLine($"[DEBUG CACHE] Closing old cache: path={kv.Data.Path}, pointer=0x{kv.Data.Resource:X}");
                        }
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

        // Split-graph decode: embed(input_ids) -> inputs_embeds + per_layer_inputs, THEN
        // decoder(inputs_embeds, per_layer_inputs, position_ids, attention_mask, num_logits_to_keep, past_kv)
        // -> logits + present_kv. This is the real gemma4-e2b q4 export shape (two graphs, DYNAMIC KV — no
        // baked slot cap, unlike the fused litert path above). Same VOM-native KV carry-forward as DecodeLoop.
        private void DecodeLoopSplit(List<int> fullTokenIds, int eosId, int endOfTurnId, Action<AgentDelta> writer, Owner owner, CancellationToken ct)
        {
            var decInputs = _model!.Graph.Input.Where(i => !_model.Graph.Initializer.Any(init => init.Name == i.Name)).ToList();
            var pastKvInputs = decInputs.Where(i => i.Name.Contains("past") || i.Name.Contains("key_values")).ToList();
            string pastPrefix = pastKvInputs.Count > 0 && pastKvInputs[0].Name.StartsWith("past_key_values") ? "past_key_values" : "past";

            var kvCacheHandles = new Dictionary<string, DpTensor>(StringComparer.OrdinalIgnoreCase);
            var seq = new List<int>(fullTokenIds);
            int pastLen = 0;
            int step = 0;

            while (step < _maxTokens)
            {
                ct.ThrowIfCancellationRequested();
                if (owner.Token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(owner.Token);
                }

                int[] cur = step == 0 ? seq.ToArray() : new[] { seq[^1] };   // prefill all tokens, then one per step
                int S = cur.Length, totalSeq = pastLen + S;

                var e = _embedInterp!.Run(new() { ["input_ids"] = Tensor.I(Array.ConvertAll(cur, t => (long)t), 1, S) });

                var posArr = new long[S]; for (int i = 0; i < S; i++) posArr[i] = pastLen + i;
                var amask = new long[totalSeq]; for (int i = 0; i < totalSeq; i++) amask[i] = 1;

                var feed = new Dictionary<string, Tensor>
                {
                    ["inputs_embeds"] = e["inputs_embeds"],
                    ["per_layer_inputs"] = e["per_layer_inputs"],
                    ["position_ids"] = Tensor.I(posArr, 1, S),
                    ["attention_mask"] = Tensor.I(amask, 1, totalSeq),
                    ["num_logits_to_keep"] = Tensor.I(new long[] { 1 }),
                };

                foreach (var kvInput in pastKvInputs)
                {
                    if (kvCacheHandles.TryGetValue(kvInput.Name, out var kv))
                    {
                        // Zero-copy: alias the persisted VOM region directly as the feed tensor's native
                        // backing (Dp.Run only reads it) - no managed array, no Marshal.Copy.
                        int[] shape = GetKvInputShape(kvInput, pastLen);
                        unsafe { feed[kvInput.Name] = Tensor.F((float*)kv.Data.Resource, shape); }
                    }
                    else
                    {
                        int[] shape = GetKvInputShape(kvInput, 0);
                        feed[kvInput.Name] = Tensor.F(Array.Empty<float>(), shape);
                    }
                }

                var outputs = _interp!.Run(feed, onNode: (node, outs, env) =>
                {
                    foreach (var o in outs)
                        if (o != null && o.Shape.Contains(0))
                        { Dg.Log("rb", $"ZERO-SHAPE '{node.Name}' (op={node.OpType}) -> [{string.Join(",", o.Shape)}]"); break; }
                });
                pastLen = totalSeq;

                if (!outputs.TryGetValue("logits", out var logitsTensor))
                {
                    throw new KeyNotFoundException("Graph output not found: logits");
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

                seq.Add(nextTokenId);

                if (nextTokenId == eosId || (endOfTurnId >= 0 && nextTokenId == endOfTurnId))
                {
                    break;
                }

                string text = _tokenizer!.Detokenize(new[] { nextTokenId });
                if (!string.IsNullOrEmpty(text))
                {
                    writer(new AgentDelta(AgentDeltaKind.Token, text));
                }

                var newCache = new Dictionary<string, DpTensor>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvOutput in outputs.Keys.Where(name => name.Contains("present")))
                {
                    string pastName = kvOutput.Replace("present", pastPrefix);
                    var tensor = outputs[kvOutput];

                    var kv = DpTensor.Alloc(owner, tensor.Shape, VomFormat.Float32, subdir: "Objects", name: pastName);
                    tensor.AsF().CopyTo(kv.ReadF32());

                    newCache[pastName] = kv;
                    if (Verbose)
                    {
                        Console.Error.WriteLine($"[DEBUG CACHE] Allocated new cache: key={pastName}, shape=[{string.Join(",", tensor.Shape)}], pointer=0x{kv.Data.Resource:X}");
                    }
                }

                foreach (var kv in kvCacheHandles.Values)
                {
                    if (Verbose)
                    {
                        Console.Error.WriteLine($"[DEBUG CACHE] Closing old cache: path={kv.Data.Path}, pointer=0x{kv.Data.Resource:X}");
                    }
                    kv.Close();
                }

                kvCacheHandles = newCache;
                step++;
            }

            foreach (var kv in kvCacheHandles.Values)
            {
                kv.Close();
            }
        }

        // KV layout is [batch, heads, seq, head_dim] — only index 2 (seq) is the axis THIS decode loop drives
        // (past/present length). Any other symbolic/unset dim (e.g. a dynamically-declared batch axis) must
        // default to 1, not seqLen — conflating them zeroed the batch dim at step 0 (seqLen=0), corrupting
        // GroupQueryAttention's output to [0,S,...] and crashing downstream on the residual Add.
        private int[] GetKvInputShape(ValueInfoProto kvInput, int seqLen)
        {
            var dims = kvInput.Type?.TensorType?.Shape?.Dim;
            if (dims == null) return new[] { 1, 1, seqLen, 1 }; // default fallback layout

            int[] shape = new int[dims.Count];
            for (int i = 0; i < dims.Count; i++)
            {
                long val = dims[i].DimValue;
                if (i == 2)
                {
                    shape[i] = seqLen;
                }
                else if (val > 0)
                {
                    shape[i] = (int)val;
                }
                else
                {
                    shape[i] = 1;   // symbolic/unset non-seq axis (batch) - this decode loop always runs batch=1
                }
            }
            return shape;
        }

        public void Dispose()
        {
            // Weight storage is VOM-native (CRQ164): the Dp instance lazily owns a Weights owner (its
            // packed q4 tensors), scoped to its OWN lifetime, not any single turn's owner - a turn's
            // owner gets Terminated on cancellation, but weights persist across turns (Dp.Run's _winit
            // cache is decoded once and reused), so this cannot be wired to the per-turn Terminate above.
            if (_interp?.WeightsOwner is Owner weightsOwner)
            {
                try { VomClass.Terminate(weightsOwner); } catch (Exception ex) { Dg.Log("rb", $"Terminate weights owner failed: {ex.Message}"); }
            }
            if (_embedInterp?.WeightsOwner is Owner embedWeightsOwner)
            {
                try { VomClass.Terminate(embedWeightsOwner); } catch (Exception ex) { Dg.Log("rb", $"Terminate embed weights owner failed: {ex.Message}"); }
            }
        }
    }

    public class SingleFaultEnumerable : IAsyncEnumerable<AgentDelta>, IAsyncEnumerator<AgentDelta>
    {
        private readonly RbFault _fault;
        private bool _done;

        public SingleFaultEnumerable(RbFault fault) => _fault = fault;

        public IAsyncEnumerator<AgentDelta> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (!_done)
            {
                _done = true;
                return ValueTask.FromResult(true);
            }
            return ValueTask.FromResult(false);
        }

        public AgentDelta Current => new AgentDelta(AgentDeltaKind.Error, _fault.NativeDetail, Fault: _fault);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Strictly synchronous blocking wrapper that implements IAsyncEnumerable/IAsyncEnumerator to satisfy
    // the external IRuntime contract. It uses an in-memory BlockingCollection and zero-cost ValueTask
    // completions, avoiding ThreadPool hops and keeping execution on the calling thread.
    public class BlockingTurnStream<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        private readonly System.Collections.Concurrent.BlockingCollection<T> _queue;
        private readonly SemaphoreSlim _turnGate;
        private readonly Owner _owner;
        private T _current;

        public BlockingTurnStream(System.Collections.Concurrent.BlockingCollection<T> queue, SemaphoreSlim turnGate, Owner owner)
        {
            _queue = queue;
            _turnGate = turnGate;
            _owner = owner;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            try
            {
                if (_queue.TryTake(out _current, Timeout.Infinite))
                {
                    return ValueTask.FromResult(true);
                }
            }
            catch (ObjectDisposedException) { }
            return ValueTask.FromResult(false);
        }

        public T Current => _current;

        public ValueTask DisposeAsync()
        {
            try { _queue.Dispose(); } catch { }
            _turnGate.Release();
            try { VomClass.Terminate(_owner); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
