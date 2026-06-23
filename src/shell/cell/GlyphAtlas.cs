using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Subsystem.Shell.Cell;

// GlyphAtlas — the SHARED, pure-System.* MSDF cell rasterizer. It turns a CellBuffer (the head-agnostic
// frame the compositor produces) into a flat BGRA byte[] the per-head GPU presenter pushes through its
// DirectPort region (D3D12 shared texture on Windows / VkImage on Android). One rasterizer, both heads —
// only the upload+fence differs per head (ICellPresenter), never the glyph math.
//
// Home-rolled, never imported (CONTRACT): the PNG decoder is ~one screen of zlib-inflate + un-filter built on
// System.IO.Compression.ZLibStream — no System.Drawing (absent on the Android head), no SkiaSharp, no NuGet.
// The atlas is msdf-atlas-gen output: 8-bit RGB (3-channel) MSDF, yOrigin=bottom, glyphs keyed by `unicode`.
// We sample median(r,g,b) and threshold across the screen-px range — the canonical MSDF reconstruction — so a
// glyph stays crisp at any cell size from ONE texture (the whole point of MSDF over a 1-bit mask).
//
// Atlas/metrics provenance: C:\tui-dwm\glyphs\cascadia-code-{atlas.png,metrics.json} (Scott's, do NOT
// regenerate). NOTE: the *universal* atlas is keyed by glyph `index` (a font glyph id needing a cmap we don't
// carry), so it is unusable without the font's char->glyph table; the *code* atlas is `unicode`-keyed and is
// what AtlasMetrics (the tui-dwm recipe) consumes. Code wins over the "universal" filename — the binding is to
// what actually maps a char to a quad.
public sealed class GlyphAtlas
{
    // One decoded atlas page: RGB pixels, top-down, row-major.
    private readonly byte[] _rgb;   // 3 bytes/px, length = AtlasW*AtlasH*3
    public int AtlasW { get; }
    public int AtlasH { get; }

    public float DistanceRange { get; }   // SDF range in atlas texels (msdf-atlas-gen "distanceRange")
    public float EmPxInAtlas { get; }     // atlas "size": px per em in the atlas (sets the screen-px range)
    public float LineHeight { get; }      // em
    public float Ascender { get; }        // em above baseline to the cell top

    // Per-codepoint glyph quad. Indexed by codepoint (0..0xFFFF); null = no glyph (render background only).
    private readonly Glyph?[] _glyphs = new Glyph?[0x10000];

    private readonly struct Glyph
    {
        // atlasBounds in atlas px, yOrigin=bottom (as emitted). planeBounds in em, relative to the pen
        // origin (x) and the baseline (y, up-positive).
        public readonly float AL, AB, AR, AT;   // atlas left/bottom/right/top (px, bottom-up)
        public readonly float PL, PB, PR, PT;   // plane left/bottom/right/top (em)
        public Glyph(float al, float ab, float ar, float at, float pl, float pb, float pr, float pt)
        { AL = al; AB = ab; AR = ar; AT = at; PL = pl; PB = pb; PR = pr; PT = pt; }
    }

    private GlyphAtlas(byte[] rgb, int w, int h, float distRange, float emPx, float lineH, float asc)
    {
        _rgb = rgb; AtlasW = w; AtlasH = h;
        DistanceRange = distRange; EmPxInAtlas = emPx; LineHeight = lineH; Ascender = asc;
    }

    // Resolve the atlas pair, preferring an explicit override, else the tui-dwm glyphs dir. Path is RESOLVED,
    // never a hardcoded literal baked into the call site (CONTRACT: no hardcoded paths). On Android the head
    // passes an extracted-asset path; the default is the dev-box reuse location the task pins.
    public static string DefaultGlyphsDir =>
        Environment.GetEnvironmentVariable("SS_GLYPHS_DIR") is { Length: > 0 } d ? d
        : Path.Combine("C:", "tui-dwm", "glyphs");

    // Load <name>-atlas.png + <name>-metrics.json from a resolved directory. Throws on a missing/garbled pair
    // (the presenter catches and degrades to the VT floor — graceful degradation, never a half-rendered GPU).
    public static GlyphAtlas Load(string? glyphsDir = null, string name = "cascadia-code")
    {
        glyphsDir ??= DefaultGlyphsDir;
        string png = Path.Combine(glyphsDir, name + "-atlas.png");
        string json = Path.Combine(glyphsDir, name + "-metrics.json");
        var (rgb, w, h) = DecodePngRgb(File.ReadAllBytes(png));

        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var root = doc.RootElement;
        var atlas = root.GetProperty("atlas");
        float distRange = atlas.GetProperty("distanceRange").GetSingle();
        float emPx = atlas.GetProperty("size").GetSingle();
        var m = root.GetProperty("metrics");
        float lineH = m.GetProperty("lineHeight").GetSingle();
        float asc = m.GetProperty("ascender").GetSingle();

        var ga = new GlyphAtlas(rgb, w, h, distRange, emPx, lineH, asc);
        foreach (var g in root.GetProperty("glyphs").EnumerateArray())
        {
            if (!g.TryGetProperty("unicode", out var uEl)) continue;
            int cp = uEl.GetInt32();
            if (cp < 0 || cp >= 0x10000) continue;
            // Space and other whitespace carry no atlasBounds — leave null (background only).
            if (!g.TryGetProperty("atlasBounds", out var ab) || !g.TryGetProperty("planeBounds", out var pb))
                continue;
            ga._glyphs[cp] = new Glyph(
                ab.GetProperty("left").GetSingle(),  ab.GetProperty("bottom").GetSingle(),
                ab.GetProperty("right").GetSingle(), ab.GetProperty("top").GetSingle(),
                pb.GetProperty("left").GetSingle(),  pb.GetProperty("bottom").GetSingle(),
                pb.GetProperty("right").GetSingle(), pb.GetProperty("top").GetSingle());
        }
        return ga;
    }

