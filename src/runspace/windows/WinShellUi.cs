using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Subsystem.Windows;

// WinShellUi — the double-click face of the Windows head: a WinForms window hosting a WebView2. The page
// is a DirectPort CONSUMER (WebViewDirectPortAdapter) — it receives a shared Float32 region + a fence and
// renders it, no loopback socket. This slice proves the producer/consumer handoff end to end with an
// animated region driven from the native side; the real shell + its lanes layer onto the same channel.
// WinForms runs WITHOUT its ApplicationConfiguration source generator (the ouroboros runs none): visual
// styles are set by hand and the form is built in code. The message loop runs on a dedicated STA thread.
internal static class WinShellUi
{
    private const int RegionFloats = 256;   // the slice's region: 256 Float32 lanes (256-aligned by construction)

    public static int Run(string[] args)
    {
        int rc = 0;
        var ui = new System.Threading.Thread(() => rc = RunUi(args));
        ui.SetApartmentState(System.Threading.ApartmentState.STA);
        ui.Start();
        ui.Join();
        return rc;
    }

    private static int RunUi(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new ShellForm
        {
            Text = "Subsystem",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new System.Drawing.Size(420, 860),   // portrait, phone-sized; F11 = fullscreen
            MinimumSize = new System.Drawing.Size(300, 480),
        };
        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        WebViewDirectPortAdapter? adapter = null;
        var pump = new System.Windows.Forms.Timer { Interval = 33 };   // ~30 fps producer tick (the fence rate)
        var frame = new float[RegionFloats];
        int phase = 0;

        form.Shown += async (_, _) =>
        {
            try
            {
                var dataDir = Path.Combine(Subsystem.MainActivity.Instance!.FilesDir.AbsolutePath, "webview2");
                Directory.CreateDirectory(dataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await web.EnsureCoreWebView2Async(env);

                // Inject the JS consumer endpoint before the page loads, then load the slice's render page.
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebViewDirectPortAdapter.ConsumerScript);
                web.CoreWebView2.NavigateToString(ConsumerPage);

                web.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    adapter = new WebViewDirectPortAdapter(web, "ui-state", RegionFloats);
                    adapter.Attach();        // hand the shared region to the page once
                    pump.Start();            // begin producing frames into it
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 init failed: " + ex.Message, "Subsystem", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        // The producer: write an animated Float32 region (a travelling wave) + signal the fence. The page
        // reads the SAME shared bytes back and renders — native→WebView over a region, zero socket.
        pump.Tick += (_, _) =>
        {
            phase++;
            for (int i = 0; i < frame.Length; i++)
                frame[i] = 0.5f + 0.5f * (float)Math.Sin((i * 0.08) + (phase * 0.15));
            adapter?.SignalFrame(frame);
        };

        form.FormClosed += (_, _) => { pump.Stop(); adapter?.Dispose(); };

        Application.Run(form);
        return 0;
    }

    // The slice's render page: a canvas that draws the shared Float32 region on each fence tick. Proves the
    // handoff is live (the wave moves) with the data crossing only through the shared region + the doorbell.
    private const string ConsumerPage = @"<!doctype html><html><head><meta charset='utf-8'><style>
html,body{margin:0;height:100%;background:#0b0f1a;color:#9fb3c8;font:13px system-ui;overflow:hidden}
#c{width:100%;height:100%;display:block}#hud{position:fixed;top:8px;left:10px;opacity:.7}
</style></head><body><canvas id='c'></canvas><div id='hud'>WebView DirectPort adapter — waiting for region…</div>
<script>
const cv=document.getElementById('c'),hud=document.getElementById('hud'),g=cv.getContext('2d');
function fit(){cv.width=cv.clientWidth*devicePixelRatio;cv.height=cv.clientHeight*devicePixelRatio;}
addEventListener('resize',fit);fit();
addEventListener('ssframe',ev=>{
  const r=ev.detail.region,f=ev.detail.frame,n=r.length;
  g.clearRect(0,0,cv.width,cv.height);
  g.beginPath();
  for(let i=0;i<n;i++){const x=i/(n-1)*cv.width,y=(1-r[i])*cv.height;i?g.lineTo(x,y):g.moveTo(x,y);}
  g.strokeStyle='#4ea1ff';g.lineWidth=2*devicePixelRatio;g.stroke();
  hud.textContent='WebView DirectPort adapter — region \''+(ev.detail.manifest.region)+'\' ('+n+' Float32) · frame '+f+' · zero socket';
});
</script></body></html>";

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    // The shell window. WebView2 consumes ordinary key events, but forwards UNHANDLED accelerator keys (F11
    // has no browser action) to the parent, where ProcessCmdKey catches them. F11 = true fullscreen (over the
    // taskbar, unlike plain Maximize); Esc leaves it.
    private sealed class ShellForm : Form
    {
        private bool _full;
        private FormBorderStyle _savedBorder;
        private FormWindowState _savedState;
        private System.Drawing.Rectangle _savedBounds;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F11) { ToggleFullScreen(); return true; }
            if (keyData == Keys.Escape && _full) { ToggleFullScreen(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ToggleFullScreen()
        {
            if (!_full)
            {
                _savedBorder = FormBorderStyle; _savedState = WindowState; _savedBounds = Bounds;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                _full = true;
            }
            else
            {
                FormBorderStyle = _savedBorder; Bounds = _savedBounds; WindowState = _savedState;
                _full = false;
            }
        }
    }
}
