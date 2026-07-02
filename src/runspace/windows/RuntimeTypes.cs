using System;
using System.Collections.Generic;
using System.Threading;

namespace Subsystem
{
    // ITurnSource — yields a turn as AgentDeltas. The native DPX citizen is one; a mounted foreign engine
    // is one. The only shape they share.
    public interface ITurnSource : IDisposable
    {
        RbFault? BringUp();
        IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, CancellationToken ct = default);
        IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, byte[]? imageBytes, CancellationToken ct = default)
            => StreamTurnAsync(prompt, audioBytes, ct);
        bool IsAlive { get; }
        string BackendName { get; }
        Benchmark? GetBenchmark() => null;
    }

    // Per-turn decode counters (mirrors the Android head's Runtime.cs contract member-for-member).
    public sealed record Benchmark(double InitSeconds, double TimeToFirstTokenSeconds,
        int PrefillTokens, double PrefillTokensPerSecond, int DecodeTokens, double DecodeTokensPerSecond);

    // Runtime — the GUEST contract. A foreign, boundary-crossing engine (LiteRT-LM, ONNX, GGML) signs this to
    // be mounted through the guest door. DPX IS NOT A RUNTIME — she is the native citizen, an ITurnSource.
    public interface Runtime : ITurnSource
    {
    }

    public enum AgentDeltaKind
    {
        Token,
        Think,
        ToolCall,
        ToolResult,
        Error
    }

    public readonly record struct AgentDelta(AgentDeltaKind Kind, string Text, string? Name = null, RbFault? Fault = null);

    public enum RbFaultClass
    {
        AdmissionRefused,
        BringUpFailed,
        VerificationFailed,
        EngineReclaimed,
        ConversationDefunct,
        DecodeCancelled,
        DecodeFaulted,
        BackendUnavailable,
    }

    public sealed record RbFault(RbFaultClass Class, string UnitId, string Backend, string NativeDetail);

    public sealed class RbFaultException : Exception
    {
        public RbFault Fault { get; }
        public RbFaultException(RbFault fault)
            : base($"{fault.Class} [{fault.UnitId}/{fault.Backend}] {fault.NativeDetail}")
            => Fault = fault;
    }
}