    public bool Has(char c) => _glyphs[c] is not null;

    // Rasterize `buf` into a fresh BGRA frame of (Width*cellW) x (Height*cellH), top-down, row-major,
    // 4 bytes/px (B,G,R,A). This is the producer payload — the bytes the GPU presenter copies into its
    // shared region. role->color is applied HERE at composite: cell.Fg/Bg are xterm-256 indices.
    public byte[] RasterizeBgra(CellBuffer buf, int cellW, int cellH, out int outW, out int outH)
    {
        outW = Math.Max(1, buf.Width * cellW);
        outH = Math.Max(1, buf.Height * cellH);
        var px = new byte[outW * outH * 4];

        float emPx = cellH / LineHeight;                 // px per em at this cell size
        float baseline = Ascender * emPx;                // px from cell top down to the baseline
        // Canonical MSDF screen-px range: the SDF's atlas-texel range scaled into output px.
        float screenPxRange = Math.Max(1f, DistanceRange * (emPx / EmPxInAtlas));

        for (int cy = 0; cy < buf.Height; cy++)
        for (int cx = 0; cx < buf.Width; cx++)
        {
            ref Cell cell = ref buf.At(cx, cy);
            (byte fr, byte fg, byte fb) = Xterm256.Rgb(cell.Fg);
            (byte br, byte bg, byte bb) = Xterm256.Rgb(cell.Bg);

            int px0 = cx * cellW, py0 = cy * cellH;
            // Fill the cell background first.
            for (int y = 0; y < cellH; y++)
            {
                int row = (py0 + y) * outW + px0;
                for (int x = 0; x < cellW; x++)
                {
                    int o = (row + x) * 4;
                    px[o] = bb; px[o + 1] = bg; px[o + 2] = br; px[o + 3] = 255;
                }
            }

            var gOpt = _glyphs[cell.Rune];
            if (gOpt is not Glyph gl) continue;   // whitespace / unmapped -> background only

            // Glyph quad in cell-local px from planeBounds (em, baseline-relative, y-up).
            float gLeft   = gl.PL * emPx;
            float gRight  = gl.PR * emPx;
            float gTop    = baseline - gl.PT * emPx;   // top edge (smaller y)
            float gBottom = baseline - gl.PB * emPx;   // bottom edge (larger y)
            float gW = gRight - gLeft, gH = gBottom - gTop;
            if (gW <= 0 || gH <= 0) continue;

            int x0 = Math.Max(0, (int)MathF.Floor(gLeft));
            int x1 = Math.Min(cellW - 1, (int)MathF.Ceiling(gRight));
            int y0 = Math.Max(0, (int)MathF.Floor(gTop));
            int y1 = Math.Min(cellH - 1, (int)MathF.Ceiling(gBottom));

            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float fx = (x + 0.5f - gLeft) / gW;             // 0..1 across the glyph, left->right
                float fyTop = (y + 0.5f - gTop) / gH;           // 0..1 down the glyph, top->bottom
                if (fx < 0 || fx > 1 || fyTop < 0 || fyTop > 1) continue;

                float au = gl.AL + fx * (gl.AR - gl.AL);                      // atlas px, x
                // atlas yOrigin=bottom: convert the bottom-up rect to a top-down sample row.
                float rowTop = AtlasH - gl.AT, rowBot = AtlasH - gl.AB;
                float av = rowTop + fyTop * (rowBot - rowTop);               // atlas px, top-down row

                float med = SampleMedian(au, av);
                float cov = Math.Clamp((med - 0.5f) * screenPxRange + 0.5f, 0f, 1f);
                if (cov <= 0f) continue;

                int o = ((py0 + y) * outW + (px0 + x)) * 4;
                px[o]     = (byte)(bb + (fb - bb) * cov);
                px[o + 1] = (byte)(bg + (fg - bg) * cov);
                px[o + 2] = (byte)(br + (fr - br) * cov);
                px[o + 3] = 255;
            }
        }
        return px;
    }

    // Bilinear median(r,g,b) of the MSDF atlas at (u,v) atlas-px, normalized 0..1.
    private float SampleMedian(float u, float v)
    {
        u = Math.Clamp(u - 0.5f, 0, AtlasW - 1);
        v = Math.Clamp(v - 0.5f, 0, AtlasH - 1);
        int x0 = (int)u, y0 = (int)v;
        int x1 = Math.Min(x0 + 1, AtlasW - 1), y1 = Math.Min(y0 + 1, AtlasH - 1);
        float tx = u - x0, ty = v - y0;
        float m00 = Med(x0, y0), m10 = Med(x1, y0), m01 = Med(x0, y1), m11 = Med(x1, y1);
        float top = m00 + (m10 - m00) * tx, bot = m01 + (m11 - m01) * tx;
        return top + (bot - top) * ty;
    }

    private float Med(int x, int y)
    {
        int i = (y * AtlasW + x) * 3;
        float r = _rgb[i] / 255f, g = _rgb[i + 1] / 255f, b = _rgb[i + 2] / 255f;
        return MathF.Max(MathF.Min(r, g), MathF.Min(MathF.Max(r, g), b));   // median of 3
    }

    // ── Home-rolled PNG decode (8-bit RGB, colortype 2) ─────────────────────────────────────────────
    // Just enough to read an msdf-atlas-gen page: parse chunks, zlib-inflate the concatenated IDAT, reverse
    // the per-scanline filters. No interlace (msdf-atlas-gen never writes Adam7). Throws on anything else so
    // the caller degrades rather than rendering garbage.
    private static (byte[] rgb, int w, int h) DecodePngRgb(byte[] file)
    {
        ReadOnlySpan<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(sig))
            throw new InvalidDataException("not a PNG.");

        int w = 0, h = 0, bitDepth = 0, colorType = 0;
        var idat = new MemoryStream();
        int p = 8;
        while (p + 8 <= file.Length)
        {
            int len = BE32(file, p); p += 4;
            string type = System.Text.Encoding.ASCII.GetString(file, p, 4); p += 4;
            int dataAt = p; p += len;
            int crc = p; p += 4;   // CRC ignored
            if (crc > file.Length) throw new InvalidDataException("truncated PNG chunk.");
            switch (type)
            {
                case "IHDR":
                    w = BE32(file, dataAt); h = BE32(file, dataAt + 4);
                    bitDepth = file[dataAt + 8]; colorType = file[dataAt + 9];
                    if (file[dataAt + 12] != 0) throw new NotSupportedException("interlaced PNG unsupported.");
                    break;
                case "IDAT": idat.Write(file, dataAt, len); break;
                case "IEND": p = file.Length; break;
            }
        }
        if (bitDepth != 8 || colorType != 2) throw new NotSupportedException($"PNG must be 8-bit RGB (got depth {bitDepth}, type {colorType}).");

        idat.Position = 0;
        using var zs = new ZLibStream(idat, CompressionMode.Decompress);
        const int bpp = 3;
        int stride = w * bpp;
        var raw = new byte[(stride + 1) * h];   // filtered: each row prefixed by a filter byte
        ReadExact(zs, raw);

        var rgb = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int ft = raw[y * (stride + 1)];
            int rin = y * (stride + 1) + 1;
            int rout = y * stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? rgb[rout + x - bpp] : 0;          // left
                int b = y > 0 ? rgb[rout - stride + x] : 0;          // up
                int c = (x >= bpp && y > 0) ? rgb[rout - stride + x - bpp] : 0;  // up-left
                int val = raw[rin + x];
                int recon = ft switch
                {
                    0 => val,                          // None
                    1 => val + a,                      // Sub
                    2 => val + b,                      // Up
                    3 => val + ((a + b) >> 1),         // Average
                    4 => val + Paeth(a, b, c),         // Paeth
                    _ => throw new InvalidDataException($"bad PNG filter {ft}."),
                };
                rgb[rout + x] = (byte)(recon & 0xFF);
            }
        }
        return (rgb, w, h);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int BE32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

    private static void ReadExact(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n == 0) throw new EndOfStreamException("PNG IDAT shorter than IHDR implies.");
            total += n;
        }
    }
}

// xterm-256 -> 24-bit RGB. The standard cube: 0-15 system palette, 16-231 the 6x6x6 color cube, 232-255 the
// 24-step grayscale ramp. Cell.Bg==0 is "terminal default" on the VT floor; on the GPU floor there is no
// terminal behind us, so 0 resolves to the dark desktop (palette[0]) — a concrete pixel, not transparency.
internal static class Xterm256
{
    private static readonly uint[] System16 =
    {
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    };

    public static (byte r, byte g, byte b) Rgb(byte idx)
    {
        if (idx < 16) { uint v = System16[idx]; return ((byte)(v >> 16), (byte)(v >> 8), (byte)v); }
        if (idx < 232)
        {
            int n = idx - 16;
            int r = n / 36, g = (n / 6) % 6, b = n % 6;
            return (Step(r), Step(g), Step(b));
        }
        byte gray = (byte)(8 + (idx - 232) * 10);
        return (gray, gray, gray);
    }

    private static byte Step(int c) => (byte)(c == 0 ? 0 : 55 + c * 40);
}
