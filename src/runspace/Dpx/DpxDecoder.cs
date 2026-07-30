#nullable enable
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
using Subsystem;
using Subsystem.Vom;
using VomClass = Subsystem.Vom.Vom;

namespace Subsystem.Dpx
{
    // DPX is the PROTOCOL (DirectPortX) — logistics, not an agent. This is the citizen decode loop SS drives
    // over it: an ITurnSource, never a Runtime (a Runtime is a mounted GUEST engine; DPX is neither).
    public sealed class DpxDecoder : ITurnSource
    {
        private readonly string _modelPath;
        private readonly string _embedModelPath;
        private readonly string _spmPath;
        private readonly string _unitId;
        private int _maxTokens;
        private readonly bool _split;
        private readonly bool _consolidated;

        private ModelProto _model;
        private ModelProto _embedModel;
        private SentencePieceTokenizer _tokenizer;
        private Dpx _interp;
        private Dpx _embedInterp;

        private readonly object _gate = new();
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private volatile bool _ready;
        private RbFault _initFault;
        private string _backendName = "DPX (uninitialized)";
        public bool? WorkerIsThreadPoolThread { get; private set; }
        public bool Verbose { get; set; }
        public int PromptTokensCount { get; private set; }
        // Ring receipt (CRQ190): present-KV outputs that came back ring-marked (appended in place inside
        // the persistent region) instead of needing the legacy per-token alloc/copy/close carry-forward.
        public long KvRingSteps { get; private set; }

        // Last turn's measured counters (the settings benchmark / done-frame receipt). Written by the
        // decode worker at turn end; read after the stream drains — turns are serialized by _turnGate.
        private double _lastPrefillMs, _lastDecodeMs;
        private int _lastDecodeTokens;

        public Benchmark? GetBenchmark()
        {
            if (_lastPrefillMs <= 0 && _lastDecodeTokens == 0) return null;
            double prefillTps = _lastPrefillMs > 0 ? PromptTokensCount / (_lastPrefillMs / 1000.0) : 0;
            double decodeTps = _lastDecodeMs > 0 ? _lastDecodeTokens / (_lastDecodeMs / 1000.0) : 0;
            return new Benchmark(0, _lastPrefillMs / 1000.0, PromptTokensCount, prefillTps, _lastDecodeTokens, decodeTps);
        }

        // Per-turn decode cap. The constructor value seeds it; a resident host (e.g. ss mcp `query`)
        // sets it between turns. Turns are serialized by _turnGate and the hosts are single-threaded,
        // so a set never races an in-flight decode loop.
        public int MaxTokens { get => _maxTokens; set { if (value > 0) _maxTokens = value; } }

