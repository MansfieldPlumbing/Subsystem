// shader-bg.js — the shell's IN-WEBVIEW animated shader backdrop.
//
// This is NOT the live wallpaper. It renders inside the shell WebView (the .obp host), on a <canvas> at
// the backdrop layer — it never touches the native WallpaperService (WpService / Gr's Wp port). It runs
// the shell/shaders/*.frag catalog (GLSL ES 1.00, the same u_resolution / u_time / u_camera convention the
// native port uses), choosing which shader by the active \Capability\Shader playlist (Get-SystemWallpaper),
// rendering NOTHING when the registry is silent — a shader is OPT-IN (the owner names a \Capability\Shader
// playlist); the default is no shader, so an idle shell paints the static Mica backdrop and burns no GPU.
//
// Discipline:
//   - Ladder: WebGL2 → WebGL1 → solid. If a context or the shader fails, the canvas is removed and the
//     static .shell-backdrop gradient underneath shows through. The backdrop never blocks the shell.
//   - Battery: the rAF loop PAUSES when document.hidden — no GPU burn while the shell is backgrounded.
//   - DPR is capped (these fbm/noise shaders are fill-rate heavy at native retina density).

const VERT = 'attribute vec2 p; void main(){ gl_Position = vec4(p, 0.0, 1.0); }';
const DEFAULT_PLAYLIST = null;       // OFF by default — no shader unless the owner opts in via \Capability\Shader (Get-SystemWallpaper); silence → static Mica backdrop, no rAF loop, no heat

export const ShaderBg = {
  _raf: 0, _canvas: null, _onVis: null, _onResize: null, _start: 0,

  // Mount onto `host` (the .shell-backdrop element). Returns true if a shader is live, false if it fell
  // back to solid. Never throws — a backdrop must not be able to break boot.
  async mount(host, opts) {
    try {
      opts = opts || {};
      const c = document.createElement('canvas');
      c.className = 'shell-shader-bg';
      c.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;display:block;';
      host.appendChild(c);
      this._canvas = c;

      const gl = c.getContext('webgl2') || c.getContext('webgl');
      if (!gl) { c.remove(); this._canvas = null; return false; }      // → solid gradient

      const src = await this._pickFrag(opts.activePlaylist);
      if (!src) { c.remove(); this._canvas = null; return false; }

      const prog = this._program(gl, VERT, src);
      if (!prog) { c.remove(); this._canvas = null; return false; }    // compile/link reported
      gl.useProgram(prog);

      // one oversized triangle covers the viewport (cheaper than a quad)
      const buf = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
      const aPos = gl.getAttribLocation(prog, 'p');
      gl.enableVertexAttribArray(aPos);
      gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);

      const uRes = gl.getUniformLocation(prog, 'u_resolution');
      const uTime = gl.getUniformLocation(prog, 'u_time');
      const uCam = gl.getUniformLocation(prog, 'u_camera');   // null if the shader omits it — harmless

      const resize = () => {
        const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
        const w = Math.max(1, Math.floor(c.clientWidth * dpr));
        const h = Math.max(1, Math.floor(c.clientHeight * dpr));
        if (c.width !== w || c.height !== h) { c.width = w; c.height = h; gl.viewport(0, 0, w, h); }
      };
      this._onResize = resize;
      window.addEventListener('resize', resize);

      this._start = performance.now();
      const frame = (now) => {
        resize();
        if (uRes) gl.uniform2f(uRes, c.width, c.height);
        if (uTime) gl.uniform1f(uTime, (now - this._start) / 1000);
        if (uCam) gl.uniform2f(uCam, 0, 0);          // in-app: no launcher scroll parallax
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        this._raf = requestAnimationFrame(frame);
      };
      const run = () => { if (!this._raf) this._raf = requestAnimationFrame(frame); };
      const stop = () => { if (this._raf) { cancelAnimationFrame(this._raf); this._raf = 0; } };
      this._onVis = () => { if (document.hidden) stop(); else run(); };
      document.addEventListener('visibilitychange', this._onVis);
      if (!document.hidden) run();
      return true;
    } catch (e) {
      console.warn('[shaderbg] mount failed — falling back to the static backdrop:', e);
      if (this._canvas) { this._canvas.remove(); this._canvas = null; }
      return false;
    }
  },

  // Playlist source order: explicit override → the registry (Get-SystemWallpaper) → the default. Then pick
  // a .frag from that playlist out of the shell/shaders manifest. Returns the GLSL source text, or null.
  async _pickFrag(activePlaylist) {
    let pl = activePlaylist;
    if (!pl) {
      try {
        const er = await fetch('/api/exec', { method: 'POST', headers: { 'Content-Type': 'text/plain', 'X-Subsystem-Cap': (window.AndroidBridge?.getCap?.()||window.__ssCap||'') }, body: 'Get-SystemWallpaper' });
        if (er.ok) { const j = await er.json(); if (j && j.Playlist) pl = j.Playlist; }
      } catch (_) { /* offline/preview — fall to the default playlist */ }
    }
    pl = pl || DEFAULT_PLAYLIST;
    if (!pl) return null;          // OFF by default: no explicit playlist → no shader; mount() falls back to the static Mica backdrop (no GPU loop)

    const mr = await fetch('/shaders/manifest.json', { cache: 'no-store' });
    if (!mr.ok) return null;
    const all = await mr.json();
    if (!Array.isArray(all) || !all.length) return null;

    let pool = all.filter(s => s && s.playlist === pl && s.file);
    if (!pool.length) pool = all.filter(s => s && s.file);          // unknown playlist → whole catalog
    if (!pool.length) return null;
    const pick = pool[Math.floor(Math.random() * pool.length)];

    const fr = await fetch('/shaders/' + pick.file, { cache: 'no-store' });
    if (!fr.ok) return null;
    const txt = await fr.text();
    return txt && txt.indexOf('gl_FragColor') !== -1 ? txt : null;   // sanity: a real GLSL-ES fragment
  },

  _program(gl, vs, fs) {
    const compile = (type, src, label) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
        console.warn('[shaderbg] ' + label + ' compile:', gl.getShaderInfoLog(s));
        gl.deleteShader(s);
        return null;
      }
      return s;
    };
    const v = compile(gl.VERTEX_SHADER, vs, 'vertex');
    const f = v && compile(gl.FRAGMENT_SHADER, fs, 'fragment');
    if (!v || !f) return null;
    const p = gl.createProgram();
    gl.attachShader(p, v); gl.attachShader(p, f); gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
      console.warn('[shaderbg] link:', gl.getProgramInfoLog(p));
      return null;
    }
    return p;
  },

  unmount() {
    if (this._raf) cancelAnimationFrame(this._raf);
    if (this._onVis) document.removeEventListener('visibilitychange', this._onVis);
    if (this._onResize) window.removeEventListener('resize', this._onResize);
    if (this._canvas) this._canvas.remove();
    this._raf = 0; this._canvas = null; this._onVis = null; this._onResize = null;
  }
};
