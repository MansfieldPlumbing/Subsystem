using System;
using System.Collections.Generic;

namespace Subsystem.Device;

// McpRelay — fragments an MCP JSON-RPC line (newline-delimited JSON, the same wire `ss mcp` speaks over
// stdio — see windows/Mcp.cs) over a small-MTU notify/write characteristic (BLE GATT: ~20-512B depending
// on negotiated MTU). Shared by both heads: Android's DpxBleAdvert.cs (GATT server) and Windows'
// DpxBleScan.cs (GATT client) fragment/reassemble through this ONE seam — one concept, N couriers
// (invariant 9). Pure System.* — no platform API — so it compiles into both heads unmodified.
//
// Framing per chunk: [seq:1][total:1][payload...]. seq/total cap a message at 255 fragments; at a
// conservative 180B payload/chunk that is ~45KB per message — comfortably over any single MCP tool
// call/result this rung needs to carry (screen surface stays on the AOA/WinUsb rungs above it).
//
// Latest-wins (invariant 8): Reassembler is intentionally forgetful. A NEW seq==0 arriving mid-reassembly
// drops whatever was buffered and starts over — a lost fragment degrades to "wait for the next full
// send", never a permanently wedged channel. This is correct for a best-effort notify characteristic;
// it is NOT a reliable transport, and callers must not assume delivery.
public static class McpRelay
{
    public const int HeaderSize = 2;

    // Split `message` into chunks no larger than `mtu` bytes INCLUDING the 2-byte header. Order matters —
    // the receiver reassembles strictly by arrival, not by seq (GATT notifications/writes are already
    // ordered per-connection).
    public static List<byte[]> Fragment(ReadOnlySpan<byte> message, int mtu)
    {
        int payloadPerChunk = Math.Max(1, mtu - HeaderSize);
        int total = Math.Max(1, (message.Length + payloadPerChunk - 1) / payloadPerChunk);
        if (total > 255) throw new ArgumentException($"message too large to fragment (>{255 * payloadPerChunk}B at mtu={mtu})");

        var chunks = new List<byte[]>(total);
        for (int seq = 0; seq < total; seq++)
        {
            int offset = seq * payloadPerChunk;
            int size = Math.Min(payloadPerChunk, message.Length - offset);
            byte[] chunk = new byte[HeaderSize + size];
            chunk[0] = (byte)seq;
            chunk[1] = (byte)total;
            message.Slice(offset, size).CopyTo(chunk.AsSpan(HeaderSize));
            chunks.Add(chunk);
        }
        return chunks;
    }

    // Reassembles one message's worth of chunks. One instance per connection/direction — it holds the
    // in-progress buffer, so it is NOT thread-safe and must not be shared across concurrent senders.
    public sealed class Reassembler
    {
        private byte[]? _buf;
        private int _expectedTotal;
        private int _received;

        // Feed one chunk. Returns true and sets `message` when the fragment completes a full message.
        public bool Feed(ReadOnlySpan<byte> chunk, out byte[] message)
        {
            message = Array.Empty<byte>();
            if (chunk.Length < HeaderSize) return false;   // malformed — drop, do not wedge

            byte seq = chunk[0], total = chunk[1];
            if (total == 0) return false;

            if (seq == 0)
            {
                // Restart — either the true start of a message, or a stale partial being superseded
                // (latest-wins: never block waiting on a fragment that was already dropped on the floor).
                _expectedTotal = total;
                _received = 0;
                _buf = new byte[(chunk.Length - HeaderSize) * total];   // sized off the FIRST chunk's payload width
            }

            if (_buf == null || seq >= _expectedTotal) return false;    // out-of-order start / no in-progress message — drop

            int payloadLen = chunk.Length - HeaderSize;
            int perChunk = _buf.Length / _expectedTotal;
            int offset = seq * perChunk;
            if (offset + payloadLen > _buf.Length) return false;        // corrupt length — drop rather than throw

            chunk.Slice(HeaderSize).CopyTo(_buf.AsSpan(offset, payloadLen));
            _received++;

            if (_received < _expectedTotal) return false;

            // Last chunk may be short — trim to the actual final offset+length.
            int realLength = offset + payloadLen;
            message = _received == _expectedTotal && realLength == _buf.Length
                ? _buf
                : _buf.AsSpan(0, realLength).ToArray();
            _buf = null;
            return true;
        }
    }
}
