// agent-browser — a WARM, MCP-driven WebView2 browse agent (renamed from the slopped-up askgoogle).
//
// ONE persistent WebView2 session stays up; a harness (ss / Claude / a model) drives it over MCP JSON-RPC on
// stdin/stdout — browse_goto / browse_click / browse_type / browse_back / browse_hud — and each call returns
// the SETTLED numbered-element HUD (+ a tiny vibe-thumbnail). No detaching, no file-tailing, no reattach.
//
// Why this shape:
//  - CONSOLE subsystem (csproj OutputType=Exe), not WinExe: a GUI-subsystem process has no usable stdout, so
//    the old build could never hand its HUD back to a harness. A console app still hosts the WinForms widget.
//  - WinForms, not WPF: Form.Opacity is a layered-window alpha that fades the WHOLE window including the
//    WebView2 child HWND; WPF can't (the WebView2 is an HwndHost DWM-composites separately — "airspace").
//  - Flow-settle the firefox.ps1 way (reliable, no dumb timeout): settle on network/IO quiescence — CDP
//    in-flight request count == 0 AND the msedgewebview2 process tree's IO bytes quiescent — held for an idle
//    window. A wall-clock cap is only a safety net, never the primary trigger.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentBrowser
{
    // Drag-anywhere AND resize-anywhere on a borderless translucent form: the interior is the title bar
    // (HTCAPTION), the outer ~7px rim is the resize grip. A tiny vibe thumbnail you can shove aside.
    // Display-only pane. TOP band = drag/move the window (NOT resize). BOTTOM band = a "window blind" you pull
    // DOWN to EXTEND height (a back-and-forth convo is long, and the web is portrait). No other resize.
    sealed class PipForm : Form
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1, HTCAPTION = 2, HTBOTTOM = 15;
        public int TopBand = 16, BottomBand = 14;
        public bool NativeChrome;   // user mode: real Win11 title bar, no custom bands

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (NativeChrome) return;
            if (m.Msg != WM_NCHITTEST || (int)m.Result != HTCLIENT) return;
            int lp = m.LParam.ToInt32();
            var p = PointToClient(new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)(lp >> 16))));
            if (p.Y >= ClientSize.Height - BottomBand) m.Result = (IntPtr)HTBOTTOM;   // bottom blind -> extend down
            else if (p.Y <= TopBand) m.Result = (IntPtr)HTCAPTION;                    // top bar -> drag/move
            // the web area stays HTCLIENT (and is non-interactive via pointer-events:none)
        }
    }

    // One per-page filter section (ported from firefox.config.ini).
    sealed class PageSection
    {
        public string Name;
        public readonly List<string> Match = new List<string>();   // regex vs URL
        public readonly List<string> Hide = new List<string>();    // -like intent label
        public readonly List<string> Keep = new List<string>();    // -like whitelist
        public readonly List<string> Strip = new List<string>();   // regex text line
        public readonly List<string> Expect = new List<string>();  // -like staleness anchor
        public readonly Dictionary<string, string> Intents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> -like
        public string KeepAfter, KeepUntil;
        public int MaxText;
    }

    // Loads + matches the per-page config. Source: a file beside the exe (editable), else the embedded copy.
    static class PageConfig
    {
        static List<PageSection> _sections = new List<PageSection>();
        static PageSection _global;

        public static void Load()
        {
            _sections = new List<PageSection>(); _global = null;
            string text = Read();
            if (text == null) return;
            PageSection cur = null;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    cur = new PageSection { Name = line.Substring(1, line.Length - 2).Trim() };
                    if (cur.Name == "*") _global = cur; else _sections.Add(cur);
                    continue;
                }
                if (cur == null) continue;
                int eq = line.IndexOf('='); if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (k.StartsWith("intent ")) { cur.Intents[k.Substring(7).Trim()] = v; continue; }
                switch (k)
                {
                    case "name": cur.Name = v; break;
                    case "match": cur.Match.Add(v); break;
                    case "hide": cur.Hide.Add(v); break;
                    case "keep": cur.Keep.Add(v); break;
                    case "strip": cur.Strip.Add(v); break;
                    case "expect": cur.Expect.Add(v); break;
                    case "keep_after": cur.KeepAfter = v; break;
                    case "keep_until": cur.KeepUntil = v; break;
                    case "max_text": int.TryParse(v, out cur.MaxText); break;
                }
            }
        }

        public static PageSection Match(string url)
        {
            foreach (var s in _sections)
                foreach (var m in s.Match)
                    try { if (Regex.IsMatch(url, m)) return s; } catch { }
            return null;
        }

        public static int GlobalMaxText => _global != null ? _global.MaxText : 0;

        // PowerShell-style -like: * and ? wildcards, case-insensitive, whole-string.
        public static bool Like(string s, string pattern)
        {
            string rx = "^" + Regex.Escape(pattern ?? "").Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try { return Regex.IsMatch(s ?? "", rx, RegexOptions.IgnoreCase); } catch { return false; }
        }

        static string Read()
        {
            try { var p = Path.Combine(AppContext.BaseDirectory, "agent-browser.config.ini"); if (File.Exists(p)) return File.ReadAllText(p); } catch { }
            try { using var st = typeof(Program).Assembly.GetManifestResourceStream("agent_browser.config.ini"); if (st != null) { using var r = new StreamReader(st); return r.ReadToEnd(); } } catch { }
            return null;
        }
    }

    static class Program
    {
        const string ProtocolVersion = "2024-11-05";
        static BrowserSession _session;
        static ApplicationContext _ctx;

        // ONE process, like ss-mcp: this exe IS the MCP stdio server AND the WebView2 owner. The webview is
        // LAZY — it opens only on the first browse. The warm session lives for the whole client session; logins
        // persist on disk in the profile across restarts. No node, no second daemon — one process.
        // DEFAULT = agent mode: MCP stdio server; the pane is display-only (non-interactive + blurred) and the
        // agent gets the HUD over MCP. `--user [url]` = run it yourself: the page is INTERACTIVE and the HUD
        // shows in its own WinForms window that refreshes as you browse. None of that is hard-coded — it's the
        // default, flipped by the flag.
        [STAThread]
        static void Main(string[] args)
        {
            args = args ?? new string[0];
            bool userMode = false, forced = false; string startUrl = null;
            foreach (var a in args)
            {
                if (a == "--user" || a == "-u") { userMode = true; forced = true; }
                else if (a == "--mcp" || a == "--agent") { userMode = false; forced = true; }
                else if (!a.StartsWith("-")) startUrl = a;
            }
            // No flag -> auto-detect: Claude Code PIPES stdin (redirected) => agent/MCP mode; a human
            // double-click has an interactive console (stdin NOT redirected) => user mode.
            if (!forced) { try { userMode = !Console.IsInputRedirected; } catch { userMode = false; } }
            // User mode is a single GUI card — hide the console window so it isn't a second window.
            if (userMode) { try { var ch = GetConsoleWindow(); if (ch != IntPtr.Zero) ShowWindow(ch, 0); } catch { } }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Application.EnableVisualStyles();
            PageConfig.Load();
            BrowserSession.BeginResolveLocation();   // geolocate off the UI thread; HUD reads the cached value
            _session = new BrowserSession(userMode);  // hidden form, handle forced — NO webview yet (lazy)
            _ctx = new ApplicationContext();
            if (userMode) _session.StartUserMode(startUrl);
            else new Thread(McpLoop) { IsBackground = true, Name = "mcp-stdio" }.Start();
            Application.Run(_ctx);                     // UI pump
        }

        // Newline-delimited JSON-RPC on stdin/stdout. stdin EOF = the client disconnected = exit cleanly.
        static void McpLoop()
        {
            try
            {
                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    JsonDocument req;
                    try { req = JsonDocument.Parse(line); } catch { continue; }
                    using (req)
                    {
                        var root = req.RootElement;
                        string method = root.TryGetProperty("method", out var m) ? (m.GetString() ?? "") : "";
                        if (!root.TryGetProperty("id", out var idEl)) continue;   // notification
                        object result = null, error = null;
                        try { result = Resolve(method, root); }
                        catch (Exception ex) { error = new { code = -32603, message = ex.Message }; }
                        Write(idEl, result, error);
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[agent-browser] mcp: " + ex.Message); }
            try { _session.Form.BeginInvoke(new Action(() => { _session.Shutdown(); _ctx.ExitThread(); })); } catch { }
        }

        static object Resolve(string method, JsonElement root)
        {
            switch (method)
            {
                case "initialize":
                    return new
                    {
                        protocolVersion = ProtocolVersion,
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "agent-browser", version = "0.4.0" },
                    };
                case "ping":       return new { };
                case "tools/list": return new { tools = ToolList() };
                case "tools/call": return CallTool(root);
                default: throw new InvalidOperationException("unknown method: " + method);
            }
        }

        static object[] ToolList() => new object[]
        {
            Tool("browse_goto",
                 "Navigate the warm browser session to a URL, wait until the page SETTLES (network/IO quiescence — no timeout), then return the numbered-element HUD.",
                 new Dictionary<string, object> { ["url"] = Prop("string", "URL to open (https:// is added if the scheme is missing).") },
                 "url"),
            Tool("browse_click",
                 "Click the element with the given HUD id [N], settle, and return the new HUD.",
                 new Dictionary<string, object> { ["id"] = Prop("integer", "The [N] id of an element from the current HUD.") },
                 "id"),
            Tool("browse_type",
                 "Type text into element [N] and press Enter, settle, and return the new HUD.",
                 new Dictionary<string, object> { ["id"] = Prop("integer", "The [N] id of an input/textarea from the HUD."), ["text"] = Prop("string", "Text to type.") },
                 "id", "text"),
            Tool("browse_back",
                 "Go back in history, settle, and return the new HUD.",
                 new Dictionary<string, object>()),
            Tool("browse_hud",
                 "Re-extract and return the current settled HUD (no navigation). Use to poll a streaming answer.",
                 new Dictionary<string, object>()),
            Tool("browse_do",
                 "Invoke a SCOPED intent by name (e.g. 'ask', 'send', 'new_thread') — resolved per-site from the config, so you don't chase shifting [N] ids. Settles and returns the new HUD.",
                 new Dictionary<string, object> { ["name"] = Prop("string", "Scoped intent name shown in the HUD's [SCOPED] line.") },
                 "name"),
            Tool("browse_chunk",
                 "View chunk N (0-based) of a long page's text without re-navigating.",
                 new Dictionary<string, object> { ["n"] = Prop("integer", "0-based chunk index.") },
                 "n"),
            Tool("browse_end",
                 "End the browse SESSION (hide the widget, drop the page) WITHOUT killing the daemon — the next browse re-opens a fresh session. Logins persist in the profile.",
                 new Dictionary<string, object>()),
        };

        static object Prop(string type, string description) => new { type, description };

        static object Tool(string name, string description, Dictionary<string, object> props, params string[] required)
            => new
            {
                name,
                description,
                inputSchema = new { type = "object", properties = props, required },
            };

        static object CallTool(JsonElement root)
        {
            var p = root.GetProperty("params");
            string name = p.GetProperty("name").GetString() ?? "";
            JsonElement a = p.TryGetProperty("arguments", out var av) ? av : default;
            string text;
            try { text = _session.RunTool(name, a); }
            catch (Exception ex) { text = "ERROR: " + ex.Message; }
            return new { content = new object[] { new { type = "text", text } }, isError = text.StartsWith("ERROR:", StringComparison.Ordinal) };
        }

        static void Write(JsonElement id, object result, object error)
        {
            var payload = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = ParseId(id) };
            if (error != null) payload["error"] = error; else payload["result"] = result;
            Console.Out.WriteLine(JsonSerializer.Serialize(payload));
            Console.Out.Flush();
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        static object ParseId(JsonElement id)
        {
            switch (id.ValueKind)
            {
                case JsonValueKind.Number: return id.GetInt64();
                case JsonValueKind.String: return id.GetString();
                default: return id.ToString();
            }
        }
    }

    // BrowserSession — owns the one persistent WebView2 + the browse ops + the flow-settle. All WebView2
    // work runs on the UI thread; RunTool marshals each tool call there and blocks the MCP thread for the
    // settled result (the UI thread keeps pumping, so WebView2 async continuations complete).
    sealed class BrowserSession
    {
        public PipForm Form { get; }

        readonly WebView2 _wv;
        CoreWebView2 _cw;
        bool _inited;   // true once the WebView2 session has been brought up (lazily, on first browse)

        // VIRTUAL HANDLES: HUD renumbers filtered intents 1..N; these map handle -> raw DOM data-ag-id, and
        // scoped intent name -> handle, so clicks land and `browse_do ask` works without chasing shifting ids.
        Dictionary<int, int> _handleMap = new Dictionary<int, int>();
        Dictionary<string, int> _scopedMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static string _deviceLoc;   // resolved once (IP geolocation), cached

        readonly bool _userMode;            // --user: a clickable HTML HUD card; the real page renders behind it
        WebView2 _card;                     // the visible card (an HTML projection of the HUD)
        CoreWebView2Environment _env;       // shared by the page view + the card view (one profile)

        // CDP in-flight request ids — the precise network-idle signal (better than packet counting).
        readonly HashSet<string> _inflight = new HashSet<string>(StringComparer.Ordinal);
        readonly object _inflightLock = new object();
        long _reqTotal;   // monotonic count of requestWillBeSent — the new-request RATE drives the settle

        const int IoQuietBytesPerPoll = 24_000;   // < ~240 KB/s over a 100ms poll counts as IO-quiescent
        static readonly string ThumbPath = Path.Combine(Path.GetTempPath(), "agent-browser", "frame.png");
        // Everything self-managed lives under one LocalAppData root: the warm profile (the precious, save-able
        // / SQLite-able state) and the self-extracted WebView2 engine cache (disposable, re-extracts).
        static readonly string LocalAppRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-browser");
        // Permanent per-user profile so logins (google.com/ai, ChatGPT, DeepSeek) + cookies + history PERSIST
        // across runs — saved, not thrown away in %TEMP%.
        // Separate profiles so `--user` can run at the same time as the agent without a profile lock.
        string UserDataDir => Path.Combine(LocalAppRoot, _userMode ? "profile-user" : "profile");

        public BrowserSession(bool userMode = false)
        {
            _userMode = userMode;

            if (userMode)
            {
                // A normal Windows 11 window: real title bar (rounded corners + min/max/close come free),
                // theme-aware, optional Mica. The card WebView2 fills the client area.
                Form = new PipForm
                {
                    FormBorderStyle = FormBorderStyle.Sizable, NativeChrome = true, TopMost = false, ShowInTaskbar = true,
                    StartPosition = FormStartPosition.CenterScreen, Width = 560, Height = 760,
                    MinimumSize = new Size(320, 240), Text = "agent-browser",
                    BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText,
                };
                _wv = new WebView2 { Dock = DockStyle.Fill };   // page renders behind the card
                Form.Controls.Add(_wv);
            }
            else
            {
                // Agent mode: tiny, borderless, translucent vibe pane with custom drag-bar + ✕ + extend-blind.
                const int TOP = 14, BOT = 12;
                Form = new PipForm
                {
                    FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Width = 160, Height = 110, Opacity = 0.8,
                    MinimumSize = new Size(140, 80), Text = "agent-browser",
                };
                Form.TopBand = TOP; Form.BottomBand = BOT;
                Form.Location = new Point(40, 40);
                Form.BackColor = Color.FromArgb(24, 24, 27);
                _wv = new WebView2
                {
                    Location = new Point(0, TOP), Size = new Size(160, 110 - TOP - BOT),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                };
                Form.Controls.Add(_wv);
                var close = new Label
                {
                    Text = "✕", Width = TOP + 8, Height = TOP, Location = new Point(160 - (TOP + 8), 0),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right, ForeColor = Color.Gainsboro,
                    BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 8f), Cursor = Cursors.Hand,
                };
                close.MouseEnter += (s, e) => { close.BackColor = Color.FromArgb(210, 60, 60); close.ForeColor = Color.White; };
                close.MouseLeave += (s, e) => { close.BackColor = Color.Transparent; close.ForeColor = Color.Gainsboro; };
                close.Click += (s, e) => EndSession();
                Form.Controls.Add(close); close.BringToFront();
            }

            _ = Form.Handle;   // force the HWND; the form stays hidden until a session
        }

        // Lazy: no WebView2 session exists until a browse is requested ("shouldn't even launch a webview
        // session without one being requested"). The first call brings up the engine and SHOWS the widget;
        // later calls just ensure it is up — a live session must be VISIBLE on Windows or capture/render goes
        // headless, so we never hide it WHILE active.
        async Task EnsureSessionAsync()
        {
            if (!_inited)
            {
                _env = await CreateEnvironment();
                await _wv.EnsureCoreWebView2Async(_env);
                _cw = _wv.CoreWebView2;
                try { _wv.ZoomFactor = 0.3; } catch { }   // zoomed way out — a vibe pane, not for reading

                // CDP Network tracking drives the settle: a request id is added on requestWillBeSent
                // (idempotent across redirects) and removed when it finishes or fails.
                await _cw.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                _cw.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (s, e) => InflightAdd(e.ParameterObjectAsJson);
                _cw.GetDevToolsProtocolEventReceiver("Network.loadingFinished").DevToolsProtocolEventReceived += (s, e) => InflightRemove(e.ParameterObjectAsJson);
                _cw.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (s, e) => InflightRemove(e.ParameterObjectAsJson);
                // Display-only pane: no context menus, no accidental zoom; inject pointer-events:none so the
                // HUMAN can't click the web (the agent drives via CDP/el.click(), which is unaffected).
                try
                {
                    _cw.Settings.AreDefaultContextMenusEnabled = false;
                    _cw.Settings.IsZoomControlEnabled = false;
                    _cw.Settings.AreBrowserAcceleratorKeysEnabled = false;
                }
                catch { }
                // Agent mode ONLY: display-only + ambient. pointer-events:none stops the human from touching the
                // DOM (which would desync the agent), blur makes it a vibe (the agent reads innerText, unaffected
                // by CSS filter). In --user mode the page stays fully interactive — you drive it.
                if (!_userMode)
                    await _cw.AddScriptToExecuteOnDocumentCreatedAsync(
                        "try{var s=document.createElement('style');s.textContent='html{pointer-events:none!important;user-select:none!important;-webkit-user-select:none!important;filter:blur(4px)!important}';(document.head||document.documentElement).appendChild(s);}catch(e){}");

                _cw.Navigate(HomeUrl);   // comes up on google.com/ai automatically
                _inited = true;
            }
            // Agent mode shows the pane here. User mode defers Show to StartUserMode so the card comes up
            // FIRST and the raw page is never seen.
            if (!_userMode)
            {
                if (!Form.Visible) Form.Show();
                Form.TopMost = true;   // WinForms can quietly drop it after WebView2 focus/activation churn
                SetWindowPos(Form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                Form.BringToFront();
            }
        }

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        // The X ends the SESSION: drop the page and the widget, but the MCP server keeps running so the next
        // browse starts a fresh session. Closing must NEVER disconnect the server (that was the bug).
        void EndSession()
        {
            try { if (_cw != null) _cw.Navigate("about:blank"); } catch { }
            Form.Hide();
        }

        // Real shutdown — only on stdin EOF (the MCP client actually disconnected).
        public void Shutdown()
        {
            try { Form.Close(); } catch { }
        }

        // --user: ONE window. The real page renders in _wv (behind), and a CARD WebView2 on top shows an HTML
        // projection of the HUD — clickable intents + a command box. You drive via the card; script-extraction
        // reads _wv even though it's covered (no headless capture is used). (Agent mode never calls this.)
        public void StartUserMode(string url)
        {
            Form.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await EnsureSessionAsync();   // env + page view; form stays HIDDEN in user mode

                    // Card FIRST (fills the client area, on top), with a loading state — page never flashes.
                    _card = new WebView2 { Dock = DockStyle.Fill };
                    Form.Controls.Add(_card); _card.BringToFront();
                    await _card.EnsureCoreWebView2Async(_env);   // SAME env/profile as the page
                    _card.CoreWebView2.NavigateToString(HudToHtml("[PAGE] starting…\n[DEVICE] " + DeviceLine() + "\n[STALE] ok\n[TEXT]\nbringing up the session…"));
                    _card.CoreWebView2.WebMessageReceived += async (s, e) =>
                    { try { await HandleCardCommand(e.TryGetWebMessageAsString()); } catch { } };

                    ApplyWin11Chrome(Form);   // dark/light per system theme + rounded corners + Mica attempt
                    Form.Show();              // a normal Win11 window (title bar, not topmost)

                    string u = string.IsNullOrWhiteSpace(url) ? HomeUrl : (url.Contains("://") ? url : "https://" + url);
                    _cw.Navigate(u);             // page renders BEHIND the card
                    await SettleAsync();
                    await RefreshCard();
                }
                catch (Exception ex) { Console.Error.WriteLine("[user] " + ex.Message); }
            }));
        }

        async Task RefreshCard()
        {
            string hud = await HudAsync("card");               // extract the HUD from the page view (_cw)
            _card.CoreWebView2.NavigateToString(HudToHtml(hud)); // project it into the card
        }

        async Task HandleCardCommand(string cmd)
        {
            cmd = (cmd ?? "").Trim();
            if (cmd.Length == 0) return;
            try { await _card.CoreWebView2.ExecuteScriptAsync("(function(){document.body.style.opacity='0.5';var c=document.getElementById('cmd');if(c)c.placeholder='⏳ settling…';})()"); } catch { }
            if (cmd.StartsWith("/goto ")) await GotoAsync(cmd.Substring(6));
            else if (Regex.IsMatch(cmd, @"^/\d+$")) await ClickAsync(int.Parse(cmd.Substring(1)));
            else if (cmd.StartsWith("/type ")) { int h = AskHandle(); if (h > 0) await TypeAsync(h, cmd.Substring(6)); }
            else { int h = AskHandle(); if (h > 0) await TypeAsync(h, cmd); }   // plain text -> type into the ask box
            await RefreshCard();
        }

        int AskHandle()
        {
            if (_scopedMap != null && _scopedMap.TryGetValue("ask", out var h)) return h;
            return -1;
        }

        // Make the user-mode window look like Windows 11: dark/light title bar per system theme, rounded
        // corners, and a Mica backdrop attempt (won't visibly show through the opaque card, but harmless).
        static void ApplyWin11Chrome(Form f)
        {
            try
            {
                IntPtr h = f.Handle;
                int dark = SystemDark() ? 1 : 0; DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));   // IMMERSIVE_DARK_MODE
                int round = 2; DwmSetWindowAttribute(h, 33, ref round, sizeof(int));                     // CORNER_PREFERENCE = ROUND
                int mica = 2; DwmSetWindowAttribute(h, 38, ref mica, sizeof(int));                       // SYSTEMBACKDROP_TYPE = MAINWINDOW
            }
            catch { }
        }

        static bool SystemDark()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (k?.GetValue("AppsUseLightTheme") is int i) return i == 0;
            }
            catch { }
            return true;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // Resolve the WebView2 engine, preferring SELF-SUFFICIENCY:
        //   1. the embedded Fixed Version runtime, self-extracted once to a versioned cache;
        //   2. a WebView2Runtime\ folder dropped beside the exe (dev override);
        //   3. system Evergreen (browserFolder == null) — last resort, NOT self-sufficient.
        const string HomeUrl = "https://google.com/ai";   // the page that comes up automatically

        Task<CoreWebView2Environment> CreateEnvironment()
        {
            string browserFolder = null;
            try { browserFolder = ExtractEmbeddedRuntime(); } catch (Exception ex) { Console.Error.WriteLine("[agent-browser] runtime extract: " + ex.Message); }
            if (browserFolder == null)
            {
                string beside = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
                if (File.Exists(Path.Combine(beside, "msedgewebview2.exe"))) browserFolder = beside;
            }
            return CoreWebView2Environment.CreateAsync(browserFolder, UserDataDir, null);
        }

        // Self-extract the embedded Fixed Version runtime ONCE to LocalAppData\agent-browser\wv2-<len>\, keyed
        // by the embedded zip's length so a re-fetched (different-version) runtime auto-busts the cache. The
        // engine is a multi-process Chromium launched from msedgewebview2.exe by path, so it MUST live on a
        // real folder — we just make it materialize from inside the exe instead of depending on a system install.
        static string ExtractEmbeddedRuntime()
        {
            Stream res = typeof(Program).Assembly.GetManifestResourceStream("agent_browser.webview2_fixed.zip");
            if (res == null) return null;   // no runtime embedded (unfetched checkout) -> caller falls back
            using (res)
            {
                string cache = Path.Combine(LocalAppRoot, "wv2-" + res.Length);
                string exe = Path.Combine(cache, "msedgewebview2.exe");
                string ready = Path.Combine(cache, ".ready");
                if (File.Exists(exe) && File.Exists(ready)) return cache;   // already extracted

                try { if (Directory.Exists(cache)) Directory.Delete(cache, true); } catch { }
                Directory.CreateDirectory(cache);
                using (var zip = new ZipArchive(res, ZipArchiveMode.Read)) zip.ExtractToDirectory(cache, true);
                if (!File.Exists(exe)) return null;
                GrantAppContainer(cache);   // Win10 + Fixed Version >=120 renderer App Container; harmless on Win11
                File.WriteAllText(ready, DateTime.UtcNow.ToString("o"));
                return cache;
            }
        }

        // Grant ALL APPLICATION PACKAGES / ALL RESTRICTED APPLICATION PACKAGES read+execute on the extracted
        // runtime — required on Win10 for Fixed Version >=120 (renderer runs in an App Container). Best-effort.
        static void GrantAppContainer(string dir)
        {
            foreach (var sid in new[] { "*S-1-15-2-1", "*S-1-15-2-2" })
            {
                try
                {
                    var psi = new ProcessStartInfo("icacls", "\"" + dir + "\" /grant " + sid + ":(OI)(CI)(RX) /T /Q /C")
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    using var p = Process.Start(psi); p?.WaitForExit(20000);
                }
                catch { }
            }
        }

        void InflightAdd(string paramsJson)
        {
            Interlocked.Increment(ref _reqTotal);
            var id = JsonField(paramsJson, "requestId");
            if (id == null) return;
            lock (_inflightLock) _inflight.Add(id);
        }

        void InflightRemove(string paramsJson)
        {
            var id = JsonField(paramsJson, "requestId");
            if (id == null) return;
            lock (_inflightLock) _inflight.Remove(id);
        }

        int InflightCount() { lock (_inflightLock) return _inflight.Count; }
        long ReqCount() => Interlocked.Read(ref _reqTotal);

        static string JsonField(string json, string name)
        {
            try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null; }
            catch { return null; }
        }

        // The one tool dispatcher — marshal the async browse op onto the UI thread; block the MCP thread for
        // its settled result. The UI thread keeps pumping, so the WebView2 async work completes.
        public string RunTool(string name, JsonElement a)
        {
            Func<Task<string>> inner;
            bool needsSession = true;
            switch (name)
            {
                case "browse_goto":  { string url = Str(a, "url");  inner = () => GotoAsync(url); break; }
                case "browse_click": { int id = Int(a, "id");       inner = () => ClickAsync(id); break; }
                case "browse_type":  { int id = Int(a, "id"); string t = Str(a, "text"); inner = () => TypeAsync(id, t); break; }
                case "browse_back":  inner = () => BackAsync(); break;
                case "browse_hud":   inner = () => HudAsync("hud"); break;
                case "browse_do":    { string nm = Str(a, "name"); inner = () => DoAsync(nm); break; }
                case "browse_chunk": { int n = Int(a, "n"); inner = () => HudAsync("chunk " + n, n); break; }
                case "browse_end":   needsSession = false; inner = () => EndAsync(); break;
                default: return "ERROR: unknown tool '" + name + "'";
            }
            // A browse REQUEST is what lazily creates (or re-shows) the session/widget on the UI thread, right
            // before the op. browse_end must NOT re-create it.
            Func<Task<string>> op = async () => { if (needsSession) await EnsureSessionAsync(); return await inner(); };
            return ((Task<string>)Form.Invoke(op)).GetAwaiter().GetResult();
        }

        static string Str(JsonElement a, string key)
            => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        static int Int(JsonElement a, string key)
            => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : -1;

        async Task<string> GotoAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "ERROR: empty url";
            if (!url.Contains("://")) url = "https://" + url;
            _cw.Navigate(url);
            await SettleAsync();
            return await HudAsync("goto " + url);
        }

        // Virtual handle -> raw DOM data-ag-id (falls back to the raw id if there's no map yet).
        int Dom(int handle) { return _handleMap != null && _handleMap.TryGetValue(handle, out var d) ? d : handle; }

        async Task<string> ClickAsync(int id)
        {
            int dom = Dom(id);
            var r = await _cw.ExecuteScriptAsync(
                "(function(){var el=document.querySelector(\"[data-ag-id='" + dom + "']\");if(!el)return 'NO_ELEMENT';el.scrollIntoView();el.click();return 'OK';})()");
            if (r != null && r.Contains("NO_ELEMENT")) return "ERROR: no element [" + id + "] in the current HUD";
            await SettleAsync();
            return await HudAsync("click " + id);
        }

        async Task<string> DoAsync(string name)
        {
            if (_scopedMap != null && _scopedMap.TryGetValue(name ?? "", out var h)) return await ClickAsync(h);
            return "ERROR: no scoped intent '" + name + "' on this page (see the HUD [SCOPED] line)";
        }

        Task<string> EndAsync()
        {
            EndSession();
            return Task.FromResult("session ended — widget hidden, page dropped; the daemon stays warm and the next browse re-opens (logins persist in the profile).");
        }

        async Task<string> TypeAsync(int id, string text)
        {
            int dom = Dom(id);
            var r = await _cw.ExecuteScriptAsync(
                "(function(){var el=document.querySelector(\"[data-ag-id='" + dom + "']\");if(!el)return 'NO_ELEMENT';el.focus();return 'OK';})()");
            if (r != null && r.Contains("NO_ELEMENT")) return "ERROR: no element [" + id + "] in the current HUD";
            await Task.Delay(120);
            string cdp = (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            await _cw.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"" + cdp + "\"}");
            await _cw.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"rawKeyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"nativeVirtualKeyCode\":13}");
            await _cw.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"nativeVirtualKeyCode\":13}");
            await SettleAsync();
            return await HudAsync("type " + id);
        }

        async Task<string> BackAsync()
        {
            await _cw.ExecuteScriptAsync("history.back()");
            await SettleAsync();
            return await HudAsync("back");
        }

        // Flow-settle, firefox.ps1's way: the page is SETTLED when network/IO flow goes quiescent and the DOM
        // is complete, HELD for a short window. Quiescence is a RATE below a threshold (firefox.ps1's <5
        // packets/s), NOT zero — a chatty page (google.com/ai) settles once its burst ends, and an actively
        // streaming answer keeps the webview2 IO bytes flowing so it is NOT called settled until the stream
        // stops. The wall clock is a DEAD MAN'S SWITCH only — never the normal way we decide settled.
        async Task SettleAsync()
        {
            const int idleHoldMs = 700, deadManMs = 20_000, pollMs = 150;
            const int newReqQuiet = 2;   // < 2 new requests per poll = network quiescent
            var sw = Stopwatch.StartNew();
            long lastIo = ReadWebViewIoBytes();
            long lastReq = ReqCount();
            long lastLen = -1, quietSince = -1;
            while (sw.ElapsedMilliseconds < deadManMs)
            {
                await Task.Delay(pollMs);
                long io = ReadWebViewIoBytes(); bool ioQuiet = (io - lastIo) < IoQuietBytesPerPoll; lastIo = io;
                long req = ReqCount();           bool netQuiet = (req - lastReq) < newReqQuiet;   lastReq = req;
                var st = await DomState();
                bool textStable = lastLen >= 0 && st.len == lastLen; lastLen = st.len;   // streaming answer => text grows => not settled
                if (ioQuiet && netQuiet && st.ready && textStable)
                {
                    if (quietSince < 0) quietSince = sw.ElapsedMilliseconds;
                    if (sw.ElapsedMilliseconds - quietSince >= idleHoldMs) return;
                }
                else quietSince = -1;
            }
        }

        async Task<(bool ready, long len)> DomState()
        {
            try
            {
                var r = await _cw.ExecuteScriptAsync("JSON.stringify([document.readyState,(document.body?document.body.innerText.length:0)])");
                using var d = JsonDocument.Parse(JsonSerializer.Deserialize<string>(r));
                return (d.RootElement[0].GetString() == "complete", d.RootElement[1].GetInt64());
            }
            catch { return (true, 0); }
        }

        // The HUD as a GRAPH (ported from firefox.work.ps1): friendly page name, device telemetry, config-shaped
        // text, scoped named intents, virtual handles (1..N over the filtered set), staleness, and chunking.
        async Task<string> HudAsync(string lastStep, int chunk = 0)
        {
            string raw = await _cw.ExecuteScriptAsync(HudJs);
            string json; try { json = JsonSerializer.Deserialize<string>(raw); } catch { json = raw; }
            string url = "", title = "", text = ""; var els = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                url = doc.RootElement.GetProperty("url").GetString() ?? "";
                title = doc.RootElement.GetProperty("title").GetString() ?? "";
                text = doc.RootElement.GetProperty("text").GetString() ?? "";
                foreach (var x in doc.RootElement.GetProperty("els").EnumerateArray()) els.Add(x.GetString() ?? "");
            }
            catch { }

            var sec = PageConfig.Match(url);
            string page = sec != null ? sec.Name : url;

            // Filter (hide/keep) -> renumber to virtual handles -> map handle -> raw data-ag-id.
            _handleMap = new Dictionary<int, int>();
            var intents = new List<(int handle, string type, string label)>();
            int h = 0;
            foreach (var el in els)
            {
                var parts = el.Split('|');
                if (parts.Length != 3) continue;
                if (!int.TryParse(parts[0], out int dom)) continue;
                string type = parts[1], label = parts[2];
                if (sec != null)
                {
                    bool hidden = false; foreach (var hp in sec.Hide) if (PageConfig.Like(label, hp)) { hidden = true; break; }
                    if (hidden) continue;
                    if (sec.Keep.Count > 0) { bool ok = false; foreach (var kp in sec.Keep) if (PageConfig.Like(label, kp)) { ok = true; break; } if (!ok) continue; }
                }
                h++; _handleMap[h] = dom; intents.Add((h, type, label));
            }

            // Scoped named intents (name -> handle) + staleness anchors.
            _scopedMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var scoped = new List<string>();
            var stale = new List<string>();
            if (sec != null)
            {
                foreach (var kv in sec.Intents)
                    foreach (var it in intents)
                        if (PageConfig.Like(it.label, kv.Value)) { _scopedMap[kv.Key] = it.handle; scoped.Add(kv.Key + "->/" + it.handle); break; }
                foreach (var ex in sec.Expect)
                {
                    bool found = false; foreach (var it in intents) if (PageConfig.Like(it.label, ex)) { found = true; break; }
                    if (!found) stale.Add(ex);
                }
            }

            // Text shaping + chunking.
            string shaped = ShapeText(text, sec);
            int max = (sec != null && sec.MaxText > 0) ? sec.MaxText : PageConfig.GlobalMaxText;
            string chunkInfo = null, viewText = shaped;
            if (max > 0 && shaped.Length > max)
            {
                int total = (shaped.Length + max - 1) / max;
                if (chunk < 0) chunk = 0; if (chunk >= total) chunk = total - 1;
                int start = chunk * max, len = Math.Min(max, shaped.Length - start);
                viewText = shaped.Substring(start, len);
                chunkInfo = "[chunk " + (chunk + 1) + "/" + total + (chunk + 1 < total ? " — browse_chunk " + (chunk + 1) + " for more]" : " — end]");
            }

            var sb = new StringBuilder();
            sb.Append("[SITREP] after: ").Append(lastStep).Append('\n');
            sb.Append("[DEVICE] ").Append(DeviceLine()).Append('\n');
            sb.Append("[PAGE] ").Append(page).Append("  |  ").Append(title).Append('\n');
            if (scoped.Count > 0) sb.Append("[SCOPED] ").Append(string.Join("  ", scoped)).Append('\n');
            sb.Append("[STALE] ").Append(stale.Count > 0 ? "!! " + string.Join(", ", stale) : "ok").Append('\n');
            if (chunkInfo != null) sb.Append(chunkInfo).Append('\n');
            sb.Append("\n[TEXT]\n").Append(viewText);
            sb.Append("\n\n[INTENTS] browse_type <id> \"text\" | browse_click <id> | browse_do <name> | browse_goto <url> | browse_back | browse_end\n");
            foreach (var it in intents) sb.Append('[').Append(it.handle).Append("] ").Append(it.type).Append(' ').Append(it.label).Append('\n');
            return sb.ToString();
        }

        // Per-page text rules (keep_after/keep_until/strip + blank-line collapse). Mirrors firefox Format-HudText.
        static string ShapeText(string text, PageSection sec)
        {
            if (string.IsNullOrEmpty(text) || sec == null) return text ?? "";
            if (!string.IsNullOrEmpty(sec.KeepAfter)) { var m = Regex.Match(text, sec.KeepAfter, RegexOptions.Singleline); if (m.Success) text = text.Substring(m.Index + m.Length); }
            if (!string.IsNullOrEmpty(sec.KeepUntil)) { var m = Regex.Match(text, sec.KeepUntil, RegexOptions.Multiline); if (m.Success) text = text.Substring(0, m.Index); }
            if (sec.Strip.Count > 0)
            {
                var kept = new List<string>();
                foreach (var l in text.Split('\n')) { bool drop = false; foreach (var s in sec.Strip) { try { if (Regex.IsMatch(l, s)) { drop = true; break; } } catch { } } if (!drop) kept.Add(l); }
                text = string.Join("\n", kept);
            }
            return Regex.Replace(text, @"(\r?\n\s*){2,}", "\n").Trim();
        }

        // Device telemetry: fresh time/timezone each HUD; location resolved ONCE on a background thread (never
        // block the WebView2 UI thread — a sync HTTP call there deadlocks). Reads the cached value only.
        static string DeviceLine()
        {
            var now = DateTime.Now;
            return now.ToString("yyyy-MM-dd HH:mm:ss") + " " + now.DayOfWeek + " (" + TimeZoneInfo.Local.Id + ") @ " + (_deviceLoc ?? "(resolving)");
        }

        // A basic HTML projection of the text HUD: header, shaped text, clickable intent rows, a command box.
        // Clicks/Enter post back to the host via window.chrome.webview.postMessage.
        static string HudToHtml(string hud)
        {
            string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var sb = new StringBuilder();
            foreach (var l in (hud ?? "").Replace("\r", "").Split('\n'))
            {
                var m = Regex.Match(l, @"^\[(\d+)\]\s");
                if (m.Success) sb.Append("<div class=row onclick=\"post('/" + m.Groups[1].Value + "')\">" + E(l) + "</div>");
                else if (l.Length == 0) sb.Append("<div class=sp></div>");
                else sb.Append("<div class='" + (l.StartsWith("[") ? "tag" : "txt") + "'>" + E(l) + "</div>");
            }
            return "<!doctype html><html><head><meta charset=utf-8><style>"
                + "html,body{height:100%}body{margin:0;display:flex;flex-direction:column;background:#202020;color:#e6e6e6;"
                + "font:13px/1.5 'Segoe UI Variable Text','Segoe UI',system-ui}"
                + ".hud{flex:1 1 auto;overflow:auto;padding:12px 14px;white-space:pre-wrap;word-break:break-word}"
                + ".tag{color:#9aa0a6;font-size:11px;letter-spacing:.02em;margin-top:2px}.txt{color:#d8d8d8}.sp{height:9px}"
                + ".row{cursor:pointer;border-radius:6px;padding:6px 9px;margin:1px -5px}.row:hover{background:rgba(255,255,255,.07)}"
                + "#cmd{flex:0 0 auto;box-sizing:border-box;width:calc(100% - 24px);margin:8px 12px 12px;border:1px solid #3a3a3a;"
                + "border-radius:7px;padding:9px 12px;background:#2b2b2b;color:#fff;font:13px 'Segoe UI Variable Text','Segoe UI'}"
                + "#cmd:focus{outline:none;border-color:#4cc2ff;background:#323232}"
                + "@media (prefers-color-scheme: light){body{background:#f3f3f3;color:#1a1a1a}.tag{color:#616161}.txt{color:#2a2a2a}"
                + ".row:hover{background:rgba(0,0,0,.05)}#cmd{background:#fff;color:#111;border-color:#d4d4d4}#cmd:focus{border-color:#005fb8;background:#fff}}"
                + "</style></head><body>"
                + "<div class=hud>" + sb + "</div>"
                + "<input id=cmd autofocus placeholder='/goto x  ·  /2  ·  or just type to ask…' "
                + "onkeydown=\"if(event.key==='Enter'){post(this.value);this.value=''}\">"
                + "<script>function post(c){window.chrome.webview.postMessage(c)}</script>"
                + "</body></html>";
        }

        public static void BeginResolveLocation()
        {
            Task.Run(() =>
            {
                try
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                    var s = hc.GetStringAsync("http://ip-api.com/json/?fields=status,country,regionName,city,lat,lon").GetAwaiter().GetResult();
                    using var d = JsonDocument.Parse(s);
                    if (d.RootElement.GetProperty("status").GetString() == "success")
                        _deviceLoc = d.RootElement.GetProperty("city").GetString() + ", " + d.RootElement.GetProperty("regionName").GetString() + ", "
                                   + d.RootElement.GetProperty("country").GetString() + " [" + d.RootElement.GetProperty("lat").GetDouble() + ", " + d.RootElement.GetProperty("lon").GetDouble() + "]";
                    else _deviceLoc = "(unknown)";
                }
                catch { _deviceLoc = "(unknown)"; }
            });
        }

        // Await (never .GetResult() on the UI thread) + a hard timeout: if the widget isn't rendering (occluded/
        // minimized), CapturePreviewAsync can stall forever — we skip the thumbnail rather than wedge the HUD.
        async Task<string> CaptureThumbAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ThumbPath));
                using var fs = File.Create(ThumbPath);
                var cap = _cw.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                if (await Task.WhenAny(cap, Task.Delay(1500)) != cap) return null;   // stalled — skip
                await cap;
                return ThumbPath;
            }
            catch { return null; }
        }

        // The HUD projection: tag every visible interactive node data-ag-id=N, return {url,title,text,els[]}.
        const string HudJs = @"(function(){
  var url=document.location.href, title=document.title;
  var text=((document.body?document.body.innerText:'')||'').replace(/[ \t]+/g,' ').replace(/\n\s*\n/g,'\n').trim();
  if(text.length>60000) text=text.substring(0,60000)+'...';
  var els=[].slice.call(document.querySelectorAll('a,button,input,textarea,select,[role=button],[role=link],[role=textbox],[contenteditable=true],[onclick]'));
  var out=[],k=0;
  els.forEach(function(el){
    if(el.offsetParent===null||el.offsetWidth<=0||el.offsetHeight<=0) return;
    var label=((el.innerText||el.value||el.placeholder||el.getAttribute('aria-label')||el.title||el.name||el.alt||'')+'').replace(/\s+/g,' ').trim().substring(0,80);
    if(!label) return;
    el.setAttribute('data-ag-id',k);
    var type=el.tagName.toLowerCase(); if(el.type) type+='['+el.type+']';
    out.push(k+'|'+type+'|'+label); k++;
  });
  if(out.length>300) out=out.slice(0,300);
  return JSON.stringify({url:url,title:title,text:text,els:out});
})()";

        // ---- OS-level IO quiescence (the firefox.ps1 'IO Data Bytes/sec' analog) ----
        // Sum Read+Write transfer bytes over the msedgewebview2 processes that descend from THIS process
        // (our session's renderer/gpu/network children). Best-effort: any failure returns 0, which the
        // settle treats as quiescent, so it degrades safely to the CDP network-idle signal.
        static long ReadWebViewIoBytes()
        {
            try
            {
                var parentOf = SnapshotParents();
                int self = Process.GetCurrentProcess().Id;
                long total = 0;
                foreach (var pr in Process.GetProcessesByName("msedgewebview2"))
                {
                    try { if (IsDescendant(pr.Id, self, parentOf) && GetProcessIoCounters(pr.Handle, out var c)) total += (long)(c.ReadTransferCount + c.WriteTransferCount); }
                    catch { }
                    finally { pr.Dispose(); }
                }
                return total;
            }
            catch { return 0; }
        }

        static bool IsDescendant(int pid, int ancestor, Dictionary<int, int> parentOf)
        {
            int cur = pid, guard = 0;
            while (parentOf.TryGetValue(cur, out int par) && guard++ < 64)
            {
                if (par == ancestor) return true;
                cur = par;
            }
            return false;
        }

        static Dictionary<int, int> SnapshotParents()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32First(snap, ref pe))
                    do { map[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; } while (Process32Next(snap, ref pe));
            }
            finally { CloseHandle(snap); }
            return map;
        }

        const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct PROCESSENTRY32
        {
            public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID;
            public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase;
            public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll")] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll")] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    }
}
