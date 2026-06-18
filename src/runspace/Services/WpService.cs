using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Android.App;
using Android.Opengl;
using Android.Service.Wallpaper;
using Android.Views;

namespace Subsystem;

// Wp — Gr's Android wallpaper port: the launcher rendered as a presenter over the Shader catalog
// (\Capability\Shader\*). "Wallpaper" is Android's own word, kept at this leaf per the
// surge-protector doctrine; the objects are Shader programs and the renderer is Gr's EGL loop.
// No second truth — the engine reads the SAME GLSL ES 1.00 .frag files from the compiled shell
// store (ObpHost, in-process — never HTTP-to-self), and its selection is a Cm record
// (\Shell\SystemWallpaper), not a private preference.
//
// Render path: a per-engine managed EGL14/GLES20 thread drawing straight into the engine's
// Surface — the surface IS SurfaceFlinger's buffer-queue consumer, so the zero-copy "broadcast
// target" exists by construction (DirectPort discipline satisfied without inventing transport).
// No WebView, no native shims, no managed byte[] across JNI (the one string crossing is the
// shader source, once per compile).
//
// Ladder (per engine, surge-protector style — ground never absent, mirrors Wallpaper.js):
//   1. IDEAL    registry .frag @ 15fps, u_resolution/u_time/u_camera fed (offsets+zoom → camera)
//   2. DEGRADE  shader compile/link failure → the built-in sky (the WGSL sky ported to ES 1.00)
//   3. GROUND   EGL unavailable/lost → one static sky-gradient via lockCanvas, then park
//
// Po doctrine: hidden ⇒ the loop PARKS on a wait handle (zero wakeups), it does not skip draws.
// CoreCLR law: the render thread is cancellation-polled and bounded per frame — never aborted.
// Engines: the system runs SEVERAL at once (home, picker preview, lock screen) — all state is
// per-engine except the playlist cursors and the change-generation, which are process-wide truth.
[Service(Name = "dev.mansfieldplumbing.subsystem.WpService",
         Label = "Subsystem Live Wallpaper",
         Permission = "android.permission.BIND_WALLPAPER",
         Exported = true)]
[IntentFilter(new[] { "android.service.wallpaper.WallpaperService" })]
[MetaData("android.service.wallpaper", Resource = "@xml/wallpaper")]
public sealed class WpService : WallpaperService
{
    // Selection generation — Set-SystemWallpaper bumps it; every live engine notices on its next
    // frame and re-resolves from Cm. An int compare per frame, not a poll of the registry.
    private static int _gen;
    public static void NotifyChanged() => Interlocked.Increment(ref _gen);
    internal static int Generation => Volatile.Read(ref _gen);

    // Playlist cursors (which member is up), per playlist id — process-wide so home + preview
    // agree and an engine rebuild resumes where the playlist left off.
    internal static readonly ConcurrentDictionary<string, int> Cursors = new(StringComparer.OrdinalIgnoreCase);

    public override Engine OnCreateEngine() => new WpEngine(this);

    // ---- catalog resolution (Cm is the one truth; everything failable returns null → ladder) ----

    internal sealed class Selection
    {
        public string PlaylistId = "bliss-xp";
        public List<string> Files = new();
        public int CycleSeconds = 90;
    }

