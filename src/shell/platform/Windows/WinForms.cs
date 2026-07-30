using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Subsystem;
using WV2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Subsystem.Windows;

// WinForms — opens a WinForms/WebView2 window at the given URL.
// Intercepts http://shell/* and serves from ObpHost (embedded assembly resources).
// Nothing else. Open what you're told.
internal static class WinForms
{
    private static string AppName =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Name ?? "app";

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "webview2");

    public static int Open(string url, string[] args)
    {
        if (Interactive.IsDoubleClick()) Interactive.HideOwnConsole();
        int rc = 0;
        var thread = new System.Threading.Thread(() => rc = RunUi(url));
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        return rc;
    }

    private static int RunUi(string url)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new ShellForm
        {
            Text = AppName,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new System.Drawing.Size(420, 860),
            MinimumSize = new System.Drawing.Size(300, 480),
        };
        var webView = new WV2Control { Dock = DockStyle.Fill };
        form.Controls.Add(webView);

        form.Shown += async (_, _) =>
        {
            try
            {
                EnsureNativeWebView2Loader();
                Directory.CreateDirectory(DataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.Settings.IsScriptEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
                webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

                // Inject DirectPort consumer script (shared ArrayBuffer + ssframe event dispatcher)
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebViewDirectPortAdapter.ConsumerScript);

                // Stand up DirectPort zero-socket shared memory region
                var dpAdapter = new WebViewDirectPortAdapter(webView, "default", 1024);
                dpAdapter.Attach();

                form.FormClosed += (_, _) =>
                {
                    dpAdapter.Dispose();
                };

                webView.CoreWebView2.WindowCloseRequested += (_, _) =>
                {
                    form.Close();
                };

                // Serve http://shell/* from embedded assembly resources via ObpHost.
                webView.CoreWebView2.AddWebResourceRequestedFilter("http" + "://shell/*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += (_, e) =>
                {
                    var uri = new Uri(e.Request.Uri);
                    string path = uri.AbsolutePath == "/" ? "/shell.obp" : uri.AbsolutePath;

                    if (path.StartsWith("/psrp", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var reqStream = e.Request.Content;
                            var resStream = Subsystem.Host.PsrpSeam.Execute(path, reqStream);
                            e.Response = env.CreateWebResourceResponse(resStream, 200, "OK", "Content-Type: application/json");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Subsystem.Dg.Warn("winforms", $"psrp error: {ex.Message}");
                        }
                    }

                    try
                    {
                        var stream = ObpHost.OpenRead("shell" + path);
                        if (stream != null)
                        {
                            e.Response = env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {MimeFor(path)}\r\nCache-Control: max-age=31536000, immutable");
                            return;
                        }
                    }
                    catch { }
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(), 404, "Not Found", "Content-Type: text/plain");
                };

                webView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 init failed: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        Application.Run(form);
        return 0;
    }

    private static string MimeFor(string path)
    {
        if (path.EndsWith(".html") || path.EndsWith(".obp")) return "text/html";
        if (path.EndsWith(".js"))   return "application/javascript";
        if (path.EndsWith(".css"))  return "text/css";
        if (path.EndsWith(".png"))  return "image/png";
        if (path.EndsWith(".svg"))  return "image/svg+xml";
        if (path.EndsWith(".json")) return "application/json";
        return "application/octet-stream";
    }

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

    private static void EnsureNativeWebView2Loader()
    {
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "WebView2Loader.dll")))
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(dir);
                return;
            }

            var asm = typeof(WinForms).Assembly;
            var resourceName = System.Linq.Enumerable.FirstOrDefault(
                asm.GetManifestResourceNames(),
                n => n.EndsWith("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "subsystem_wv2");
                    Directory.CreateDirectory(tempDir);
                    var extractedPath = Path.Combine(tempDir, "WebView2Loader.dll");
                    if (!File.Exists(extractedPath) || new FileInfo(extractedPath).Length != stream.Length)
                    {
                        using var fs = File.Create(extractedPath);
                        stream.CopyTo(fs);
                    }
                    CoreWebView2Environment.SetLoaderDllFolderPath(tempDir);
                }
            }
        }
        catch (Exception ex)
        {
            Dg.Warn("winforms", ex);
        }
    }
}
