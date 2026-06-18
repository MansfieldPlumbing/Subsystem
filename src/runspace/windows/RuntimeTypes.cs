using System;
using System.Collections.Generic;
using System.Threading;

namespace Subsystem.RuntimeBroker
{
    // The inference-runtime contract both heads implement: one Runtime per inference exec —
    // LiteRtRuntime / OnnxRuntime / GgmlRuntime — brokered behind the RuntimeBroker.
    public interface Runtime : IDisposable
    {
        RbFault? BringUp();
        IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, byte[]? audioBytes, CancellationToken ct = default);
        bool IsAlive { get; }
        string BackendName { get; }
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