    internal static Selection? ResolveSelection()
    {
        try
        {
            var sel = new Selection();
            var pref = Subsystem.Cm.Cm.Get("\\Shell\\SystemWallpaper");
            if (pref?.ManifestJson != null)
            {
                using var doc = JsonDocument.Parse(pref.ManifestJson);
                if (doc.RootElement.TryGetProperty("playlist", out var plv) && plv.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(plv.GetString()))
                    sel.PlaylistId = plv.GetString()!.Trim();
            }
            var rec = Subsystem.Cm.Cm.Get("\\Capability\\Shader\\" + sel.PlaylistId);
            if (rec?.ManifestJson == null || !rec.Enabled) return null;
            using var man = JsonDocument.Parse(rec.ManifestJson);
            if (man.RootElement.TryGetProperty("cycleSeconds", out var cv) && cv.ValueKind == JsonValueKind.Number)
                sel.CycleSeconds = Math.Max(15, cv.GetInt32());
            if (man.RootElement.TryGetProperty("files", out var fv) && fv.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in fv.EnumerateArray())
                    if (m.TryGetProperty("file", out var ff) && ff.ValueKind == JsonValueKind.String)
                        sel.Files.Add(ff.GetString()!);
            }
            else if (man.RootElement.TryGetProperty("file", out var sf) && sf.ValueKind == JsonValueKind.String)
            {
                sel.Files.Add(sf.GetString()!);
            }
            return sel.Files.Count > 0 ? sel : null;
        }
        catch (Exception ex) { Dg.Log("wp", "resolve failed: " + ex.Message); return null; }
    }

    internal static string? ReadFrag(string file)
    {
        try { return ObpHost.ReadAllText("shell/" + file); }
        catch { return null; }
    }

    // ---- the engine ----

    private sealed class WpEngine : Engine
    {
        // The WGSL boot sky from shell/Wallpaper.js, ported verbatim to GLSL ES 1.00 — the DEGRADE
        // rung, compiled when the catalog shader is unavailable or fails to compile.
        private const string SkyFrag = @"precision mediump float;
uniform vec2 u_resolution; uniform float u_time; uniform vec2 u_camera;
float rnd(vec2 s){ return fract(sin(dot(s, vec2(12.9898,78.233)))*43758.5453123); }
float noise(vec2 s){ vec2 i=floor(s); vec2 f=fract(s);
  float a=rnd(i); float b=rnd(i+vec2(1.,0.)); float c=rnd(i+vec2(0.,1.)); float d=rnd(i+vec2(1.,1.));
  vec2 k=f*f*(3.-2.*f);
  return mix(a,b,k.x)+(c-a)*k.y*(1.-k.x)+(d-b)*k.x*k.y; }
float fbm(vec2 q){ vec2 p=q; float v=0.; float a=.5;
  for(int i=0;i<4;i++){ v+=a*noise(p); p*=2.; a*=.5; } return v; }
void main(){
  vec2 st = gl_FragCoord.xy / u_resolution;
  st += u_camera * 0.00006;
  vec2 a = st; a.x *= u_resolution.x / u_resolution.y;
  vec3 sky = mix(vec3(0.035,0.26,0.65), vec3(0.66,0.83,0.97), st.y);
  float n = fbm((a + vec2(u_time*0.02, 0.0)) * 3.0);
  n = smoothstep(0.4, 0.8, n);
  gl_FragColor = vec4(mix(sky, vec3(1.0), n*0.7), 1.0);
}";
        private const string Vs = "attribute vec2 p; void main(){ gl_Position = vec4(p, 0.0, 1.0); }";
        private const int FrameMs = 1000 / 15;   // Po: the same 15fps cap as the in-app renderer

        private Thread? _thread;
        private CancellationTokenSource? _cts;
        private readonly AutoResetEvent _kick = new(false);
        private readonly AutoResetEvent _frameDone = new(false);
        private volatile bool _visible;
        private volatile bool _redrawPending;
        private volatile int _w = 1, _h = 1;
        private float _camX, _camY;   // offsets/zoom feed; read by the render thread per frame

        public WpEngine(WpService svc) : base(svc) { }

        public override void OnCreate(ISurfaceHolder? surfaceHolder)
        {
            base.OnCreate(surfaceHolder);
            try { SetOffsetNotificationsEnabled(true); } catch { /* launcher may not support offsets */ }
        }

        public override void OnSurfaceChanged(ISurfaceHolder? holder, Android.Graphics.Format format, int width, int height)
        {
            base.OnSurfaceChanged(holder, format, width, height);
            _w = Math.Max(1, width); _h = Math.Max(1, height);
        }

        public override void OnSurfaceCreated(ISurfaceHolder? holder)
        {
            base.OnSurfaceCreated(holder);
            StopRender();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _thread = new Thread(() => RenderLoop(ct)) { IsBackground = true, Name = "wp-render" };
            _thread.Start();
        }

        public override void OnSurfaceDestroyed(ISurfaceHolder? holder)
        {
            // The GL thread must stop producing into this surface BEFORE we return. Cancellation +
            // bounded join (frames are ≤ ~70ms); a wedged thread can't be aborted (CoreCLR law) —
            // it would only touch a dead EGL surface, which the loop catches and grounds on.
            StopRender();
            base.OnSurfaceDestroyed(holder);
        }

        public override void OnDestroy()
        {
            StopRender();
            base.OnDestroy();
        }

        public override void OnVisibilityChanged(bool visible)
        {
            _visible = visible;
            if (visible) _kick.Set();   // hidden needs no signal — the loop parks itself
        }

        public override void OnSurfaceRedrawNeeded(ISurfaceHolder? holder)
        {
            // Unlock/resume composites the wallpaper synchronously — produce one frame now or the
            // user sees stale pixels. Signal the loop and wait briefly for the swap.
            _redrawPending = true;
            _frameDone.Reset();
            _kick.Set();
            try { _frameDone.WaitOne(350); } catch { }
        }

        public override void OnOffsetsChanged(float xOffset, float yOffset, float xOffsetStep, float yOffsetStep, int xPixelOffset, int yPixelOffset)
        {
            // Launcher page scroll → the same parallax camera the boundless Surface feeds in-app.
            // One UI's stock launcher pins this at 0.5 (no scroll) — that's a constant, not a bug.
            _camX = (xOffset - 0.5f) * 4000f;
        }

        public override void OnZoomChanged(float zoom)
        {
            // The launcher zoom-out gesture (drawer/recents) — a depth nudge; reliable on Samsung
            // where offsets are not.
            _camY = zoom * 1500f;
        }

        private void StopRender()
        {
            try
            {
                _cts?.Cancel();
                _kick.Set();
                _thread?.Join(1500);
            }
            catch { }
            finally { _thread = null; _cts?.Dispose(); _cts = null; }
        }

        // ---- the render thread ----

        private void RenderLoop(CancellationToken ct)
        {
            try
            {
                if (!RunEgl(ct) && !ct.IsCancellationRequested)
                {
                    DrawGroundGradient();                       // rung 3: one static frame
                    while (!ct.IsCancellationRequested)         // then park — zero wakeups
                        WaitHandle.WaitAny(new WaitHandle[] { _kick, ct.WaitHandle });
                }
            }
            catch (Exception ex) { Dg.Log("wp", "render loop died: " + ex.Message); }
        }

        // Rungs 1+2. Returns false only when EGL itself is unusable (→ rung 3).
        private bool RunEgl(CancellationToken ct)
        {
            // NOTE: EGL handle wrappers are distinct managed objects over the same native handle —
            // compare with Equals (handle equality), never reference ==.
            var dpy = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
            if (dpy == null || dpy.Equals(EGL14.EglNoDisplay)) return false;
            var ver = new int[2];
            if (!EGL14.EglInitialize(dpy, ver, 0, ver, 1)) return false;

            EGLSurface? surf = null;
            EGLContext? ctx = null;
            int program = 0;
            try
            {
                var cfgAttrs = new[]
                {
                    EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8, EGL14.EglAlphaSize, 8,
                    EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
                    EGL14.EglSurfaceType, EGL14.EglWindowBit,
                    EGL14.EglNone
                };
                var cfgs = new EGLConfig[1];
                var num = new int[1];
                if (!EGL14.EglChooseConfig(dpy, cfgAttrs, 0, cfgs, 0, 1, num, 0) || num[0] < 1) return false;

                ctx = EGL14.EglCreateContext(dpy, cfgs[0], EGL14.EglNoContext,
                    new[] { EGL14.EglContextClientVersion, 2, EGL14.EglNone }, 0);
                if (ctx == null || ctx.Equals(EGL14.EglNoContext)) return false;

                var window = SurfaceHolder?.Surface;
                if (window == null) return false;
                surf = EGL14.EglCreateWindowSurface(dpy, cfgs[0], window, new[] { EGL14.EglNone }, 0);
                if (surf == null || surf.Equals(EGL14.EglNoSurface)) return false;
                if (!EGL14.EglMakeCurrent(dpy, surf, surf, ctx)) return false;

                // fullscreen two-triangle quad (the same geometry as Wallpaper.js)
                var fb = Java.Nio.ByteBuffer.AllocateDirect(12 * sizeof(float))
                    .Order(Java.Nio.ByteOrder.NativeOrder())!.AsFloatBuffer()!;
                fb.Put(new float[] { -1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1 });
                fb.Position(0);

                int uRes = -1, uTime = -1, uCam = -1;
                int gen = -1, cursorSeen = -1;
                Selection? sel = null;
                long visibleMs = 0;

                bool Recompile()
                {
                    if (program != 0) { GLES20.GlDeleteProgram(program); program = 0; }
                    sel = ResolveSelection();
                    string? frag = null;
                    if (sel != null)
                    {
                        var cursor = Cursors.GetOrAdd(sel.PlaylistId, 0);
                        cursorSeen = cursor;
                        frag = ReadFrag(sel.Files[((cursor % sel.Files.Count) + sel.Files.Count) % sel.Files.Count]);
                    }
                    program = BuildProgram(frag);
                    if (program == 0 && frag != null) { Dg.Log("wp", "catalog shader failed — grounding to sky"); program = BuildProgram(null); }
                    if (program == 0) return false;
                    GLES20.GlUseProgram(program);
                    var loc = GLES20.GlGetAttribLocation(program, "p");
                    GLES20.GlEnableVertexAttribArray(loc);
                    GLES20.GlVertexAttribPointer(loc, 2, GLES20.GlFloat, false, 0, fb);
                    uRes = GLES20.GlGetUniformLocation(program, "u_resolution");
                    uTime = GLES20.GlGetUniformLocation(program, "u_time");
                    uCam = GLES20.GlGetUniformLocation(program, "u_camera");
                    gen = Generation;
                    visibleMs = 0;
                    return true;
                }

                if (!Recompile()) return false;
                var epoch = Environment.TickCount64;

                while (!ct.IsCancellationRequested)
                {
                    if (!_visible && !_redrawPending)
                    {
                        WaitHandle.WaitAny(new WaitHandle[] { _kick, ct.WaitHandle });   // parked: zero wakeups
                        continue;
                    }
                    var t0 = Environment.TickCount64;

                    // selection changed (Set-SystemWallpaper) or the playlist clock advanced?
                    if (gen != Generation) { if (!Recompile()) return false; }
                    else if (sel != null && sel.Files.Count > 1 && visibleMs >= sel.CycleSeconds * 1000L)
                    {
                        Cursors[sel.PlaylistId] = (Cursors.GetOrAdd(sel.PlaylistId, 0) + 1) % sel.Files.Count;
                        if (!Recompile()) return false;
                    }
                    else if (sel != null && Cursors.TryGetValue(sel.PlaylistId, out var cNow) && cNow != cursorSeen)
                    {
                        if (!Recompile()) return false;          // another engine advanced the playlist
                    }

                    bool redraw = _redrawPending;
                    _redrawPending = false;

                    GLES20.GlViewport(0, 0, _w, _h);
                    if (uRes >= 0) GLES20.GlUniform2f(uRes, _w, _h);
                    if (uTime >= 0) GLES20.GlUniform1f(uTime, (t0 - epoch) * 0.001f);
                    if (uCam >= 0) GLES20.GlUniform2f(uCam, _camX, _camY);
                    GLES20.GlDrawArrays(GLES20.GlTriangles, 0, 6);
                    if (!EGL14.EglSwapBuffers(dpy, surf))
                    {
                        // context/surface lost (driver restart, surface torn down) — leave quietly;
                        // the system re-creates the surface and OnSurfaceCreated re-enters the ladder.
                        Dg.Log("wp", "eglSwapBuffers failed — exiting render loop");
                        return true;
                    }
                    if (redraw) _frameDone.Set();

                    var elapsed = (int)(Environment.TickCount64 - t0);
                    if (_visible) visibleMs += Math.Max(elapsed, FrameMs);
                    var sleep = FrameMs - elapsed;
                    if (sleep > 2 && !_redrawPending) ct.WaitHandle.WaitOne(sleep);
                }
                return true;
            }
            catch (Exception ex) { Dg.Log("wp", "egl path failed: " + ex.Message); return false; }
            finally
            {
                try
                {
                    if (program != 0) GLES20.GlDeleteProgram(program);
                    EGL14.EglMakeCurrent(dpy, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                    if (surf != null && !surf.Equals(EGL14.EglNoSurface)) EGL14.EglDestroySurface(dpy, surf);
                    if (ctx != null && !ctx.Equals(EGL14.EglNoContext)) EGL14.EglDestroyContext(dpy, ctx);
                    EGL14.EglTerminate(dpy);
                }
                catch { }
            }
        }

        // null frag = the built-in sky. 0 = failure (caller decides the next rung).
        private static int BuildProgram(string? frag)
        {
            int Compile(int type, string src)
            {
                var s = GLES20.GlCreateShader(type);
                GLES20.GlShaderSource(s, src);
                GLES20.GlCompileShader(s);
                var ok = new int[1];
                GLES20.GlGetShaderiv(s, GLES20.GlCompileStatus, ok, 0);
                if (ok[0] == 0)
                {
                    Dg.Log("wp", "shader compile: " + GLES20.GlGetShaderInfoLog(s));
                    GLES20.GlDeleteShader(s);
                    return 0;
                }
                return s;
            }
            var v = Compile(GLES20.GlVertexShader, Vs);
            if (v == 0) return 0;
            var f = Compile(GLES20.GlFragmentShader, frag ?? SkyFrag);
            if (f == 0) { GLES20.GlDeleteShader(v); return 0; }
            var prog = GLES20.GlCreateProgram();
            GLES20.GlAttachShader(prog, v);
            GLES20.GlAttachShader(prog, f);
            GLES20.GlLinkProgram(prog);
            GLES20.GlDeleteShader(v);
            GLES20.GlDeleteShader(f);
            var linked = new int[1];
            GLES20.GlGetProgramiv(prog, GLES20.GlLinkStatus, linked, 0);
            if (linked[0] == 0)
            {
                Dg.Log("wp", "program link: " + GLES20.GlGetProgramInfoLog(prog));
                GLES20.GlDeleteProgram(prog);
                return 0;
            }
            return prog;
        }

        // Rung 3 — the FIRST_PAINT analog: one software frame mirroring the sky's vertical stops,
        // so a GPU-less failure mode is a calm gradient, never a black launcher.
        private void DrawGroundGradient()
        {
            try
            {
                var holder = SurfaceHolder;
                var canvas = holder?.LockCanvas();
                if (canvas == null) return;
                try
                {
                    using var paint = new Android.Graphics.Paint();
                    using var shader = new Android.Graphics.LinearGradient(
                        0, 0, 0, canvas.Height,
                        new Android.Graphics.Color(168, 212, 247),
                        new Android.Graphics.Color(9, 67, 166),
                        Android.Graphics.Shader.TileMode.Clamp!);
                    paint.SetShader(shader);
                    canvas.DrawRect(0, 0, canvas.Width, canvas.Height, paint);
                }
                finally { holder!.UnlockCanvasAndPost(canvas); }
            }
            catch (Exception ex) { Dg.Log("wp", "ground gradient failed: " + ex.Message); }
        }
    }
}
