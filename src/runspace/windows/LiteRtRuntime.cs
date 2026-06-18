using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Subsystem.RuntimeBroker
{
    // In-process LiteRT-LM Runtime for the Windows head — CPU rung, P/Invoke against the litert-lm C API.
    public sealed class LiteRtRuntime : Runtime, IDisposable
    {
        private readonly string _modelPath;
        private readonly string? _cacheDir;
        private readonly string? _systemInstruction;
        private readonly string _unitId;

        private LiteRtLmEngineHandle? _engineHandle;
        private LiteRtLmConversationHandle? _conversationHandle;
        
        private readonly object _gate = new();
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private volatile bool _ready;
        private RbFault? _initFault;
        private string _backendName = "loading…";

        private ChannelWriter<AgentDelta>? _activeChannel;
        private ThinkingSplitter? _activeSplitter;

        static LiteRtRuntime()
        {
            try
            {
                // Register resolver to load companion DLLs from temp/dev directories before litert-lm.dll
                NativeLibrary.SetDllImportResolver(typeof(LiteRtRuntime).Assembly, ResolveDll);
            }
            catch (InvalidOperationException)
            {
                // Already registered in this process
            }
        }

        // Self-governing exec: the broker hands in the CONTRACT (model + system prompt, resolved from Cm),
        // no placement knobs. This runtime self-selects CPU (litert-lm picks its internal WebGPU path
        // when available).
        public LiteRtRuntime(string modelPath, string unitId, string? systemInstruction = null, string? cacheDir = null)
        {
            _modelPath = modelPath;
            _unitId = unitId;
            _systemInstruction = systemInstruction;
            _cacheDir = cacheDir;
        }

        public bool IsAlive => _ready && _initFault == null && _conversationHandle != null && !_conversationHandle.IsInvalid && !_conversationHandle.IsClosed;
        public string BackendName => _backendName;

        public RbFault? BringUp()
        {
            if (_ready) return null;
            if (_initFault != null) return _initFault;

            lock (_gate)
            {
                if (_ready) return null;
                if (_initFault != null) return _initFault;

                // Self-governed placement: CPU rung (litert-lm self-selects its internal WebGPU path).
                var admitted = new[] { "cpu" };
                string? lastDetail = null;
                foreach (var name in admitted)
                {
                    // Structure it to support "gpu" rung later, but default to "cpu"
                    string targetBackend = name.ToLowerInvariant();
                    if (targetBackend != "cpu" && targetBackend != "gpu")
                    {
                        targetBackend = "cpu";
                    }

                    try
                    {
                        // 1. Create engine settings (model_path, backend_str, vision_backend_str, audio_backend_str)
                        var settings = NativeMethods.litert_lm_engine_settings_create(NativeMethods.Utf8(_modelPath), NativeMethods.Utf8(targetBackend), null, null);
                        if (settings.IsInvalid)
                        {
                            lastDetail = "Failed to create engine settings";
                            continue;
                        }

                        using (settings)
                        {
                            if (!string.IsNullOrEmpty(_cacheDir))
                            {
                                NativeMethods.litert_lm_engine_settings_set_cache_dir(settings, NativeMethods.Utf8(_cacheDir));
                            }

                            // 2. Create engine
                            _engineHandle = NativeMethods.litert_lm_engine_create(settings);
                        }

                        if (_engineHandle.IsInvalid)
                        {
                            lastDetail = "Failed to create engine";
                            continue;
                        }

                        // 3. Create conversation config
                        var sessionConfig = NativeMethods.litert_lm_session_config_create();
                        if (sessionConfig.IsInvalid)
                        {
                            lastDetail = "Failed to create session config";
                            _engineHandle.Dispose();
                            _engineHandle = null;
                            continue;
                        }

                        using (sessionConfig)
                        {
                            var convConfig = NativeMethods.litert_lm_conversation_config_create();
                            if (convConfig.IsInvalid)
                            {
                                lastDetail = "Failed to create conversation config";
                                _engineHandle.Dispose();
                                _engineHandle = null;
                                continue;
                            }

                            using (convConfig)
                            {
                                NativeMethods.litert_lm_conversation_config_set_session_config(convConfig, sessionConfig);

                                if (!string.IsNullOrEmpty(_systemInstruction))
                                {
                                    NativeMethods.litert_lm_conversation_config_set_system_message(convConfig, NativeMethods.Utf8(_systemInstruction));
                                }

                                // 4. Create conversation
                                _conversationHandle = NativeMethods.litert_lm_conversation_create(_engineHandle, convConfig);
                            }
                        }

                        if (_conversationHandle.IsInvalid)
                        {
                            lastDetail = "Failed to create conversation";
                            _engineHandle.Dispose();
                            _engineHandle = null;
                            continue;
                        }

                        _backendName = name.ToUpperInvariant();
                        _ready = true;
                        return null;
                    }
                    catch (Exception ex)
                    {
                        lastDetail = ex.Message;
                    }
                }

                _initFault = new RbFault(RbFaultClass.BringUpFailed, _unitId,
                    string.Join("/", admitted), lastDetail ?? "no admitted backend initialized");
                return _initFault;
            }
        }

        public async IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var fault = BringUp();
            if (fault != null)
            {
                yield return new AgentDelta(AgentDeltaKind.Error, fault.NativeDetail, Fault: fault);
                yield break;
            }

            var channel = Channel.CreateUnbounded<AgentDelta>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var splitter = new ThinkingSplitter(channel.Writer);

            await _turnGate.WaitAsync(ct);
            
            GCHandle gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            IntPtr callbackData = GCHandle.ToIntPtr(gcHandle);
            
            using var ctReg = ct.Register(() =>
            {
                try
                {
                    if (_conversationHandle != null && !_conversationHandle.IsInvalid)
                    {
                        NativeMethods.litert_lm_conversation_cancel_process(_conversationHandle);
                    }
                }
                catch { }
            });

            _activeChannel = channel.Writer;
            _activeSplitter = splitter;

            try
            {
                var msgObj = new { role = "user", content = prompt };
                string msgJson = JsonSerializer.Serialize(msgObj);
                string ctxJson = "{}";

                int res;
                unsafe
                {
                    res = NativeMethods.litert_lm_conversation_send_message_stream(
                        _conversationHandle!,
                        NativeMethods.Utf8(msgJson),
                        NativeMethods.Utf8(ctxJson),
                        IntPtr.Zero,
                        &OnTokenCallback,
                        callbackData);
                }

                if (res != 0)
                {
                    channel.Writer.TryComplete(new Exception("Native litert_lm_conversation_send_message_stream failed"));
                }

                await foreach (var delta in channel.Reader.ReadAllAsync(ct))
                {
                    yield return delta;
                }
            }
            finally
            {
                _activeChannel = null;
                _activeSplitter = null;
                if (gcHandle.IsAllocated)
                {
                    gcHandle.Free();
                }
                _turnGate.Release();
            }
        }

        internal void OnChunk(string chunk, bool isFinal, string errorMsg)
        {
            try
            {
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    var detail = errorMsg;
                    var cls = detail.Contains("not alive", StringComparison.OrdinalIgnoreCase) ? RbFaultClass.ConversationDefunct
                            : detail.Contains("cancel", StringComparison.OrdinalIgnoreCase)    ? RbFaultClass.DecodeCancelled
                            : RbFaultClass.DecodeFaulted;
                    
                    _activeChannel?.TryComplete(new RbFaultException(new RbFault(cls, _unitId, _backendName, detail)));
                    return;
                }

                if (!string.IsNullOrEmpty(chunk))
                {
                    _activeSplitter?.Push(chunk);
                }

                if (isFinal)
                {
                    _activeSplitter?.Flush();
                    _activeChannel?.TryComplete();
                }
            }
            catch (Exception ex)
            {
                _activeChannel?.TryComplete(ex);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe void OnTokenCallback(IntPtr callbackData, byte* chunkPtr, byte isFinalByte, byte* errorMsgPtr)
        {
            try
            {
                if (callbackData == IntPtr.Zero) return;
                
                GCHandle gcHandle = GCHandle.FromIntPtr(callbackData);
                if (gcHandle.Target is LiteRtRuntime client)
                {
                    string chunk = chunkPtr != null ? Marshal.PtrToStringUTF8((IntPtr)chunkPtr) ?? "" : "";
                    bool isFinal = isFinalByte != 0;
                    string errorMsg = errorMsgPtr != null ? Marshal.PtrToStringUTF8((IntPtr)errorMsgPtr) ?? "" : "";
                    
                    client.OnChunk(chunk, isFinal, errorMsg);
                }
            }
            catch
            {
                // NEVER let a managed exception cross the native boundary
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _conversationHandle?.Dispose();
                _conversationHandle = null;
                _engineHandle?.Dispose();
                _engineHandle = null;
            }
        }

        // ---- DllImportResolver logic ----

        private static IntPtr ResolveDll(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == "litert-lm")
            {
                string? dir = FindExtractionDir();
                if (dir != null)
                {
                    PreloadDll(dir, "dxil.dll");
                    PreloadDll(dir, "dxcompiler.dll");
                    PreloadDll(dir, Path.Combine("vendors", "intel_openvino", "dispatch", "LiteRtDispatch.dll"));
                    
                    string facadePath = Path.Combine(dir, "litert-lm.dll");
                    if (File.Exists(facadePath))
                    {
                        if (NativeLibrary.TryLoad(facadePath, out IntPtr handle))
                        {
                            return handle;
                        }
                    }
                }
            }
            return IntPtr.Zero;
        }

        private static void PreloadDll(string dir, string relPath)
        {
            string fullPath = Path.Combine(dir, relPath);
            if (File.Exists(fullPath))
            {
                try
                {
                    NativeLibrary.Load(fullPath);
                }
                catch { }
            }
        }

        private static string? FindExtractionDir()
        {
            // 1. Check temp extraction directory for single-file publishes
            string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "ss");
            string netTempParent = Path.Combine(Path.GetTempPath(), ".net", processName);
            if (Directory.Exists(netTempParent))
            {
                foreach (var subDir in Directory.GetDirectories(netTempParent))
                {
                    if (File.Exists(Path.Combine(subDir, "litert-lm.dll")))
                    {
                        return subDir;
                    }
                }
            }
            
            // 2. Check directory beside executable
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, "litert-lm.dll")))
            {
                return exeDir;
            }
            
            // 3. Check dev staging directory (S:\litert)
            string driveRoot = Path.GetPathRoot(exeDir) ?? "";
            string litertStagedDir = Path.Combine(driveRoot, "litert");
            if (File.Exists(Path.Combine(litertStagedDir, "litert-lm.dll")))
            {
                return litertStagedDir;
            }
            
            return null;
        }

        // ---- ThinkingSplitter & CleanText ----

        private sealed class ThinkingSplitter
        {
            private const string Open = "<|channel>";
            private const string Close = "<channel|>";
            private readonly ChannelWriter<AgentDelta> _w;
            private int _emittedThink, _emittedAnswer;
            private string _buf = "";
            
            public ThinkingSplitter(ChannelWriter<AgentDelta> w) { _w = w; }

            public void Push(string chunk)
            {
                if (string.IsNullOrEmpty(chunk)) return;
                chunk = CleanText(chunk);
                _buf = chunk.StartsWith(_buf, StringComparison.Ordinal) ? chunk : _buf + chunk;
                string full = _buf;
                string answer, think;
                int o = full.IndexOf(Open, StringComparison.Ordinal);
                if (o < 0) { answer = full; think = ""; }
                else
                {
                    int ts = o + Open.Length;
                    int c = full.IndexOf(Close, ts, StringComparison.Ordinal);
                    if (c < 0) { think = full.Substring(ts); answer = full.Substring(0, o); }
                    else { think = full.Substring(ts, c - ts); answer = full.Substring(0, o) + full.Substring(c + Close.Length); }
                }
                answer = TrimTrailingPartialMarker(answer);

                if (think.Length > _emittedThink) { _w.TryWrite(new AgentDelta(AgentDeltaKind.Think, think.Substring(_emittedThink))); _emittedThink = think.Length; }
                if (answer.Length > _emittedAnswer) { _w.TryWrite(new AgentDelta(AgentDeltaKind.Token, answer.Substring(_emittedAnswer))); _emittedAnswer = answer.Length; }
            }

            public void Flush() { }

            private static string TrimTrailingPartialMarker(string s)
            {
                foreach (var mk in new[] { Open, Close })
                    for (int len = Math.Min(mk.Length - 1, s.Length); len > 0; len--)
                        if (s.EndsWith(mk.Substring(0, len), StringComparison.Ordinal)) return s.Substring(0, s.Length - len);
                return s;
            }

            private static string CleanText(string raw)
            {
                var probe = raw.TrimStart();
                if (!probe.StartsWith("{") && !probe.StartsWith("[")) return raw;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("content", out var c))
                    {
                        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? raw;
                        if (c.ValueKind == JsonValueKind.Array)
                        {
                            var sb = new System.Text.StringBuilder();
                            foreach (var part in c.EnumerateArray())
                                if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                            if (sb.Length > 0) return sb.ToString();
                        }
                    }
                }
                catch { }
                return raw;
            }
        }
    }

    // ---- SafeHandle subclasses ----

    public class LiteRtLmEngineSettingsHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LiteRtLmEngineSettingsHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            NativeMethods.litert_lm_engine_settings_delete(handle);
            return true;
        }
    }

    public class LiteRtLmEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LiteRtLmEngineHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            NativeMethods.litert_lm_engine_delete(handle);
            return true;
        }
    }

    public class LiteRtLmSessionConfigHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LiteRtLmSessionConfigHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            NativeMethods.litert_lm_session_config_delete(handle);
            return true;
        }
    }

    public class LiteRtLmConversationConfigHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LiteRtLmConversationConfigHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            NativeMethods.litert_lm_conversation_config_delete(handle);
            return true;
        }
    }

    public class LiteRtLmConversationHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LiteRtLmConversationHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            NativeMethods.litert_lm_conversation_delete(handle);
            return true;
        }
    }

    // ---- P/Invoke NativeMethods ----
    // Classic [DllImport] (not source-generated [LibraryImport]) so the dotnet-free `ss build self` — whose
    // in-proc Roslyn runs NO source generators — can compile the binding and the ouroboros can reproduce it
    // (prime-directive rule 0: anything we add must be buildable BY the system). On win-x64 the x64 ABI
    // unifies the calling conventions, so this is behavior-equivalent to the prior generated stubs. DllImport
    // has no UTF-8 string marshalling, so the C API's char* args cross as null-terminated byte[] (Utf8()).
    internal static unsafe class NativeMethods
    {
        private const string DllName = "litert-lm";

        // UTF-8, null-terminated — how the litert-lm C API expects its char* arguments.
        public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

        [DllImport(DllName)]
        public static extern LiteRtLmEngineSettingsHandle litert_lm_engine_settings_create(
            byte[] modelPath, byte[] backendStr, byte[]? visionBackendStr, byte[]? audioBackendStr);

        [DllImport(DllName)]
        public static extern void litert_lm_engine_settings_delete(IntPtr settings);

        [DllImport(DllName)]
        public static extern void litert_lm_engine_settings_set_cache_dir(
            LiteRtLmEngineSettingsHandle settings, byte[] cacheDir);

        [DllImport(DllName)]
        public static extern LiteRtLmEngineHandle litert_lm_engine_create(
            LiteRtLmEngineSettingsHandle settings);

        [DllImport(DllName)]
        public static extern void litert_lm_engine_delete(IntPtr engine);

        [DllImport(DllName)]
        public static extern LiteRtLmSessionConfigHandle litert_lm_session_config_create();

        [DllImport(DllName)]
        public static extern void litert_lm_session_config_delete(IntPtr sessionConfig);

        [DllImport(DllName)]
        public static extern LiteRtLmConversationConfigHandle litert_lm_conversation_config_create();

        [DllImport(DllName)]
        public static extern void litert_lm_conversation_config_delete(IntPtr convConfig);

        [DllImport(DllName)]
        public static extern void litert_lm_conversation_config_set_session_config(
            LiteRtLmConversationConfigHandle convConfig, LiteRtLmSessionConfigHandle sessionConfig);

        [DllImport(DllName)]
        public static extern void litert_lm_conversation_config_set_system_message(
            LiteRtLmConversationConfigHandle convConfig, byte[] systemMessage);

        [DllImport(DllName)]
        public static extern LiteRtLmConversationHandle litert_lm_conversation_create(
            LiteRtLmEngineHandle engine, LiteRtLmConversationConfigHandle convConfig);

        [DllImport(DllName)]
        public static extern void litert_lm_conversation_delete(IntPtr conversation);

        [DllImport(DllName)]
        public static extern void litert_lm_conversation_cancel_process(
            LiteRtLmConversationHandle conversation);

        [DllImport(DllName)]
        public static extern int litert_lm_conversation_send_message_stream(
            LiteRtLmConversationHandle conversation,
            byte[] msgJson,
            byte[] ctxJson,
            IntPtr visualTokenBudgetPtr,
            delegate* unmanaged[Cdecl]<IntPtr, byte*, byte, byte*, void> callback,
            IntPtr callbackData);
    }
}
