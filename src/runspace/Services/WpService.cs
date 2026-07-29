using System;
using System.Text.Json;
using System.Threading;
using Android.App;
using Android.Graphics;
using Android.Service.Wallpaper;
using Android.Views;

namespace Subsystem;

// WpService — the Subsystem desktop wallpaper ("ss-desktop-wallpaper"). A WallpaperService is a
// privileged, OS-kept-alive foothold (the same class of hook as the accessibility / voice services), so
// we KEEP it. It used to render the shell/shaders catalog on an EGL loop; that was retired 2026-06-19
// (Scott's call): the wallpaper is now a SOLID COLOR or a still IMAGE — no GPU loop, no heat — and the
// shader catalog is moving to a Shadertoy-style in-app tool.
//
// KNOWN BLOCKER (2026-06-19): this preview toolchain (.NET 11 preview.4 / Android SDK 36.99-preview.4) does
// NOT emit the ACW for a nested WallpaperService.Engine — WpService$WpService_WpEngine.java is never
// generated, so onCreateEngine throws ClassNotFoundException and the Android wallpaper PICKER crashes.
// Making the engine top-level makes the ACW generate but javac then rejects it ("an enclosing instance
// that contains WallpaperService.Engine is required" — Engine is a Java non-static inner class). [Register]
// on the nested type crashes the linker (XALNS7002). The engine below is correct and builds; it just can't
// be INSTANTIATED until the toolchain emits the wrapper (or we hand-author the ACW Java stub). The fix is
// out of our hands for now — see Scott's call on whether to temporarily unregister from the picker.
[Service(Name = "dev.mansfieldplumbing.subsystem.WpService",
         Label = "Subsystem Wallpaper",
         Permission = "android.permission.BIND_WALLPAPER",
         Exported = true)]
[IntentFilter(new[] { "android.service.wallpaper.WallpaperService" })]
[MetaData("android.service.wallpaper", Resource = "@xml/wallpaper")]
public sealed class WpService : WallpaperService
{
    // Set-SystemWallpaper bumps this so live engines re-resolve the color/image on their next paint.
    private static int _gen;
    public static void NotifyChanged() => Interlocked.Increment(ref _gen);
    internal static int Generation => Volatile.Read(ref _gen);

    public override Engine OnCreateEngine() => new WpEngine(this);

    // The chosen wallpaper from Cm (\Shell\SystemWallpaper): a solid color and, optionally, a still image
    // layered over it. Cm is the one truth; a missing/malformed record degrades to the neutral solid.
    internal sealed class Choice
    {
        public Color Color = Color.ParseColor("#0e0f12");   // the Dark theme --bg: the neutral floor
        public string? ImagePath;                            // absolute path to a still, when chosen
    }

    internal static Choice ResolveChoice()
    {
        var c = new Choice();
        try
        {
            var pref = Subsystem.Cm.Cm.Get("\\Shell\\SystemWallpaper");
            if (pref?.ManifestJson == null) return c;
            using var doc = JsonDocument.Parse(pref.ManifestJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("color", out var cv) && cv.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(cv.GetString()))
                try { c.Color = Color.ParseColor(cv.GetString()!.Trim()); } catch (Exception ex) { Dg.Warn("wp", ex); /* keep the floor */ }
            if (root.TryGetProperty("image", out var iv) && iv.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(iv.GetString()))
                c.ImagePath = iv.GetString()!.Trim();
        }
        catch (Exception ex) { Dg.Log("wp", "resolve failed: " + ex.Message); }
        return c;
    }

    // A STILL engine: paint once per surface and on visibility/redraw — no render thread, no EGL, no
    // thermal listener. Nested (required: Engine is a Java inner class). A solid color is a DrawColor; an
    // image is decoded + center-cropped once.
    private sealed class WpEngine : Engine
    {
        private readonly WpService _svc;
        private ISurfaceHolder? _holder;
        private int _w = 1, _h = 1;

        public WpEngine(WpService svc) : base(svc) { _svc = svc; }

        public override void OnSurfaceCreated(ISurfaceHolder? holder)
        {
            base.OnSurfaceCreated(holder);
            _holder = holder;
        }

        public override void OnSurfaceChanged(ISurfaceHolder? holder, Format format, int width, int height)
        {
            base.OnSurfaceChanged(holder, format, width, height);
            _holder = holder; _w = Math.Max(1, width); _h = Math.Max(1, height);
            Paint();
        }

        public override void OnVisibilityChanged(bool visible)
        {
            if (visible) Paint();   // re-resolve + repaint when the home screen returns (picks up a change)
        }

        public override void OnSurfaceRedrawNeeded(ISurfaceHolder? holder)
        {
            _holder = holder;
            Paint();
        }

        // Lock -> solid fill (+ center-cropped image if chosen) -> post. Failable end to end: a bad bitmap
        // or a lost surface degrades to the solid fill and never throws into the OS.
        private void Paint()
        {
            var holder = _holder;
            if (holder == null) return;
            Canvas? canvas = null;
            try
            {
                canvas = holder.LockCanvas();
                if (canvas == null) return;
                var choice = ResolveChoice();
                canvas.DrawColor(choice.Color);
                if (choice.ImagePath != null)
                {
                    using var bmp = BitmapFactory.DecodeFile(choice.ImagePath);
                    if (bmp != null) DrawCenterCrop(canvas, bmp);
                }
            }
            catch (Exception ex) { Dg.Log("wp", "paint failed: " + ex.Message); }
            finally { try { if (canvas != null) holder.UnlockCanvasAndPost(canvas); } catch (Exception ex) { Dg.Warn("wp", ex); } }
        }

        // Cover the surface preserving aspect (center-crop), the way the system still wallpaper does.
        private void DrawCenterCrop(Canvas canvas, Bitmap bmp)
        {
            float scale = Math.Max((float)_w / bmp.Width, (float)_h / bmp.Height);
            float dw = bmp.Width * scale, dh = bmp.Height * scale;
            float left = (_w - dw) / 2f, top = (_h - dh) / 2f;
            using var src = new Rect(0, 0, bmp.Width, bmp.Height);
            var dst = new RectF(left, top, left + dw, top + dh);
            using var paint = new Paint { FilterBitmap = true };
            canvas.DrawBitmap(bmp, src, dst, paint);
        }
    }
}