        // Constructor 1: Injected for testing
        public DpxDecoder(ModelProto model, SentencePieceTokenizer tokenizer, string unitId, int maxTokens = 4096)
        {
            _model = model;
            _tokenizer = tokenizer;
            _unitId = unitId;
            _maxTokens = maxTokens > 0 ? maxTokens : 4096;
            _backendName = "DPX";
            _interp = new Dpx(_model);
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

        // Constructor 4: ONE consolidated db (CRQ191) — embed + decoder faces resolved BY NAME from one file,
        // the tokenizer read from its blob table. No loose sidecar, no per-face pair; still the split decode
        // path (two graphs, dynamic KV), just sourced from a single self-describing db.
        public DpxDecoder(string consolidatedDbPath, string unitId, int maxTokens = 4096)
        {
            _modelPath = consolidatedDbPath;
            _embedModelPath = consolidatedDbPath;
            _unitId = unitId;
            _maxTokens = maxTokens > 0 ? maxTokens : 4096;
            _split = true;
            _consolidated = true;
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
                    if (_consolidated)
                    {
                        if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
                        {
                            throw new FileNotFoundException($"Consolidated db not found: {_modelPath}");
                        }
                        // One db, faces by name: decoder + embed graphs, tokenizer from the blob table.
                        if (_model == null) _model = ModelDb.LoadGraphFromDb("decoder", _modelPath);
                        if (_embedModel == null) _embedModel = ModelDb.LoadGraphFromDb("embed", _modelPath);
                        if (_tokenizer == null)
                        {
                            byte[] spm = ModelDb.ExtractTokenizerBlob(_modelPath, null)
                                ?? throw new FileNotFoundException($"Consolidated db carries no tokenizer blob: {_modelPath}");
                            _tokenizer = new SentencePieceTokenizer(SpModelProto.Parse(spm));
                        }
                    }
                    else
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
                    }

                    _interp = new Dpx(_model) { Verbose = this.Verbose };
                    if (_split) _embedInterp = new Dpx(_embedModel!) { Verbose = this.Verbose };
                    Dpx.ActiveModelDbPath = _modelPath;
                    _backendName = "DPX";
                    _ready = true;

                    if (OperatingSystem.IsWindows() && Dpx.UseGpuMatMulNBits)
                    {
                        ulong workloadBytes = CalculateWorkloadBytes();
                        var (bestIdx, bestName, needsMulti) = GpuD3D12.EvaluateWorkload(workloadBytes);
                        double wMb = workloadBytes / (1024.0 * 1024.0);
                        if (!needsMulti)
                        {
                            Console.WriteLine($"[DPX Workload Sizer] Dynamic Footprint: {wMb:F1} MB | Target GPU [{bestIdx}]: {bestName} | Strategy: 100% Single-GPU VRAM Resident (0 PCIe Ping-Ponging)");
                            InitializeLayerNodes();
                        }
                        else
                        {
                            Console.WriteLine($"[DPX Workload Sizer] Dynamic Footprint: {wMb:F1} MB exceeds GPU [{bestIdx}] VRAM | Strategy: Multi-GPU Multi-Die Pipeline Split");
                            InitializeLayerNodes();
                        }
                    }

                    return null;
                }
                catch (Exception ex)
                {
                    _initFault = new RbFault(RbFaultClass.BringUpFailed, _unitId, "CPU/DPX", ex.Message);
                    return _initFault;
                }
            }
        }

        public ulong CalculateWorkloadBytes()
        {
            ulong weightsBytes = 0;
            if (_model?.Graph?.Initializer != null)
            {
                foreach (var init in _model.Graph.Initializer)
                    weightsBytes += (ulong)(init.RawData.Span.Length);
            }
            if (_embedModel?.Graph?.Initializer != null)
            {
                foreach (var init in _embedModel.Graph.Initializer)
                    weightsBytes += (ulong)(init.RawData.Span.Length);
            }
            ulong scratchBytes = 16UL * 1024UL * 1024UL; // 16 MB activation scratch
            ulong kvBytes = (ulong)(_maxTokens * 2 * 16 * 2048 * 2); // KV cache estimate
            return weightsBytes + scratchBytes + kvBytes;
        }

        private DpxLayerNode[]? _layerNodes;

        private void InitializeLayerNodes()
        {
            _layerNodes = new DpxLayerNode[28];
            for (int i = 0; i < 28; i++)
            {
                _layerNodes[i] = new DpxLayerNode(i, 2048 * 4); // 2048 hidden dim float32
                if (OperatingSystem.IsWindows())
                {
                    _layerNodes[i].ScratchBuffer = GpuD3D12.CreateDefaultVramBuffer(_layerNodes[i].ScratchBytes);
                    _layerNodes[i].BlitBuffer = GpuD3D12.CreateDefaultVramBuffer(_layerNodes[i].BlitBytes);
                    _layerNodes[i].ScratchVA = GpuD3D12.GetGpuVA(_layerNodes[i].ScratchBuffer);
                    _layerNodes[i].BlitVA = GpuD3D12.GetGpuVA(_layerNodes[i].BlitBuffer);
                }
                if (i > 0)
                {
                    _layerNodes[i].BindUpstreamContract(_layerNodes[i - 1]);
                }
            }
            Console.WriteLine($"[DPX Contracts] Initialized 28 DpxLayerNode capability contracts (256-byte aligned VRAM pitch: {_layerNodes[0].AlignedScratchPitch} bytes)");
        }

        public IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, CancellationToken ct = default)
            => StreamTurnAsync(prompt, audioBytes, null, ct);

        // Strips literal turn markers from the VISIBLE token stream. The model sometimes emits
        // "<end_of_turn>"/"<start_of_turn>" as ordinary word pieces (text) before the real control id
        // lands — the stop check catches the id, but the literal pieces would reach the UI. Tokens
        // accumulate here; complete markers are dropped, a trailing partial marker is held back until
        // it resolves, everything else flows through in order. Non-token deltas flush and pass through.
        private sealed class TurnMarkerFilter
        {
            // Instance state (SS015: no static mutable). Gemma's turn delimiters — the model sometimes spells
            // them as ordinary word-pieces (TEXT), so the tokenizer's special-id skip never sees them.
            private readonly string[] _markers = { "<end_of_turn>", "<start_of_turn>" };
            private readonly Action<AgentDelta> _out;
            private string _pending = "";

            public TurnMarkerFilter(Action<AgentDelta> sink) { _out = sink; }

            public void Push(AgentDelta d)
            {
                if (d.Kind != AgentDeltaKind.Token || string.IsNullOrEmpty(d.Text)) { Flush(); _out(d); return; }
                _pending += d.Text;
                foreach (var mk in _markers) _pending = _pending.Replace(mk, "");
                int hold = TrailingPartial(_pending);
                if (_pending.Length > hold)
                {
                    _out(new AgentDelta(AgentDeltaKind.Token, _pending.Substring(0, _pending.Length - hold)));
                    _pending = _pending.Substring(_pending.Length - hold);
                }
            }

            // Length of the longest pending suffix that could still become a marker. A pure marker
            // prefix at turn end is dropped by Flush — it was never going to be prose.
            private int TrailingPartial(string s)
            {
                foreach (var mk in _markers)
                    for (int len = Math.Min(mk.Length - 1, s.Length); len > 0; len--)
                        if (s.EndsWith(mk.Substring(0, len), StringComparison.Ordinal)) return len;
                return 0;
            }

            public void Flush()
            {
                if (_pending.Length == 0) return;
                var text = _pending; _pending = "";
                if (TrailingPartial(text) == text.Length) return;   // pure partial marker — drop
                _out(new AgentDelta(AgentDeltaKind.Token, text));
            }
        }

        public IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[] audioBytes, byte[]? imageBytes, CancellationToken ct = default)
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
                    var marker = new TurnMarkerFilter(delta => queue.Add(delta));
                    try
                    {
                        DecodeLoop(prompt, marker.Push, childOwner, ct);
                        marker.Flush();
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

            var graphInputs = _model.Graph.Input.Where(i => !_model.Graph.Initializer.Any(init => init.Name == i.Name)).ToList();
            string mainInputName = graphInputs.FirstOrDefault(i => !i.Name.Contains("past"))?.Name;
            if (string.IsNullOrEmpty(mainInputName))
            {
                mainInputName = graphInputs.FirstOrDefault()?.Name ?? "input_ids";
            }

            string mainOutputName = _model.Graph.Output.FirstOrDefault(o => !o.Name.Contains("present"))?.Name ?? "logits";

            var kvCacheHandles = new Dictionary<string, DpxTensor>(StringComparer.OrdinalIgnoreCase);
            var pastKvInputs = graphInputs.Where(i => i.Name.Contains("past") || i.Name.Contains("key_values")).ToList();
            // present-output -> past-input name mapping: derive the real prefix ("past_key_values" for the
            // gemma q4 export, not "past") from the graph's own declared input names instead of assuming it.
            string pastPrefix = pastKvInputs.Count > 0 && pastKvInputs[0].Name.StartsWith("past_key_values") ? "past_key_values" : "past";

            int step = 0;
            var currentTokens = new List<int>(fullTokenIds);

            // KV ring (CRQ190): same persistent-region scheme as DecodeLoopSplit - one VOM region per
            // past-KV input for the whole turn, in-place append by the ring-aware kernels, demote-once
            // to legacy copy-forward when a cache's present comes back unmarked.
            int kvRingCap = fullTokenIds.Count + _maxTokens;
            var ringLive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvInput in pastKvInputs)
            {
                kvCacheHandles[kvInput.Name] = DpxTensor.Alloc(owner, GetKvInputShape(kvInput, kvRingCap), VomFormat.Float32, subdir: "Objects", name: kvInput.Name);
                ringLive.Add(kvInput.Name);
            }

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
                            // native backing - no managed array, no Marshal.Copy. A ring-live cache also
                            // carries KvCap so the kernel appends this step's K/V in place.
                            int[] shape = GetKvInputShape(kvInput, step);
                            unsafe
                            {
                                var past = Tensor.F((float*)kv.Data.Resource, shape);
                                if (ringLive.Contains(kvInput.Name)) past.KvCap = kvRingCap;
                                feed[kvInput.Name] = past;
                            }
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
                        if (kvCacheHandles.TryGetValue(kvInput.Name, out var kv))
                        {
                            // Prefill sees an empty (0-row) window over the ring so the kernel writes the
                            // whole prompt's K/V straight into the persistent region.
                            unsafe
                            {
                                var past = Tensor.F((float*)kv.Data.Resource, shape);
                                if (ringLive.Contains(kvInput.Name)) past.KvCap = kvRingCap;
                                feed[kvInput.Name] = past;
                            }
                        }
                        else
                        {
                            feed[kvInput.Name] = Tensor.F(Array.Empty<float>(), shape);
                        }
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

                string text = _tokenizer.Detokenize(new[] { nextTokenId }, trimDummyPrefix: false);
                if (!string.IsNullOrEmpty(text))
                {
                    writer(new AgentDelta(AgentDeltaKind.Token, text));
                }

                if (pastKvInputs.Count > 0)
                {
                    foreach (var kvOutput in outputs.Keys.Where(name => name.Contains("present")))
                    {
                        string pastName = kvOutput.Replace("present", pastPrefix);
                        var tensor = outputs[kvOutput];

                        // Ring-marked present = the kernel already appended in place inside the
                        // persistent region this handle points at; handle stays put, nothing to copy.
                        if (tensor.KvCap > 0 && ringLive.Contains(pastName)) { KvRingSteps++; continue; }

                        // Demoted / non-ring cache: legacy copy-forward - single copy straight into a
                        // fresh persisted VOM region (source -> VOM), close the previous one.
                        ringLive.Remove(pastName);
                        var kv = DpxTensor.Alloc(owner, tensor.Shape, VomFormat.Float32, subdir: "Objects", name: pastName);
                        tensor.AsF().CopyTo(kv.ReadF32());
                        if (kvCacheHandles.TryGetValue(pastName, out var old))
                        {
                            if (Verbose)
                            {
                                Console.Error.WriteLine($"[DEBUG CACHE] Closing old cache: path={old.Data.Path}, pointer=0x{old.Data.Resource:X}");
                            }
                            old.Close();
                        }
                        kvCacheHandles[pastName] = kv;
                        if (Verbose)
                        {
                            Console.Error.WriteLine($"[DEBUG CACHE] Allocated new cache: key={pastName}, shape=[{string.Join(",", tensor.Shape)}], pointer=0x{kv.Data.Resource:X}");
                        }
                    }
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

            var kvCacheHandles = new Dictionary<string, DpxTensor>(StringComparer.OrdinalIgnoreCase);
            var seq = new List<int>(fullTokenIds);
            int pastLen = 0;
            int step = 0;

            // KV ring (CRQ190): ONE VOM region per past-KV input, preallocated to the most rows this
            // turn can reach (prompt + maxTokens) and kept for the whole turn - the ring-aware kernels
            // append each step's K/V in place at row `pastLen` and alias the region back out as present,
            // so there is no per-token alloc/copy/close here. KvCap on the feed tensor is the engage
            // signal; a cache whose present comes back unmarked (no ring-capable producer in the graph)
            // is demoted once to the legacy copy-forward below and stays there. Regions belong to the
            // turn owner and cascade-terminate with it.
            int kvRingCap = fullTokenIds.Count + _maxTokens;
            var ringLive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvInput in pastKvInputs)
            {
                kvCacheHandles[kvInput.Name] = DpxTensor.Alloc(owner, GetKvInputShape(kvInput, kvRingCap), VomFormat.Float32, subdir: "Objects", name: kvInput.Name);
                ringLive.Add(kvInput.Name);
            }

            _lastPrefillMs = 0; _lastDecodeMs = 0; _lastDecodeTokens = 0;
            var turnSw = System.Diagnostics.Stopwatch.StartNew();

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
                        // backing - no managed array, no Marshal.Copy. A ring-live cache also carries
                        // KvCap so the kernel writes this step's K/V in place at row `pastLen`.
                        int[] shape = GetKvInputShape(kvInput, pastLen);
                        unsafe
                        {
                            var past = Tensor.F((float*)kv.Data.Resource, shape);
                            if (ringLive.Contains(kvInput.Name)) past.KvCap = kvRingCap;
                            feed[kvInput.Name] = past;
                        }
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
                if (step == 0) { _lastPrefillMs = turnSw.Elapsed.TotalMilliseconds; turnSw.Restart(); }
                else { _lastDecodeMs = turnSw.Elapsed.TotalMilliseconds; }
                _lastDecodeTokens = step + 1;

                if (nextTokenId == eosId || (endOfTurnId >= 0 && nextTokenId == endOfTurnId))
                {
                    break;
                }

                string text = _tokenizer!.Detokenize(new[] { nextTokenId }, trimDummyPrefix: false);
                if (!string.IsNullOrEmpty(text))
                {
                    writer(new AgentDelta(AgentDeltaKind.Token, text));
                }

                foreach (var kvOutput in outputs.Keys.Where(name => name.Contains("present")))
                {
                    string pastName = kvOutput.Replace("present", pastPrefix);
                    var tensor = outputs[kvOutput];

                    // Ring-marked present = the kernel already appended in place inside the persistent
                    // region this handle points at; the handle stays put, nothing to copy or close.
                    if (tensor.KvCap > 0 && ringLive.Contains(pastName)) { KvRingSteps++; continue; }

                    // Demoted / non-ring cache: legacy copy-forward (alloc fresh, copy present, close old).
                    ringLive.Remove(pastName);
                    var kv = DpxTensor.Alloc(owner, tensor.Shape, VomFormat.Float32, subdir: "Objects", name: pastName);
                    tensor.AsF().CopyTo(kv.ReadF32());
                    if (kvCacheHandles.TryGetValue(pastName, out var old))
                    {
                        if (Verbose)
                        {
                            Console.Error.WriteLine($"[DEBUG CACHE] Closing old cache: path={old.Data.Path}, pointer=0x{old.Data.Resource:X}");
                        }
                        old.Close();
                    }
                    kvCacheHandles[pastName] = kv;
                    if (Verbose)
                    {
                        Console.Error.WriteLine($"[DEBUG CACHE] Allocated new cache: key={pastName}, shape=[{string.Join(",", tensor.Shape)}], pointer=0x{kv.Data.Resource:X}");
                    }
                }

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
            // owner gets Terminated on cancellation, but weights persist across turns (Dpx.Run's _winit
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
            catch (ObjectDisposedException) { Dg.Log("dpx", "DpxDecoder queue disposed while awaiting the next item — turn ended"); }
            return ValueTask.FromResult(false);
        }

        public T Current => _current;

        public ValueTask DisposeAsync()
        {
            try { _queue.Dispose(); } catch (Exception ex) { Dg.Error("dpx", ex); }
            _turnGate.Release();
            try { VomClass.Terminate(_owner); } catch (Exception ex) { Dg.Error("dpx", ex); }
            return ValueTask.CompletedTask;
        }
    }
}
