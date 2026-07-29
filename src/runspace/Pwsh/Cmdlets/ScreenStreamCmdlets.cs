using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Management.Automation;
using Android.Hardware.Display;
using Android.Media;
using Android.Views;
// 'Stream' is ambiguous between System.IO.Stream and Android.Media.Stream once Android.Media is imported.
// Every Stream in this file is a System.IO pipe stream (ADB stdin/stdout) — alias to disambiguate the file.
using Stream = System.IO.Stream;

namespace Subsystem.Pwsh.Cmdlets;

// ---------------------------------------------------------------------------
// Screen streaming — scrcpy's ScreenCapture + SurfaceEncoder pipeline mapped
// to PowerShell cmdlets. Symmetric P2P over ADB stdin/stdout pipes.
// ---------------------------------------------------------------------------

/// <summary>
/// Starts a symmetric ADB pipe P2P screen stream.
/// </summary>
[Cmdlet(VerbsLifecycle.Start, "AndroidScreenStream")]
public sealed class StartAndroidScreenStreamCmdlet : WrapperCmdlet
{
    [Parameter]
    public string Codec { get; set; } = "video/avc";

    [Parameter]
    public int Bitrate { get; set; } = 4_000_000;

    [Parameter]
    public int FrameRate { get; set; } = 60;

    [Parameter]
    public int MaxSize { get; set; } = 0;

    [Parameter]
    public int DisplayId { get; set; } = 0;

    [Parameter]
    public int KeyFrameInterval { get; set; } = 10;

    protected override void ProcessRecord()
    {
        try
        {
            var ctx = MainActivity.Instance
                ?? throw new InvalidOperationException("MainActivity is not available.");

            var dm = ctx.Resources?.DisplayMetrics
                ?? throw new InvalidOperationException("Cannot read DisplayMetrics.");

            int width  = dm.WidthPixels;
            int height = dm.HeightPixels;

            if (MaxSize > 0)
            {
                float scale = (float)MaxSize / Math.Max(width, height);
                if (scale < 1f)
                {
                    width  = (int)(width  * scale) & ~1;
                    height = (int)(height * scale) & ~1;
                }
            }

            var sessionId = Guid.NewGuid().ToString("N")[..8];

            // 1. Build MediaFormat for YUV420 input
            var fmt = MediaFormat.CreateVideoFormat(Codec, width, height)!;
            // 2135033992 = COLOR_FormatYUV420Flexible
            fmt.SetInteger(MediaFormat.KeyColorFormat, 2135033992);
            fmt.SetInteger(MediaFormat.KeyBitRate,   Bitrate);
            fmt.SetInteger(MediaFormat.KeyFrameRate, FrameRate);
            fmt.SetInteger(MediaFormat.KeyIFrameInterval, KeyFrameInterval);
            fmt.SetInteger("low-latency", 1);

            var encoder = MediaCodec.CreateEncoderByType(Codec)
                ?? throw new InvalidOperationException($"No encoder for {Codec}.");
            encoder.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
            encoder.Start();

            // 2. Create ImageReader to capture raw YUV420 frames from the VirtualDisplay
            // 35 = ImageFormat.YUV_420_888
            var imageReader = ImageReader.NewInstance(width, height, (Android.Graphics.ImageFormatType)35, 2)
                ?? throw new InvalidOperationException("Failed to create ImageReader.");

            var displayManager = (DisplayManager?)
                ctx.GetSystemService(Android.Content.Context.DisplayService)
                ?? throw new InvalidOperationException("DisplayManager unavailable.");

            var flags = Android.Hardware.Display.VirtualDisplayFlags.Presentation
                      | Android.Hardware.Display.VirtualDisplayFlags.Secure;
            var vd = displayManager.CreateVirtualDisplay(
                $"scrcpy-stream-{sessionId}",
                width, height, (int)dm.DensityDpi,
                imageReader.Surface,
                flags)
                ?? throw new InvalidOperationException("CreateVirtualDisplay failed.");

            // 3. Open raw stdio streams
            var stdoutStream = Console.OpenStandardOutput();
            var stdinStream = Console.OpenStandardInput();
            var cts = new CancellationTokenSource();

            var session = new ScreenStreamSession(encoder, vd, imageReader, cts);
            ScreenStreamRegistry.Register(sessionId, session);

            // Log configuration metadata strictly to stderr so stdout remains clean
            Console.Error.WriteLine($"CONFIG:SessionId={sessionId};Width={width};Height={height};Codec={Codec}");

            // Run P2P receiver and frame pumping loops
            var owner = Subsystem.Vom.Vom.CreateOwner(@"\System\ScreenStream");
            Subsystem.Vom.Vom.Spawn(owner, "ReceiveLoop", _ => ReceiveLoopAsync(stdinStream, cts.Token).GetAwaiter().GetResult());
            Subsystem.Vom.Vom.Spawn(owner, "PumpFrames", _ => PumpFramesPipe(encoder, imageReader, stdoutStream, width, height, cts.Token));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
        }
    }

