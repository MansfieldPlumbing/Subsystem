using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Subsystem;

public class AdbException : Exception
{
    public AdbException(string message) : base(message) { }
}

public class AdbMessage
{
    public uint Command;
    public uint Arg0;
    public uint Arg1;
    public uint DataLength;
    public uint DataCrc32;
    public uint Magic;
    public byte[] Data = Array.Empty<byte>();

    public static uint GetCommandMask(string cmd)
    {
        if (cmd.Length != 4) throw new ArgumentException("Command must be 4 characters");
        byte[] bytes = Encoding.ASCII.GetBytes(cmd);
        return BitConverter.ToUInt32(bytes, 0);
    }
}

public class AdbConnection : IDisposable
{
    public static readonly uint CMD_SYNC = AdbMessage.GetCommandMask("SYNC");
    public static readonly uint CMD_CNXN = AdbMessage.GetCommandMask("CNXN");
    public static readonly uint CMD_AUTH = AdbMessage.GetCommandMask("AUTH");
    public static readonly uint CMD_OPEN = AdbMessage.GetCommandMask("OPEN");
    public static readonly uint CMD_OKAY = AdbMessage.GetCommandMask("OKAY");
    public static readonly uint CMD_CLSE = AdbMessage.GetCommandMask("CLSE");
    public static readonly uint CMD_WRTE = AdbMessage.GetCommandMask("WRTE");
    public static readonly uint CMD_STLS = AdbMessage.GetCommandMask("STLS");

    public const uint AUTH_TOKEN = 1;
    public const uint AUTH_SIGNATURE = 2;
    public const uint AUTH_RSAPUBLICKEY = 3;

    public const uint MAX_PAYLOAD = 1024 * 1024;
    public const uint VERSION = 0x01000000;
    public const uint A_STLS_VERSION = 0x01000000;

    private Java.Net.Socket? _socket;
    private Javax.Net.Ssl.SSLSocket? _sslSocket;
    private Stream _stream = null!;
    private readonly RSA _rsaKey;
    private bool _isConnected;

    private ConcurrentDictionary<uint, TaskCompletionSource<AdbMessage>> _pendingStreams = new();
    private ConcurrentDictionary<uint, BlockingCollection<AdbMessage>> _streamQueues = new();
    private uint _nextLocalId = 1;

