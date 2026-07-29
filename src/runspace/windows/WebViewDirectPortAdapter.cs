using System;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Subsystem.Windows;

// WebViewDirectPortAdapter — the WebView's endpoint on the DirectPort fabric. The WebView is NOT a Sub-VOM
// (a sandboxed renderer owns nothing of the native graph); it is a PEER producer/consumer, and because its
// sandbox can't open a shared D3D NT-handle the way a native consumer (VirtuaCam, the Win32 viewer) can, it
// is specifically the BUFFER endpoint of the fabric: it takes the CPU-side region and uploads to its own
// canvas/WebGL.
//
// The contract mirrors DirectPort's BroadcastManifest (frameValue + dims + format + region name): a region
// + a fence doorbell, no loopback socket. On Windows the region is a WebView2 shared buffer
// (CreateSharedBuffer → PostSharedBufferToScript) — genuine shared memory between this process and the
// renderer, so the host writes and the page reads the SAME 256-aligned row-major Float32 bytes with no copy
// on the CPU side. The fence is a sequence the host bumps and signals via PostWebMessageAsString; the JS
// consumer re-reads the shared region on each tick. (Android's analog is the in-process bridge + AHardwareBuffer.)
internal sealed class WebViewDirectPortAdapter : IDisposable
{
    private readonly WebView2 _web;
    private readonly string _regionName;
    private readonly int _floatCount;
    private CoreWebView2SharedBuffer? _buffer;   // the shared region — created once, re-read each frame
    private long _frameValue;                    // the fence: bumped per produced frame (DirectPort frameValue)

    public WebViewDirectPortAdapter(WebView2 web, string regionName, int floatCount)
    {
        _web = web;
        _regionName = regionName;
        _floatCount = floatCount;
    }

    // Stand up the shared region and hand it to the page ONCE (the BroadcastManifest equivalent rides in
    // additionalDataAsJson). After this the host and the page share the same memory; producing a frame is
    // just "write the bytes, bump the fence, ring the doorbell" — no re-post, no copy.
    public void Attach()
    {
        var env = _web.CoreWebView2.Environment;
        // 256-align the byte length (the kernel's row-pitch/DMA contract; VomFormat.Float32, row-major).
        int bytes = _floatCount * sizeof(float);
        int padded = (bytes + 255) & ~255;
        _buffer = env.CreateSharedBuffer((ulong)padded);

        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "directport.region",
            region = _regionName,
            format = "Float32",     // VomFormat.Float32 — the universal element
            rowMajor = true,
            align = 256,
            floats = _floatCount,
            bytes = padded,
        });
        _web.CoreWebView2.PostSharedBufferToScript(_buffer, CoreWebView2SharedBufferAccess.ReadOnly, manifest);
    }

    // Produce one frame: write the Float32 region into the shared memory, bump the fence, ring the doorbell.
    // The page reads the SAME bytes back (no copy) and renders. This is the producer half of the handoff.
    public void SignalFrame(ReadOnlySpan<float> data)
    {
        if (_buffer == null) return;
        var dst = _buffer.Buffer;   // the host-side pointer to the shared region (same pages the page sees)
        int n = Math.Min(data.Length, _floatCount);
        unsafe
        {
            var p = (float*)dst;
            for (int i = 0; i < n; i++) p[i] = data[i];
        }
        long fv = System.Threading.Interlocked.Increment(ref _frameValue);
        // The fence/doorbell — DirectPort signal_frame(): tell the consumer a new frame landed in the region.
        try { _web.CoreWebView2.PostWebMessageAsString("frame:" + fv); } catch (Exception ex) { Dg.Warn("webview", ex); }
    }

    public void Dispose()
    {
        try { _buffer?.Close(); } catch (Exception ex) { Dg.Warn("webview", ex); }
        _buffer = null;
    }

    // The JS consumer half — the page-side DirectPort endpoint. It's NOT a VOM: it just attaches to the
    // region (the shared ArrayBuffer), waits on the fence (the 'frame:' doorbell message), views the bytes
    // as a Float32Array, and blits. Injected before navigation so it's ready when the buffer arrives.
    public const string ConsumerScript = @"
(function () {
  // DirectPort buffer endpoint: receive the shared region once, then re-read it on each fence tick.
  let region = null, manifest = null;
  chrome.webview.addEventListener('sharedbufferreceived', function (e) {
    try { manifest = (typeof e.additionalData === 'string') ? JSON.parse(e.additionalData) : (e.additionalData || {}); } catch (_) { manifest = {}; }
    region = new Float32Array(e.getBuffer());           // the SAME bytes the host wrote — no copy
    window.__ssRegion = region; window.__ssManifest = manifest;
  });
  chrome.webview.addEventListener('message', function (e) {
    const d = e.data;
    if (typeof d === 'string' && d.indexOf('frame:') === 0 && region) {
      // The fence rang — the region holds a fresh frame. A real consumer blits to a canvas/WebGL here;
      // the slice just surfaces it so the handoff is observable end to end.
      window.__ssFrame = parseInt(d.slice(6), 10);
      window.dispatchEvent(new CustomEvent('ssframe', { detail: { frame: window.__ssFrame, region: region, manifest: manifest } }));
    }
  });
})();";
}
