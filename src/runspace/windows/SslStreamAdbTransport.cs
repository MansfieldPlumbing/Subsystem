using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Subsystem;

// SslStream binding for the ADB transport seam (IAdbTransport) — the Windows head's socket + TLS. Plaintext
// TcpClient for the cleartext STLS handshake, then a TLS 1.3 SslStream presenting a self-signed CN=Subsystem
// client cert that carries clientKey (adbd authenticates by the cert's public key against the paired
// keystore). Named for the MECHANISM (SslStream), not the platform (SS012) — the mirror of
// ConscryptAdbTransport on Android. Pure System.* (no Java/Android), so it lives in the Windows head; the
// protocol core (AdbConnection) speaks the wire over the duplex Stream this returns. Async is legitimate
// here — Adb is a seam, not the synchronous core (SS018).
//
// Two parities with the Conscrypt path that adbd forces:
//   * the client cert must reach SslStream WITH a usable private key — CreateSelfSigned's ephemeral key is
//     not directly usable by Schannel, so we round-trip through PKCS12 the same way the Conscrypt binding
//     exports a PKCS12 keystore.
//   * adbd does NOT advertise our self-signed issuer, so SslStream's default client-cert selection would
//     send nothing (the same PEER_DID_NOT_RETURN_A_CERTIFICATE adbd kicks with). The selection callback
//     forces our cert regardless of acceptableIssuers — the mirror of the Conscrypt ForcingKeyManager.
public sealed class SslStreamAdbTransport : IAdbTransport
{
    private TcpClient? _tcp;
    private SslStream? _ssl;

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        Dg.Log("adb", $"TCP connected to {host}:{port}");
        return _tcp.GetStream();
    }

    public async Task<Stream> UpgradeToTlsAsync(RSA clientKey, string host, int port, CancellationToken ct = default)
    {
        if (_tcp == null) throw new AdbException("transport not connected (call ConnectAsync first)");

        // The client cert carries clientKey; adbd matches its public key against the paired keystore. Round-trip
        // through PKCS12 (EphemeralKeySet — in-memory, no disk artifact) so SslStream/Schannel can present the
        // cert WITH its private key; CreateSelfSigned's ephemeral key is not directly usable in the handshake.
        using var selfSigned = BuildClientCert(clientKey);
        var pkcs12 = selfSigned.Export(X509ContentType.Pkcs12, "password");
        var clientCert = X509CertificateLoader.LoadPkcs12(pkcs12, "password", X509KeyStorageFlags.EphemeralKeySet);

        _ssl = new SslStream(
            _tcp.GetStream(),
            leaveInnerStreamOpen: false,
            // adbd's server cert is self-signed/ephemeral; authentication is by the client key vs the paired
            // keystore, never the server chain — trust all (mirror of the Conscrypt TrustAllManager).
            userCertificateValidationCallback: static (_, _, _, _) => true,
            // Force OUR cert regardless of the server's acceptable-issuer list (adbd lists none of ours, so the
            // default selection would send nothing) — the mirror of the Conscrypt ForcingKeyManager.
            userCertificateSelectionCallback: (_, _, _, _, _) => clientCert);

        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ClientCertificates = new X509CertificateCollection { clientCert },
            EnabledSslProtocols = SslProtocols.Tls13,
        }, ct);

        Dg.Log("adb", $"TLS OK (SslStream): {_ssl.SslProtocol} {_ssl.NegotiatedCipherSuite}");
        return _ssl;
    }

    // Build the self-signed client cert from the ADB RSA key (CN=Subsystem) — the same shape the Conscrypt path
    // builds. Pure System.Security.Cryptography, identical on every head.
    private static X509Certificate2 BuildClientCert(RSA clientKey)
    {
        var req = new CertificateRequest("CN=Subsystem", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    public void Dispose()
    {
        try { _ssl?.Dispose(); } catch (Exception ex) { Dg.Log("adb", "ssl close: " + ex.Message); }
        try { _tcp?.Dispose(); } catch (Exception ex) { Dg.Log("adb", "socket close: " + ex.Message); }
    }
}