    public AdbConnection(RSA rsaKey)
    {
        _rsaKey = rsaKey;
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        // Plaintext Java socket. We use Android's Conscrypt TLS (SSLSocket) for the upgrade, NOT .NET
        // SslStream: .NET-on-Android won't reliably put a self-signed client cert on the wire for TLS
        // 1.3 (adbd kicks us with PEER_DID_NOT_RETURN_A_CERTIFICATE). Conscrypt does — it's the same
        // TLS path the working pairing client uses.
        _socket = await Task.Run(() => new Java.Net.Socket(host, port), cancellationToken);
        var rawIn = _socket.InputStream!;
        var rawOut = _socket.OutputStream!;
        Android.Util.Log.Info("SubsystemAdb", $"TCP connected to {host}:{port}");

        // adb wireless StartTLS (STLS), CLEARTEXT, before the upgrade:
        //   client -> CNXN ;  device -> STLS ;  client -> STLS ;  THEN the TLS handshake.
        var systemIdentity = Encoding.ASCII.GetBytes("host::Subsystem\0");
        await WriteMessageAsync(rawOut, CMD_CNXN, VERSION, MAX_PAYLOAD, systemIdentity, cancellationToken);

        var reply = await ReadMessageAsync(rawIn, cancellationToken);
        Android.Util.Log.Info("SubsystemAdb", $"after cleartext CNXN, device sent: {FormatCommand(reply.Command)} (arg0=0x{reply.Arg0:x8})");
        if (reply.Command != CMD_STLS)
            throw new AdbException($"Expected STLS, got {FormatCommand(reply.Command)}");

        await WriteMessageAsync(rawOut, CMD_STLS, A_STLS_VERSION, 0, Array.Empty<byte>(), cancellationToken);

        // TLS upgrade over the SAME socket (StartTLS). adbd authenticates by the client cert's public
        // key, matched against the keys stored at pairing — so the cert must use the SAME RSA key.
        var certReq = new CertificateRequest("CN=Subsystem", _rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var clientCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pkcs12 = clientCert.Export(X509ContentType.Pkcs12, "password");
        using (var ms = new MemoryStream(pkcs12))
        {
            var keyStore = Java.Security.KeyStore.GetInstance("PKCS12");
            keyStore.Load(ms, "password".ToCharArray());
            var kmf = Javax.Net.Ssl.KeyManagerFactory.GetInstance(Javax.Net.Ssl.KeyManagerFactory.DefaultAlgorithm!);
            kmf.Init(keyStore, "password".ToCharArray());

            // The default KeyManager.chooseClientAlias filters by the server's acceptable-CA list; adbd
            // doesn't name our self-signed issuer, so it returns null and no cert is sent. Wrap it so
            // chooseClientAlias ALWAYS returns our alias — forcing the cert onto the wire (what adb does).
            string alias = "";
            var aliasEnum = keyStore.Aliases();
            if (aliasEnum != null && aliasEnum.HasMoreElements) alias = aliasEnum.NextElement()!.ToString()!;
            var baseKm = (Javax.Net.Ssl.IX509KeyManager)kmf.GetKeyManagers()![0];
            Android.Util.Log.Info("SubsystemAdb", $"client cert alias='{alias}'");

            var sslContext = Javax.Net.Ssl.SSLContext.GetInstance("TLSv1.3");
            sslContext.Init(
                new Javax.Net.Ssl.IKeyManager[] { new ForcingKeyManager(baseKm, alias) },
                new Javax.Net.Ssl.ITrustManager[] { new TrustAllManager() },
                new Java.Security.SecureRandom());

            // Layer an SSLSocket over the already-connected plaintext socket (autoClose=true).
            _sslSocket = (Javax.Net.Ssl.SSLSocket)sslContext.SocketFactory!.CreateSocket(_socket, host, port, true)!;
            _sslSocket.UseClientMode = true;
            await Task.Run(() => _sslSocket.StartHandshake(), cancellationToken);
            Android.Util.Log.Info("SubsystemAdb", $"TLS OK (Conscrypt): {_sslSocket.Session?.Protocol} {_sslSocket.Session?.CipherSuite}");
        }

        _stream = new DuplexStream(_sslSocket.InputStream!, _sslSocket.OutputStream!);
        _ = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);

        // Post-TLS: per adbd source (adbd_wifi_secure_connect), after a successful TLS handshake the
        // DAEMON proactively send_connect()s its "device::..." CNXN to us once it has verified our
        // client cert against the paired keystore. We just receive it — we must NOT send our own CNXN
        // (that would re-enter handle_new_connection and fire another STLS). No banner => cert rejected
        // (daemon Kicked the transport).
        AdbMessage? banner = null;
        await Task.Run(() => { if (_handshakeQueue.TryTake(out var m, 8000)) banner = m; }, cancellationToken);
        if (banner == null) throw new AdbException("post-TLS: no CNXN from device (cert rejected / kicked?)");
        Android.Util.Log.Info("SubsystemAdb", $"post-TLS device sent: {FormatCommand(banner.Command)} banner='{Encoding.ASCII.GetString(banner.Data).Replace('\0','.')}'");
        if (banner.Command != CMD_CNXN)
            throw new AdbException($"post-TLS expected CNXN, got {FormatCommand(banner.Command)}");

        _isConnected = true;
        Android.Util.Log.Info("SubsystemAdb", "ADB elevated channel ESTABLISHED");
    }

    // A dedicated queue for the initial handshake, before full routing starts.
    private BlockingCollection<AdbMessage> _handshakeQueue = new();
    private async Task<AdbMessage> ReadFromLoopAsync(CancellationToken ct)
    {
        return await Task.Run(() => _handshakeQueue.Take(ct));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var headerBuffer = new byte[24];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await _stream.ReadAtLeastAsync(headerBuffer, 24, false, ct);
                if (bytesRead < 24) break;

                var msg = new AdbMessage
                {
                    Command = BitConverter.ToUInt32(headerBuffer, 0),
                    Arg0 = BitConverter.ToUInt32(headerBuffer, 4),
                    Arg1 = BitConverter.ToUInt32(headerBuffer, 8),
                    DataLength = BitConverter.ToUInt32(headerBuffer, 12),
                    DataCrc32 = BitConverter.ToUInt32(headerBuffer, 16),
                    Magic = BitConverter.ToUInt32(headerBuffer, 20)
                };

                if (msg.Magic != (msg.Command ^ 0xFFFFFFFF))
                {
                    throw new AdbException("ADB Magic mismatch");
                }

                if (msg.DataLength > 0)
                {
                    msg.Data = new byte[msg.DataLength];
                    await _stream.ReadAtLeastAsync(msg.Data, (int)msg.DataLength, false, ct);
                }

                if (!_isConnected)
                {
                    _handshakeQueue.Add(msg);
                }
                else
                {
                    RouteMessage(msg);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AdbConnection ReadLoop Error: {ex.Message}");
            // Handle disconnect
        }
    }

