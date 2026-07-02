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

    // Runtime — the GUEST contract. A foreign, boundary-crossing engine (LiteRT-LM, ONNX, GGML) signs this
    // to be mounted through the guest door; it self-governs admission/OOM in BringUp. DPX IS NOT A RUNTIME —
    // she is the native citizen, in-process on the VOM, nothing to broker; she is an ITurnSource, never this.
    public interface Runtime : ITurnSource
    {
        SideConversation? OpenSideConversation(string? systemPrompt, ToolDescriptor[]? tools) => null;
    }

    public abstract class SideConversation : IDisposable
    {
        public abstract IAsyncEnumerable<AgentDelta> StreamTurnAsync(string prompt, CancellationToken ct = default);
        public abstract void Dispose();
    }

    public sealed record Benchmark(double InitSeconds, double TimeToFirstTokenSeconds,
        int PrefillTokens, double PrefillTokensPerSecond, int DecodeTokens, double DecodeTokensPerSecond);
}
