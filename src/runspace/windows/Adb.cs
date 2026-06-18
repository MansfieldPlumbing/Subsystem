using System;
using System.IO;
using System.Security.Cryptography;

namespace Subsystem.Windows;

// `ss adb` — drive an EXTERNAL Android device from the Windows head over the home-spun ADB transport
// (AdbConnection over SslStreamAdbTransport: TLS 1.3, client-key auth against the device's paired keystore —
// no platform adb.exe). The protocol core + the IAdbTransport seam are shared with the Android head; this is
// the Windows binding wired on (#75). The host key lives beside the registry db (%LocalAppData%\Subsystem via
// the FilesDir seam), generated once and reused so the device trusts the SAME key on every connect.
//   ss adb shell <host:port> <command...>   run a shell command on the device, print stdout
//   ss adb pubkey                            print this host's adb_keys public key (authorize it on the device)
internal static class Adb
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "shell"  => Shell(args[1..]),
                "pubkey" => PubKey(),
                _        => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ss adb: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  ss adb shell <host:port> <command...>   run a shell command on an external device");
        Console.Error.WriteLine("  ss adb pubkey                           print this host's adb_keys public key");
        return 2;
    }

    private static int PubKey()
    {
        using var key = LoadOrCreateKey();
        Console.WriteLine(AndroidPubKey.Encode(key, "Subsystem"));
        return 0;
    }

    private static int Shell(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: ss adb shell <host:port> <command...>"); return 2; }
        var (host, port) = ParseEndpoint(args[0]);
        var command = string.Join(' ', args[1..]);

        using var key = LoadOrCreateKey();
        using var conn = new AdbConnection(key, new SslStreamAdbTransport());
        conn.ConnectAsync(host, port).GetAwaiter().GetResult();
        var output = conn.ExecuteShellAsync(command).GetAwaiter().GetResult();
        Console.Out.Write(output);
        if (!output.EndsWith('\n')) Console.Out.WriteLine();
        return 0;
    }

    private static (string Host, int Port) ParseEndpoint(string s)
    {
        int i = s.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(s[(i + 1)..], out int port))
            throw new ArgumentException($"expected host:port, got '{s}'");
        return (s[..i], port);
    }

    // The host RSA key beside the registry db (the FilesDir seam), generated once and reused. adbd trusts it
    // after pairing/authorization; the SAME key signs every connection.
    private static RSA LoadOrCreateKey()
    {
        string dir = MainActivity.Instance!.FilesDir.AbsolutePath;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "adbkey.pem");
        if (File.Exists(path))
        {
            var existing = RSA.Create();
            existing.ImportFromPem(File.ReadAllText(path));
            return existing;
        }
        var fresh = RSA.Create(2048);
        File.WriteAllText(path, fresh.ExportPkcs8PrivateKeyPem());
        Dg.Log("adb", $"generated host adb key -> {path}");
        return fresh;
    }
}