    private void RouteMessage(AdbMessage msg)
    {
        uint localId = msg.Arg1; // For OPEN/WRTE/CLSE/OKAY, arg1 is usually our localId
        if (msg.Command == CMD_OPEN) localId = msg.Arg0; // Should not receive OPEN from device usually
        
        if (_streamQueues.TryGetValue(localId, out var queue))
        {
            queue.Add(msg);
        }
    }

    public Task SendMessageAsync(uint command, uint arg0, uint arg1, byte[] data, CancellationToken ct = default)
        => WriteMessageAsync(_stream, command, arg0, arg1, data, ct);

    // Write a 24-byte adb message (+ payload) to an arbitrary stream. Used both on the raw
    // NetworkStream during the cleartext STLS handshake and on the SslStream afterwards.
    private static async Task WriteMessageAsync(Stream stream, uint command, uint arg0, uint arg1, byte[] data, CancellationToken ct = default)
    {
        var header = new byte[24];
        BitConverter.TryWriteBytes(header.AsSpan(0), command);
        BitConverter.TryWriteBytes(header.AsSpan(4), arg0);
        BitConverter.TryWriteBytes(header.AsSpan(8), arg1);
        BitConverter.TryWriteBytes(header.AsSpan(12), (uint)(data?.Length ?? 0));

        uint crc = 0; // adb's "crc32" is just a byte sum (checked only by pre-v2 peers)
        if (data != null) foreach (var b in data) crc += b;
        BitConverter.TryWriteBytes(header.AsSpan(16), crc);
        BitConverter.TryWriteBytes(header.AsSpan(20), command ^ 0xFFFFFFFF);

        await stream.WriteAsync(header, ct);
        if (data != null && data.Length > 0) await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    // Read one full adb message (header + payload) from an arbitrary stream — for the cleartext
    // STLS handshake, where the async ReadLoop isn't running yet.
    private async Task<AdbMessage> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[24];
        await ReadExactlyAsync(stream, header, 24);
        var msg = new AdbMessage
        {
            Command = BitConverter.ToUInt32(header, 0),
            Arg0 = BitConverter.ToUInt32(header, 4),
            Arg1 = BitConverter.ToUInt32(header, 8),
            DataLength = BitConverter.ToUInt32(header, 12),
            DataCrc32 = BitConverter.ToUInt32(header, 16),
            Magic = BitConverter.ToUInt32(header, 20)
        };
        if (msg.Magic != (msg.Command ^ 0xFFFFFFFF)) throw new AdbException("ADB Magic mismatch");
        if (msg.DataLength > 0)
        {
            msg.Data = new byte[msg.DataLength];
            await ReadExactlyAsync(stream, msg.Data, (int)msg.DataLength);
        }
        return msg;
    }

    private readonly object _idLock = new();