    private static async Task ReceiveLoopAsync(Stream stdinStream, CancellationToken ct)
    {
        byte[] header = new byte[11];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int read = 0;
                while (read < 11)
                {
                    int r = await stdinStream.ReadAsync(header, read, 11 - read, ct);
                    if (r <= 0) return; // EOF
                    read += r;
                }

                byte channelId = header[0];
                uint frameIndex = BitConverter.ToUInt32(header, 1);
                ushort fragIndex = BitConverter.ToUInt16(header, 5);
                ushort totalFrags = BitConverter.ToUInt16(header, 7);
                ushort payloadSize = BitConverter.ToUInt16(header, 9);

                byte[] payload = new byte[payloadSize];
                int pread = 0;
                while (pread < payloadSize)
                {
                    int r = await stdinStream.ReadAsync(payload, pread, payloadSize - pread, ct);
                    if (r <= 0) return;
                    pread += r;
                }

                if (channelId == 2) // Input Injection Channel
                {
                    float x = BitConverter.ToSingle(payload, 0);
                    float y = BitConverter.ToSingle(payload, 4);
                    TerminalAccessibilityService.Instance?.DispatchTap(x, y);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Stdin Receive error: {ex.Message}");
            }
        }
    }

    private static async Task PumpFramesPipe(
        MediaCodec encoder, ImageReader reader, Stream stdoutStream, int w, int h, CancellationToken ct)
    {
        try
        {
            var info = new MediaCodec.BufferInfo();
            uint frameIndex = 0;
            int blastFramesLeft = 5; // Blast the first 5 frames as uncompressed raw YUV420

            while (!ct.IsCancellationRequested)
            {
                var img = reader.AcquireLatestImage();
                if (img == null)
                {
                    await Task.Delay(16, ct);
                    continue;
                }

                // 1. Pack YUV420 flat bytes
                byte[] yuvData = ScreenYuv.PackYuv420Flat(img, w, h);
                img.Close();

                // 2. Blast raw YUV420 (Channel 0) if within startup phase
                if (blastFramesLeft > 0)
                {
                    await SendPipeFrameAsync(stdoutStream, 0, yuvData, frameIndex, ct);
                    blastFramesLeft--;
                }

                // 3. Feed the frame into the H.264 encoder input buffer
                int inputBufIdx = encoder.DequeueInputBuffer(timeoutUs: 10_000);
                if (inputBufIdx >= 0)
                {
                    var inputBuf = encoder.GetInputBuffer(inputBufIdx);
                    if (inputBuf != null)
                    {
                        inputBuf.Clear();
                        inputBuf.Put(yuvData);
                        encoder.QueueInputBuffer(inputBufIdx, 0, yuvData.Length, presentationTimeUs: frameIndex * 16666, flags: 0);
                    }
                }

                // 4. Dequeue encoded NALUs and send over stdout Channel 1
                int outputBufIdx = encoder.DequeueOutputBuffer(info, timeoutUs: 10_000);
                if (outputBufIdx >= 0)
                {
                    var outputBuf = encoder.GetOutputBuffer(outputBufIdx);
                    if (outputBuf != null && info.Size > 0)
                    {
                        byte[] nalu = new byte[info.Size];
                        outputBuf.Position(info.Offset);
                        outputBuf.Get(nalu);
                        await SendPipeFrameAsync(stdoutStream, 1, nalu, frameIndex, ct);
                    }
                    encoder.ReleaseOutputBuffer(outputBufIdx, render: false);
                }

                frameIndex++;
                await Task.Delay(1, ct);
            }
        }
        catch (OperationCanceledException ex) { Dg.Warn("screen", ex); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PumpFramesPipe error: {ex.Message}");
        }
    }

    private static async Task SendPipeFrameAsync(Stream stdoutStream, byte channelId, byte[] data, uint frameIndex, CancellationToken ct)
    {
        const int maxPayload = 1400;
        int totalBytes = data.Length;
        ushort totalFragments = (ushort)((totalBytes + maxPayload - 1) / maxPayload);
        if (totalFragments == 0) totalFragments = 1;

        for (ushort i = 0; i < totalFragments; i++)
        {
            if (ct.IsCancellationRequested) break;
            int offset = i * maxPayload;
            int size = Math.Min(maxPayload, totalBytes - offset);

            byte[] packet = new byte[11 + size];
            packet[0] = channelId;
            BitConverter.TryWriteBytes(new Span<byte>(packet, 1, 4), frameIndex);
            BitConverter.TryWriteBytes(new Span<byte>(packet, 5, 2), i);
            BitConverter.TryWriteBytes(new Span<byte>(packet, 7, 2), totalFragments);
            BitConverter.TryWriteBytes(new Span<byte>(packet, 9, 2), (ushort)size);
            Buffer.BlockCopy(data, offset, packet, 11, size);

            await stdoutStream.WriteAsync(packet, 0, packet.Length, ct);
        }
        await stdoutStream.FlushAsync(ct);
    }

}