    // Open a shell: stream over the elevated channel, run a command, and collect its stdout.
    // adb stream lifecycle: OPEN(local,0,"shell:cmd\0") -> device OKAY(remote,local) -> device
    // WRTE(remote,local,data)+ (we OKAY each) -> device CLSE -> we CLSE. shell: (v1) returns raw output.
    public async Task<string> ExecuteShellAsync(string command, CancellationToken ct = default)
    {
        if (!_isConnected) throw new AdbException("ADB connection not established.");

        uint localId;
        lock (_idLock) { localId = _nextLocalId++; }
        var queue = new BlockingCollection<AdbMessage>();
        _streamQueues[localId] = queue;
        try
        {
            var payload = Encoding.UTF8.GetBytes("shell:" + command + "\0");
            await SendMessageAsync(CMD_OPEN, localId, 0, payload, ct);

            var sb = new StringBuilder();
            uint remoteId = 0;
            while (true)
            {
                var msg = await Task.Run(() => queue.Take(ct), ct);
                if (msg.Command == CMD_OKAY)
                {
                    remoteId = msg.Arg0;
                }
                else if (msg.Command == CMD_WRTE)
                {
                    if (msg.Data.Length > 0) sb.Append(Encoding.UTF8.GetString(msg.Data));
                    await SendMessageAsync(CMD_OKAY, localId, msg.Arg0, Array.Empty<byte>(), ct); // ack each WRTE
                }
                else if (msg.Command == CMD_CLSE)
                {
                    await SendMessageAsync(CMD_CLSE, localId, msg.Arg0, Array.Empty<byte>(), ct);
                    break;
                }
            }
            return sb.ToString();
        }
        finally
        {
            _streamQueues.TryRemove(localId, out _);
            queue.Dispose();
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private byte[] GetPublicKeyFormat(RSA rsa)
    {
        // Android adb_keys format: base64(packed RSAPublicKey struct) + " name\0".
        return Encoding.ASCII.GetBytes(AndroidPubKey.Encode(rsa, "Subsystem") + "\0");
    }

    private string FormatCommand(uint cmd)
    {
        var bytes = BitConverter.GetBytes(cmd);
        return Encoding.ASCII.GetString(bytes);
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _sslSocket?.Close(); } catch { }
        try { _socket?.Close(); } catch { }
    }

    // Accept any server cert: adbd's TLS cert is self-signed/ephemeral; authentication is by the
    // client cert's key against the paired keystore, not by validating the server chain.
    private sealed class TrustAllManager : Java.Lang.Object, Javax.Net.Ssl.IX509TrustManager
    {
        public void CheckClientTrusted(Java.Security.Cert.X509Certificate[]? chain, string? authType) { }
        public void CheckServerTrusted(Java.Security.Cert.X509Certificate[]? chain, string? authType) { }
        public Java.Security.Cert.X509Certificate[] GetAcceptedIssuers() => Array.Empty<Java.Security.Cert.X509Certificate>();
    }

    // Forces our client cert: chooseClientAlias always returns our alias regardless of the server's
    // acceptable-issuer list (adbd doesn't list our self-signed issuer, so the default KeyManager would
    // return null and send nothing). cert chain + private key delegate to the real KeyManager.
    private sealed class ForcingKeyManager : Java.Lang.Object, Javax.Net.Ssl.IX509KeyManager
    {
        private readonly Javax.Net.Ssl.IX509KeyManager _inner;
        private readonly string _alias;
        public ForcingKeyManager(Javax.Net.Ssl.IX509KeyManager inner, string alias) { _inner = inner; _alias = alias; }
        public string? ChooseClientAlias(string[]? keyType, Java.Security.IPrincipal[]? issuers, Java.Net.Socket? socket) => _alias;
        public string? ChooseServerAlias(string? keyType, Java.Security.IPrincipal[]? issuers, Java.Net.Socket? socket) => _inner.ChooseServerAlias(keyType, issuers, socket);
        public Java.Security.Cert.X509Certificate[]? GetCertificateChain(string? alias) => _inner.GetCertificateChain(_alias);
        public string[]? GetClientAliases(string? keyType, Java.Security.IPrincipal[]? issuers) => _inner.GetClientAliases(keyType, issuers);
        public Java.Security.IPrivateKey? GetPrivateKey(string? alias) => _inner.GetPrivateKey(_alias);
        public string[]? GetServerAliases(string? keyType, Java.Security.IPrincipal[]? issuers) => _inner.GetServerAliases(keyType, issuers);
    }

    // A Java socket exposes separate read (InputStream) and write (OutputStream) .NET Streams; the adb
    // message loop wants one duplex stream. This stitches them together. Async delegates to the inner
    // streams (Conscrypt-backed, fine for this low-rate control channel).
    private sealed class DuplexStream : Stream
    {
        private readonly Stream _in;
        private readonly Stream _out;
        public DuplexStream(Stream input, Stream output) { _in = input; _out = output; }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _in.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _in.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _out.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _out.WriteAsync(buffer, ct);
        public override void Flush() => _out.Flush();
        public override Task FlushAsync(CancellationToken ct) => _out.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { try { _in.Dispose(); } catch { } try { _out.Dispose(); } catch { } } }
    }
}