// Pure YUV420 plane-packing — a non-cmdlet helper (memory-bearing byte[] stays internal to the encode
// loop; it never crosses the cmdlet boundary, so it lives off the Cmdlet surface — SS008).
internal static class ScreenYuv
{
    internal static byte[] PackYuv420Flat(Android.Media.Image image, int w, int h)
    {
        int ySize = w * h;
        int uvSize = (w / 2) * (h / 2);
        byte[] yuv = new byte[ySize + uvSize * 2];

        var planes = image.GetPlanes()!;

        // Y Plane
        var yPlane = planes[0];
        var yBuf = yPlane.Buffer!;
        int yStride = yPlane.RowStride;
        if (yStride == w)
        {
            yBuf.Get(yuv, 0, ySize);
        }
        else
        {
            for (int row = 0; row < h; row++)
            {
                yBuf.Position(row * yStride);
                yBuf.Get(yuv, row * w, w);
            }
        }

        // U/V Planes
        var uPlane = planes[1];
        var vPlane = planes[2];
        var uBuf = uPlane.Buffer!;
        var vBuf = vPlane.Buffer!;
        int uStride = uPlane.RowStride;
        int vStride = vPlane.RowStride;
        int uPixStride = uPlane.PixelStride;
        int vPixStride = vPlane.PixelStride;

        int uvW = w / 2;
        int uvH = h / 2;
        int uOffset = ySize;
        int vOffset = ySize + uvSize;

        if (uPixStride == 1 && vPixStride == 1 && uStride == uvW && vStride == uvW)
        {
            uBuf.Get(yuv, uOffset, uvSize);
            vBuf.Get(yuv, vOffset, uvSize);
        }
        else
        {
            byte[] tempRowU = new byte[uStride];
            byte[] tempRowV = new byte[vStride];
            for (int row = 0; row < uvH; row++)
            {
                uBuf.Position(row * uStride);
                uBuf.Get(tempRowU, 0, Math.Min(uStride, uBuf.Remaining()));

                vBuf.Position(row * vStride);
                vBuf.Get(tempRowV, 0, Math.Min(vStride, vBuf.Remaining()));

                for (int col = 0; col < uvW; col++)
                {
                    yuv[uOffset + row * uvW + col] = tempRowU[col * uPixStride];
                    yuv[vOffset + row * uvW + col] = tempRowV[col * vPixStride];
                }
            }
        }

        return yuv;
    }
}

[Cmdlet(VerbsLifecycle.Stop, "AndroidScreenStream")]
[OutputType(typeof(bool))]
public sealed class StopAndroidScreenStreamCmdlet : WrapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    public string SessionId { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        try
        {
            Emit(ScreenStreamRegistry.Stop(SessionId));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"StopScreenStreamFailed: {ex.Message}");
        }
    }
}

[Cmdlet(VerbsCommon.Get, "AndroidScreenStream")]
[OutputType(typeof(PSObject))]
public sealed class GetAndroidScreenStreamCmdlet : WrapperCmdlet
{
    protected override void ProcessRecord() => Emit(ScreenStreamRegistry.List());
}

internal sealed class ScreenStreamSession(
    MediaCodec encoder,
    VirtualDisplay virtualDisplay,
    ImageReader imageReader,
    CancellationTokenSource cts)
{
    public MediaCodec      Encoder        { get; } = encoder;
    public VirtualDisplay  VirtualDisplay { get; } = virtualDisplay;
    public ImageReader     ImageReader    { get; } = imageReader;
    public CancellationTokenSource Cts   { get; } = cts;

    public PSObject ToInfo(string id)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("SessionId", id));
        o.Properties.Add(new PSNoteProperty("Active",    !Cts.IsCancellationRequested));
        return o;
    }
}

internal static class ScreenStreamRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScreenStreamSession>
        _sessions = new();

    public static void Register(string id, ScreenStreamSession s) => _sessions[id] = s;

    public static bool Stop(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return false;
        s.Cts.Cancel();
        s.Encoder.Stop();
        s.Encoder.Release();
        s.VirtualDisplay.Release();
        s.ImageReader.Close();
        return true;
    }

    public static System.Collections.Generic.IEnumerable<PSObject> List()
    {
        foreach (var (id, s) in _sessions)
            yield return s.ToInfo(id);
    }
}


