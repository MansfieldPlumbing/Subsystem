using Android.App;
using Android.Hardware.Usb;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Window;
using Java.Interop;
using Subsystem.Device;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using VtNetCore.VirtualTerminal;

using System.Management.Automation.Host;
using System.Management.Automation.Provider;
using VtNetCore.XTermParser;

namespace Subsystem;

public class ReactInputEvent {
    public string type { get; set; } = "";
    public int cols { get; set; }
    public int rows { get; set; }
    public string key { get; set; } = "";
    public string text { get; set; } = "";
    public long tabId { get; set; }
}

public class TerminalSession : IDisposable {
    public long TabId { get; }
    private readonly MainActivity _main;
    public PowerShell Ps { get; private set; } = null!;
    public AndroidSubsystemHost Host { get; private set; } = null!;
    public VirtualTerminalController VtController { get; private set; }
    public DataConsumer VtConsumer { get; private set; }
    public ReplEngine Repl { get; private set; } = null!;
    public Queue<byte[]> OutputQueue { get; } = new Queue<byte[]>();
    public readonly object VtLock = new object();

    public void Dispose() {
        try {
            Repl?.Stop();
            Ps?.Dispose();
        } catch (Exception ex) { Dg.Warn("main", ex); }
    }

    public TerminalSession(long tabId, MainActivity main) {
        TabId = tabId;
        _main = main;
        VtController = new VirtualTerminalController();
        VtController.ResizeView(120, 40);
        VtConsumer = new DataConsumer(VtController);
    }

    public void Start(string appBasePath) {
        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: Init");
        var iss = InitialSessionState.Create();
        iss.LanguageMode = PSLanguageMode.FullLanguage;
        LoadFromAssembly(iss, typeof(PSObject).Assembly);
        LoadFromAssembly(iss, Assembly.Load("Microsoft.PowerShell.Commands.Utility"));
        LoadFromAssembly(iss, Assembly.Load("Microsoft.PowerShell.Commands.Management"));

        SubsystemAliases.Load(iss);

        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: Creating Host");
        Host = new AndroidSubsystemHost(this);
        var rs = RunspaceFactory.CreateRunspace(Host, iss);
        rs.Open();

        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: Init API");
        SubsystemApi.Initialize(iss, Host);
        SessionManager.Initialize(iss, Host); // named persistent PWSH sessions share this ISS/host

        Ps = PowerShell.Create();
        Ps.Runspace = rs;

        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: Env Setup");
        string initScript = $@"
$env:HOME = '{appBasePath}'
$env:PHONE_HOME = '/storage/emulated/0'
$Global:VOM = [Subsystem.Vom.Vom]
$Global:ctx = [Subsystem.MainActivity]::Instance
$env:POWERSHELL_TELEMETRY_OPTOUT = '1'
Set-Location -Path '{appBasePath}'
$env:PATH += [System.IO.Path]::PathSeparator + '{appBasePath}'
function global:prompt {{ ""PS $($ExecutionContext.SessionState.Path.CurrentLocation)> "" }}
function global:dir {{ Get-ChildItem @args | Format-Table -Property @{{N='Date';E={{$_.LastWriteTime.ToString('yyyy-MM-dd HH:mm')}}}}, @{{N='Type';E={{if($_.PSIsContainer){{'<DIR>'}}else{{''}}}}}}, @{{N='Size';E={{if(!$_.PSIsContainer){{$_.Length}}}}}}, Name -AutoSize }}
Set-Alias ls dir -Force
";
        Ps.AddScript(initScript);
        Ps.Invoke();
        Ps.Commands.Clear();

        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: Profile script");
        string profilePath = System.IO.Path.Combine(appBasePath, "profile.ps1");
        Ps.AddScript($"if (Test-Path '{profilePath}') {{ . '{profilePath}' }}");
        Ps.Invoke();
        Ps.Commands.Clear();

        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: ADB check");
        if (!SubsystemApi.IsAdbPaired())
        {
            FeedTerminal(Encoding.UTF8.GetBytes("\x1b[35m[System] Android 11+ Wireless Debugging is not paired yet.\x1b[0m\r\n"));
            FeedTerminal(Encoding.UTF8.GetBytes("\x1b[35m[System] Please go to Developer Options -> Wireless Debugging -> Pair device with pairing code.\x1b[0m\r\n"));
            FeedTerminal(Encoding.UTF8.GetBytes("\x1b[35m[System] Then tell me the port and code here (e.g. \"pair 41234 123456\").\x1b[0m\r\n"));
        }

        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: Starting REPL");
        Repl = new ReplEngine(this, Host, rs);
        Repl.Start();
        Android.Util.Log.Debug("SubsystemDebug", "TerminalSession.Start: Done");
    }

    private void LoadFromAssembly(InitialSessionState iss, Assembly assembly) {
        try {
            foreach (var type in assembly.GetTypes()) {
                var cmdletAttr = type.GetCustomAttribute<CmdletAttribute>();
                if (cmdletAttr != null) iss.Commands.Add(new SessionStateCmdletEntry($"{cmdletAttr.VerbName}-{cmdletAttr.NounName}", type, ""));
                var providerAttr = type.GetCustomAttribute<CmdletProviderAttribute>();
                if (providerAttr != null) iss.Providers.Add(new SessionStateProviderEntry(providerAttr.ProviderName, type, ""));
            }
        } catch (Exception ex) { Dg.Warn("main", ex); }
    }

    public void FeedTerminal(byte[] rawAnsiBytes) {
        lock (VtLock) {
            VtConsumer.Push(rawAnsiBytes);
            if (VtController.Changed) VtController.ClearChanges();
        }
        _main.SendRawToReact(TabId, rawAnsiBytes);
    }

    public void RouteRawInput(string payload) {
        if (string.IsNullOrEmpty(payload)) return;
        try {
            var rawUi = (AndroidSubsystemRawUserInterface)Host.UI.RawUI;
            for (int i = 0; i < payload.Length; i++) {
                char ch = payload[i]; ConsoleKey key = (ConsoleKey)0;
                if ((ch == '\x03' || ch == '\x1b') && Repl != null && Repl.IsRunning) {
                    Repl.StopActiveCommand();
                    continue;
                }
                if (ch == '\x1b' && i + 2 < payload.Length && payload[i + 1] == '[') {
                    switch (payload[i + 2]) {
                        case 'A': key = ConsoleKey.UpArrow;    break;
                        case 'B': key = ConsoleKey.DownArrow;  break;
                        case 'C': key = ConsoleKey.RightArrow; break;
                        case 'D': key = ConsoleKey.LeftArrow;  break;
                    }
                    if (key != (ConsoleKey)0) {
                        rawUi.InputQueue.Add(new KeyInfo((int)key, '\0', (ControlKeyStates)0, true));
                        i += 2; continue;
                    }
                }
                if      (ch == '\r' || ch == '\n') key = ConsoleKey.Enter;
                else if (ch == '\b' || ch == '\x7F') key = ConsoleKey.Backspace;
                else if (ch == '\t')  key = ConsoleKey.Tab;
                else if (ch == '\x1b') key = ConsoleKey.Escape;

                char keyChar = key switch { ConsoleKey.Enter => '\r', ConsoleKey.Backspace => '\b', ConsoleKey.Tab => '\t', ConsoleKey.Escape => '\x1b', _ => ch };
                rawUi.InputQueue.Add(new KeyInfo((int)key, keyChar, (ControlKeyStates)0, true));
            }
        } catch (Exception ex) { Dg.Warn("main", ex); }
    }

    public void ExecuteCommand(string command) {
        var owner = Vom.Vom.CreateOwner(@"\System\Terminal\Exec");
        Vom.Vom.Spawn(owner, "ExecCmd", _ => {
            try {
                FeedTerminal(Encoding.UTF8.GetBytes($"{command}\r\n"));
                Ps.Commands.Clear();
                Ps.AddScript(command);
                Ps.Invoke();
                if (Ps.HadErrors) foreach (var error in Ps.Streams.Error) FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[31m{error}\x1b[0m\r\n"));
                FeedTerminal(Encoding.UTF8.GetBytes("\x1b[34mPS>\x1b[0m "));
            } catch (Exception ex) { FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[31mFatal Exec Error: {ex.Message}\x1b[0m\r\n")); }
        });
    }

    public Coordinates GetCursorPosition() { lock (VtLock) { return new Coordinates(VtController.CursorState.CurrentColumn, VtController.CursorState.CurrentRow); } }
    public Size GetWindowSize() { lock (VtLock) { return new Size(VtController.VisibleColumns, VtController.VisibleRows); } }
}

// Name is pinned (not crc-mangled) so AndroidManifest activity-aliases — the FEDERATION's per-door
// launcher icons (Editor/Terminal/Settings/…) — can target this activity by a stable component name.
[Activity(Name = "dev.mansfieldplumbing.subsystem.MainActivity", Label = "@string/app_name", Icon = "@mipmap/appicon", RoundIcon = "@mipmap/appicon_round", MainLauncher = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar", WindowSoftInputMode = Android.Views.SoftInput.AdjustResize, ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.KeyboardHidden | Android.Content.PM.ConfigChanges.ScreenLayout)]
// ADB-free USB (CRQ185 rung 2 / DpAoa.cs): the OS relaunches THIS activity with the matching intent
// when a host puts the phone into accessory mode (device role) or when a peer already in accessory
// mode is plugged in via OTG (host role) — HandleUsbIntent (OnCreate/OnNewIntent) routes both to DpAoa.
[IntentFilter(new[] { UsbManager.ActionUsbAccessoryAttached })]
[MetaData(UsbManager.ActionUsbAccessoryAttached, Resource = "@xml/accessory_filter")]
[IntentFilter(new[] { UsbManager.ActionUsbDeviceAttached })]
[MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
// App-icon long-press shortcuts (launcher menu). Static list in @xml/shortcuts; each entry launches a
// door activity-alias by component name, routed by DoorFromIntent — no new intent handling needed.
[MetaData("android.app.shortcuts", Resource = "@xml/shortcuts")]
// SECURITY (history): the .ssr "open-to-import" ACTION_VIEW intent filters — and later the whole .ssr
// file-format module — were REMOVED. Open-to-import let ANY app or browsable link inject capabilities/verbs
// into Cm with no confirmation, reachable by the elevated uid=2000 adb channel. Verbs are Cm records,
// registered at runtime (Register-Capability / presenter menu-context); there is no file-import lane.
public class MainActivity : Activity
{
    private WebView _webView = null!;
    public bool IsReactReady { get; private set; } = false;
    public ConcurrentDictionary<long, TerminalSession> Sessions { get; } = new();
    public static MainActivity? Instance { get; private set; }

    // DpAoa — at most one active role at a time (device XOR host); re-attaching tears down the prior one.
    private DpAoaDevice? _aoaDevice;
    private DpAoaHost? _aoaHost;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Instance = this;
        HandleUsbIntent(Intent);   // cold start: the OS relaunched us with an already-attached accessory/device

        // SubsystemDom (Diagnostic Object Manager) — arm crash capture + persistent diag log
        // to /sdcard/SubsystemDom/ (survives reinstall) as early as possible.
        Dg.Initialize(this);

        // Move any pre-models/ flat model files (files/<name>) into files/models/ so a model
        // downloaded before this refactor is recognized as installed without re-downloading.
        try { ModelCatalog.MigrateLegacyLayout(this); } catch (Exception ex) { Dg.Warn("main", ex); }

        if (!Android.Provider.Settings.CanDrawOverlays(this)) {
            StartActivity(new Android.Content.Intent(Android.Provider.Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse("package:" + PackageName)));
        }

        // ADD THIS 1 LINE: Force the PowerShell engine to boot headlessly 
        // as Tab 0 immediately on startup. This initializes the API pool.
        CreateSession(0);

        _webView = new WebView(this);
        _webView.Settings.JavaScriptEnabled = true;
        _webView.Settings.DomStorageEnabled = true;
        _webView.Settings.AllowFileAccess = true;
        _webView.Settings.AllowFileAccessFromFileURLs = true;
        _webView.Settings.AllowUniversalAccessFromFileURLs = true;
        _webView.Settings.SetSupportZoom(false);
        _webView.Settings.UseWideViewPort = true;
        _webView.Settings.LoadWithOverviewMode = true;
        _webView.OverScrollMode = OverScrollMode.Never;
        _webView.SetWebViewClient(new SubsystemWebViewClient());
        _webView.SetWebChromeClient(new CustomWebChromeClient(this));
        _webView.SetDownloadListener(new SubsystemDownloadListener());

        Java.Lang.JavaSystem.LoadLibrary("psl-android");
        // SetDllImportResolver can only be set ONCE per assembly per process. OnCreate can run
        // again (activity recreated while the assistant/VoiceInteraction process stays alive),
        // so guard the second call — otherwise it throws "resolver already set" and crashes launch.
        try {
            NativeLibrary.SetDllImportResolver(typeof(System.Management.Automation.PowerShell).Assembly, (libraryName, assembly, searchPath) => {
                if (libraryName.Contains("libpsl-native")) return NativeLibrary.Load("libpsl-android.so", assembly, null);
                return IntPtr.Zero;
            });
        } catch (System.InvalidOperationException ex) { Dg.Warn("main", ex); /* resolver already set earlier this process */ }
        // The GPU q4 GEMM shaders (gemm.spv/gemm_q4.spv) ship as AndroidAssets, not loose files next to an
        // exe — Gpu.ShaderAssetReader defaults to a filesystem read (fine on Windows); this is the ONE place
        // allowed to touch Android.Content.Res.AssetManager, so the shared Dpx/GpuVulkan code stays platform-neutral.
        Subsystem.Dpx.Gpu.ShaderAssetReader = name => {
            using var s = Assets!.Open("shaders/" + name);
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        };

        // Pre-paint backdrop (visible only until the shell's first frame). BLACK read as a crash/hang on
        // launch; a neutral mid sky-blue (the classic desktop default) reads as "booting", not "dead".
        // This is a native surface OUTSIDE the WebView's CSS scope, so it can't reference var(--bg) — see
        // risks: ideally seeded from the active theme's --bg via Cm so the flash matches the shell.
        _webView.SetBackgroundColor(Android.Graphics.Color.Rgb(0x3A, 0x6E, 0xA5));
        _webView.LoadUrl("http" + "://shell/shell.obp");   // served in-process from assembly resources via ObpHost

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) this.OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new TerminalBackCallback(this));

        SetContentView(_webView);

        // We OWN the system bars: edge-to-edge, status bar hidden while Subsystem is open, swipe-from-top
        // reveals it transiently (the pull-down). Left/right edges are freed for our own swipes. API 30+.
        Window?.SetDecorFitsSystemWindows(false);
        ApplyImmersive();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 0);
        StartForegroundService(new Android.Content.Intent(this, typeof(SubsystemService)));

        // Edge ownership needs REAL decor dimensions, but the first OnWindowFocusChanged can beat
        // layout (observed on the Razr+ cover display: dv.Width==0 → the early-return left the edges
        // OS-owned, so a left swipe fired BACK instead of Charms). Re-assert on every decor layout —
        // cheap, idempotent. (Android still caps exclusion at ~200dp/edge, granted bottom-up: the
        // LOWER part of each edge is ours; mid/upper edge stays the OS back gesture.)
        try { if (Window?.DecorView is Android.Views.View _dv) _dv.LayoutChange += (s, e) => ApplyGestureExclusion(); } catch (Exception ex) { Dg.Warn("main", ex); }

        SeedAssets();

        SeedAssets();
        System.Environment.SetEnvironmentVariable("POWERSHELL_TELEMETRY_OPTOUT", "1");
        System.Environment.SetEnvironmentVariable("DOTNET_EnableDiagnostics", "0");

        // Deep-link: an `open` extra names the presenter to land on (the chat head's tap carries
        // open=agent). Cold start — the shell isn't loaded yet, so it's flushed on page-finish.
        _pendingOpen = Intent?.GetStringExtra("open");

        // THE FEDERATION: launched through a door alias (…door.<Id>) → load THAT presenter
        // full-bleed, no shell chrome. The component name IS the door id (manifest is the truth).
        var door = DoorFromIntent(Intent);
        if (door != null) LoadDoor(door);
    }

    // …door.Editor → "edit", …door.Broker → "agent", etc. Null = the main icon (the shell/hub).
    private static string? DoorFromIntent(Android.Content.Intent? intent)
    {
        var cls = intent?.Component?.ClassName ?? "";
        var i = cls.IndexOf(".door.", StringComparison.Ordinal);
        if (i < 0) return null;
        var id = cls.Substring(i + ".door.".Length).ToLowerInvariant();
        return id switch { "editor" => "edit", "broker" => "agent", _ => id };
    }

    // A door is the presenter ITSELF as the whole window — served flat from shell/presenters/.
    // (Presenters are standalone pages: they bring their own theme.css/themes.js.)
    private void LoadDoor(string presenterId)
    {
        RunOnUiThread(() => { try { _webView?.LoadUrl("http" + "://shell/presenters/" + presenterId + ".obp"); } catch (Exception ex) { Dg.Warn("main", ex); } });
    }

    // The presenter a launching intent asked us to open once the shell is up (flushed by
    // SubsystemWebViewClient.OnPageFinished; null = nothing pending).
    private string? _pendingOpen;

    // Warm path: the activity already exists (bubble tap with the app backgrounded) — the shell is
    // live, open the presenter immediately.
    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleUsbIntent(intent);   // hot attach: accessory/device plugged in while we're alive
        // Door alias tapped while we're alive (v1 = one task): become that door.
        var door = DoorFromIntent(intent);
        if (door != null) { LoadDoor(door); return; }
        var open = intent?.GetStringExtra("open");
        if (!string.IsNullOrEmpty(open)) OpenPresenter(open!);
    }

    // Route a USB accessory/device-attached intent to DpAoa. Permission is granted implicitly for the
    // app named in the matching accessory_filter/device_filter resource (the OS asks the user once,
    // "Open Subsystem when this USB accessory is connected?", before this activity is even launched) —
    // HasPermission inside DpAoa.Open/DpAoaHost.Open still re-checks fail-closed for the hot-attach path
    // where a filter match did NOT imply permission (e.g. a bare device attach with no matching filter).
    private void HandleUsbIntent(Android.Content.Intent? intent)
    {
        try
        {
            if (intent == null) return;
            if (intent.Action == UsbManager.ActionUsbAccessoryAttached)
            {
                var accessory = (UsbAccessory?)intent.GetParcelableExtra(UsbManager.ExtraAccessory);
                if (accessory == null) return;
                _aoaDevice?.Stop();
                _aoaDevice = DpAoaDevice.Open(this, accessory, out var error);
                if (_aoaDevice == null) Dg.Warn("dp-aoa", $"device open failed: {error}");
            }
            else if (intent.Action == UsbManager.ActionUsbDeviceAttached)
            {
                var device = (UsbDevice?)intent.GetParcelableExtra(UsbManager.ExtraDevice);
                if (device == null) return;
                _aoaHost?.Stop();
                _aoaHost = DpAoaHost.Open(this, device, out var error);
                if (_aoaHost == null) Dg.Log("dp-aoa", $"host open: {error}");   // "waiting for re-enumeration" is expected mid-handshake, not a failure
            }
        }
        catch (System.Exception ex) { Dg.Warn("dp-aoa", ex); }
    }

    public void FlushPendingOpen()
    {
        var open = _pendingOpen;
        _pendingOpen = null;
        if (!string.IsNullOrEmpty(open)) OpenPresenter(open!);
    }

    // Open a shell window by registry id — resolve-by-id through the Shell assembler, never a file
    // path (REGISTRY-SPEC §9). Retries briefly: page-finish can beat the Shell module's boot().
    public void OpenPresenter(string id)
    {
        var safe = System.Text.Json.JsonSerializer.Serialize(id);
        var js = "(function t(n){ if (window.Shell && window.Shell.open) window.Shell.open(" + safe +
                 "); else if (n > 0) setTimeout(function(){ t(n-1); }, 250); })(40)";
        EvaluateInWebView(js);
    }

    // Evaluate a JS expression in the shell WebView from native code (UI-thread marshalled, null-safe).
    // The single seam the renderer is driven through — callbacks to JS (permission results, deep links)
    // all funnel here so there is one place that talks to V8.
    public void EvaluateInWebView(string js) {
        RunOnUiThread(() => { try { _webView?.EvaluateJavascript(js, null); } catch (Exception ex) { Dg.Warn("main", ex); } });
    }

    // Runtime-permission request from the WebView (mic, etc.). getUserMedia in the WebView can only
    // succeed once the *app* holds the OS runtime grant, so the renderer asks the host to request it
    // at use-time. Synchronous return: true = already granted (caller proceeds now); false = a request
    // was dispatched and the result will arrive asynchronously at window.__onPermissionResult(name,bool)
    // (see OnRequestPermissionsResult). The renderer awaits that hook before calling getUserMedia.
    public const int PermissionRequestCode = 100;
    public bool RequestRuntimePermission(string permission) {
        try {
            if (CheckSelfPermission(permission) == Android.Content.PM.Permission.Granted) {
                NotifyPermissionResult(permission, true);   // already held — fire the hook for a uniform await path
                return true;
            }
            RunOnUiThread(() => { try { RequestPermissions(new[] { permission }, PermissionRequestCode); } catch (System.Exception ex) { Subsystem.Dg.Warn("perm", ex); } });
            return false;
        } catch (System.Exception ex) { Subsystem.Dg.Warn("perm", ex); return false; }
    }

    // The OS handed back a runtime-permission decision — relay each result to the WebView so an
    // awaiting getUserMedia (or any consumer) can proceed or degrade-and-record. Never silent.
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults) {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        try {
            for (int i = 0; i < permissions.Length; i++) {
                bool granted = i < grantResults.Length && grantResults[i] == Android.Content.PM.Permission.Granted;
                NotifyPermissionResult(permissions[i], granted);
            }
        } catch (System.Exception ex) { Subsystem.Dg.Warn("perm", ex); }
    }

    // Post a single permission decision to the renderer's await hook. JSON-encoded args so a permission
    // string can never break out of the call (the renderer holds no authority; the host quotes for it).
    private void NotifyPermissionResult(string permission, bool granted) {
        var name = System.Text.Json.JsonSerializer.Serialize(permission);
        var js = "if (window.__onPermissionResult) try { window.__onPermissionResult(" + name + ", " + (granted ? "true" : "false") + "); } catch (e) { console.warn(e); }";
        EvaluateInWebView(js);
    }

    // We own the system bars. Hide the status bar while Subsystem is open; a swipe from the top edge
    // brings it back transiently (the "pull-down"). Re-applied on focus regain (transient bars reset).
    // API 30+ (Razr+ is API 34); older OS = no-op.
    private void ApplyImmersive() {
        try {
            if (Build.VERSION.SdkInt < BuildVersionCodes.R) return;
            var c = Window?.InsetsController;
            if (c != null) {
                // Immersive at the TOP only: hide the STATUS bar (swipe-from-top reveals it transiently —
                // the pull-down). Do NOT hide the navigation/gesture bar: in sticky mode a hidden nav bar
                // consumes the bottom swipe-up to reveal itself, which STEALS the system Home gesture. We
                // keep the nav bar so the OS owns swipe-up (Home) / swipe-up-hold (Recents); the shell still
                // draws edge-to-edge beneath it (SetDecorFitsSystemWindows(false)).
                c.Hide(WindowInsets.Type.StatusBars());
                c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            // NOTE: ApplyGestureExclusion() (from OnWindowFocusChanged) excludes the LEFT/RIGHT edges from
            // the OS back-gesture so accidental edge swipes don't fire back — the taskbar + red-X are the
            // nav. (Android caps gesture exclusion at ~200dp/edge, so the lower part of each edge is what's
            // reliably covered; the back BUTTON / TerminalBackCallback still works for intentional back.)
        } catch (Exception ex) { Dg.Warn("main", ex); }
    }

    // System BACK (gesture or button) → navigate BACK inside the app (the shell's window history), not out.
    // The shell pushes a history entry per focused window, so GoBack() fires its popstate → focus the
    // previous window / close the active one. At the root: NOTHING — an edge swipe must never exit or
    // background the app (user directive: only the red-X leaves; the OS Home gesture still works).
    public void GoBackInApp() {
        RunOnUiThread(() => {
            try {
                if (_webView != null && _webView.CanGoBack()) _webView.GoBack();
            } catch (Exception ex) { Dg.Warn("main", ex); }
        });
    }

    // Own the LEFT/RIGHT edge swipes: exclude both vertical edges from Android's system back-gesture so
    // the WebView/Shell receives them instead of the OS treating them as "back". (API 29+.)
    private void ApplyGestureExclusion() {
        try {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Q) return;
            var dv = Window?.DecorView;
            if (dv == null || dv.Width == 0 || dv.Height == 0) return;
            int edge = (int)(40 * Resources!.DisplayMetrics!.Density);
            dv.SystemGestureExclusionRects = new System.Collections.Generic.List<Android.Graphics.Rect> {
                new Android.Graphics.Rect(0, 0, edge, dv.Height),
                new Android.Graphics.Rect(dv.Width - edge, 0, dv.Width, dv.Height),
            };
        } catch (Exception ex) { Dg.Warn("main", ex); }
    }

    // Native window blur-behind for the assist popup / system mica (API 31+; S/Razr+ are 34). Best-effort:
    // the OS honors it only when window blurs are enabled in dev settings AND the device supports them, so
    // a non-zero radius degrades to no-op rather than failing. radiusPx <= 0 clears it. Guarded on SdkInt.
    public void SetWindowBlur(int radiusPx) {
        RunOnUiThread(() => {
            try {
                if (Build.VERSION.SdkInt < BuildVersionCodes.S) return;
                Window?.SetBackgroundBlurRadius(radiusPx < 0 ? 0 : radiusPx);
            } catch (System.Exception ex) { Subsystem.Dg.Warn("blur", ex); }
        });
    }

    // JS-driven status-bar control (PwshBridge.setStatusBarHidden): lets the Shell show the bar on the
    // start/launcher and hide it inside a presenter, if it wants finer control than the always-immersive default.
    public void SetStatusBarHidden(bool hidden) {
        RunOnUiThread(() => {
            try {
                if (Build.VERSION.SdkInt < BuildVersionCodes.R) return;
                var c = Window?.InsetsController; if (c == null) return;
                if (hidden) c.Hide(WindowInsets.Type.StatusBars());
                else c.Show(WindowInsets.Type.StatusBars());
            } catch (Exception ex) { Dg.Warn("main", ex); }
        });
    }

    public override void OnWindowFocusChanged(bool hasFocus) {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) { ApplyImmersive(); ApplyGestureExclusion(); }   // re-assert bar + edge-gesture ownership on focus regain
    }

    public bool IsAccessibilityEnabled() {
        int accessibilityEnabled = 0;
        try { accessibilityEnabled = Android.Provider.Settings.Secure.GetInt(ContentResolver, Android.Provider.Settings.Secure.AccessibilityEnabled); } catch (Exception ex) { Dg.Warn("main", ex); }
        if (accessibilityEnabled == 1) {
            string? settingValue = Android.Provider.Settings.Secure.GetString(ContentResolver, Android.Provider.Settings.Secure.EnabledAccessibilityServices);
            if (settingValue != null && settingValue.Contains(PackageName!)) return true;
        }
        return false;
    }

    public void CreateSession(long tabId) {
        if (Sessions.ContainsKey(tabId)) return;
        var session = new TerminalSession(tabId, this);
        Sessions[tabId] = session;
        var owner = Vom.Vom.CreateOwner($@"\System\Terminal\Session\{tabId}");
        Vom.Vom.Spawn(owner, "Start", _ => session.Start(this.FilesDir!.AbsolutePath));
    }

    public void CloseSession(long tabId) {
        if (Sessions.TryRemove(tabId, out var session)) {
            session.Dispose();
        }
    }

    private void SeedAssets() {
        void SeedAsset(string assetName, string destPath) {
            if (!System.IO.File.Exists(destPath)) {
                // ObpHost: the shell tree is compiled into the assembly now (embedded -> asset fallback).
                try { using var s = ObpHost.OpenRead(assetName); if (s != null) { using var d = System.IO.File.Create(destPath); s.CopyTo(d); } } catch (Exception ex) { Dg.Warn("main", ex); }
            }
        }
        SeedAsset("shell/home/profile.ps1",  System.IO.Path.Combine(this.FilesDir!.AbsolutePath, "profile.ps1"));
        SeedAsset("shell/home/settings.ps1", System.IO.Path.Combine(this.FilesDir!.AbsolutePath, "settings.ps1"));
    }

    public void SendRawToReact(long tabId, byte[] rawAnsiBytes) {
        if (!IsReactReady) {
            if (Sessions.TryGetValue(tabId, out var s)) s.OutputQueue.Enqueue(rawAnsiBytes);
            return;
        }
        string text = Encoding.UTF8.GetString(rawAnsiBytes);
        RunOnUiThread(() => {
            _webView.PostWebMessage(new WebMessage($"{tabId}:{text}"), Android.Net.Uri.Parse("*")!);
        });
    }

    private Android.Media.Projection.MediaProjectionManager? _projectionManager;
    private Android.Media.Projection.MediaProjection? _mediaProjection;
    private Android.Hardware.Display.VirtualDisplay? _virtualDisplay;
    private Android.Media.ImageReader? _imageReader;

    [Export("startScreenCapture")] [JavascriptInterface]
    public void StartScreenCapture() {
        _projectionManager = (Android.Media.Projection.MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(_projectionManager.CreateScreenCaptureIntent(), 1000);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent? data) {
        if (requestCode == 1000 && resultCode == Result.Ok && data != null) {
            // Android 14+ requires a foreground service of type mediaProjection to be running
            // before MediaProjection.start(); ours is dataSync, so this can throw RemoteException.
            // Guard it so a failed screen-cast logs instead of hard-crashing the whole app.
            try {
                _mediaProjection = _projectionManager!.GetMediaProjection((int)resultCode, data);
                SetupVirtualDisplay();
            } catch (System.Exception ex) {
                Android.Util.Log.Error("Subsystem", "Screen capture unavailable (needs mediaProjection FGS on A14+): " + ex.Message);
            }
        }
        base.OnActivityResult(requestCode, resultCode, data);
    }

    private void SetupVirtualDisplay() {
        var metrics = Resources!.DisplayMetrics!;
        int width = metrics.WidthPixels;
        int height = metrics.HeightPixels;
        int density = (int)metrics.DensityDpi;
        
        _imageReader = Android.Media.ImageReader.NewInstance(width, height, (Android.Graphics.ImageFormatType)1, 2); // 1 = PixelFormat.RGBA_8888
        _imageReader.SetOnImageAvailableListener(new ImageAvailableListener(), null);

        _virtualDisplay = _mediaProjection!.CreateVirtualDisplay("ScreenCapture",
            width, height, density,
            (Android.Views.DisplayFlags)16, // VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR
            _imageReader.Surface, null, null);
    }

    public void RouteInputEvent(string json) {
        try {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeProp)) {
                string type = typeProp.GetString() ?? "";
                if (root.TryGetProperty("tabId", out var tabIdProp)) {
                    long tabId = tabIdProp.GetInt64();
                    if (type == "createSession") {
                        CreateSession(tabId);
                    }
                    else if (type == "input" || type == "resize" || type == "text") {
                        try {
                            var ev = System.Text.Json.JsonSerializer.Deserialize<ReactInputEvent>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (ev?.type == "resize" && Sessions.TryGetValue(tabId, out var sr)) { lock(sr.VtLock) { sr.VtController.ResizeView(ev.cols, ev.rows); sr.VtController.ClearChanges(); } }
                        } catch (Exception ex) { Subsystem.Dg.Warn("input", ex); }
                    }
                }
            }
        } catch (Exception ex) { Subsystem.Dg.Warn("input-route", ex); }
    }


    // Share text out of the device via the system chooser (ACTION_SEND).
    // Called by the Invoke-Share cmdlet which is dispatched through SubsystemDownloadListener.
    public void ShareText(string title, string text, string mime) {
        try {
            var send = new Android.Content.Intent(Android.Content.Intent.ActionSend);
            send.PutExtra(Android.Content.Intent.ExtraText, text);
            send.SetType(string.IsNullOrEmpty(mime) ? "text/plain" : mime);
            var chooser = Android.Content.Intent.CreateChooser(send, title ?? "Share");
            chooser!.AddFlags(Android.Content.ActivityFlags.NewTask);
            StartActivity(chooser);
        } catch (Exception ex) { Subsystem.Dg.Warn("share", ex); }
    }

    public void NotifyReactReady() {
        IsReactReady = true;
        foreach (var session in Sessions.Values) {
            while (session.OutputQueue.Count > 0) SendRawToReact(session.TabId, session.OutputQueue.Dequeue());
        }
    }

    // Reload the shell WebView — how a front-door swap (\Shell\FrontDoor) takes effect live.
    // Callable from JS (PwshBridge.reloadShell) and from the runspace (Invoke-ShellReload), so the
    // agent can switch doors herself: Register-Capability the new file, then reload.
    public void ReloadShell() {
        RunOnUiThread(() => { try { _webView?.Reload(); } catch (Exception ex) { Dg.Warn("main", ex); } });
    }

    // The shared TTS engine (built-in, offline — airplane-safe). Lazily initialized on first Speak so
    // a device with no TTS doesn't pay for it; Broker and Out-Speech share this one instance.
    private SpeechOutput? _speech;
    private readonly object _speechGate = new();
    public async void Speak(string text) {
        if (string.IsNullOrWhiteSpace(text)) return;
        SpeechOutput engine;
        lock (_speechGate) { _speech ??= new SpeechOutput(); engine = _speech; }
        try {
            if (!engine.Ready) await engine.InitAsync(this);
            engine.Speak(text);
        } catch (System.Exception ex) { Subsystem.Dg.Warn("tts", ex); }
    }

    public void StartProjection() { }
    public void StopProjection() { }
}

public class TerminalBackCallback : Java.Lang.Object, IOnBackInvokedCallback {
    private readonly MainActivity _activity;
    public TerminalBackCallback(MainActivity activity) { _activity = activity; }
    public void OnBackInvoked() {
        // Back = in-app navigation (shell window history), not "send ESC to the terminal". The shell owns
        // what "back" means per the active surface; at the root it drops to home (see GoBackInApp).
        _activity.GoBackInApp();
    }
}

// JS→host data channel.  download.js triggers a standard browser download with a data: URL;
// the WebView never opens a socket — this listener catches it in-process before anything hits
// the network stack.  No JNI, no custom scheme, no vom, no open ports.
public class SubsystemDownloadListener : Java.Lang.Object, Android.Webkit.IDownloadListener {
    public void OnDownloadStart(string url, string userAgent, string contentDisposition, string mimeType, long contentLength) {
        try {
            if (url.StartsWith("data:")) {
                int comma = url.IndexOf(',');
                if (comma < 0) return;
                string header = url.Substring(5, comma - 5);   // e.g. "text/plain;base64"
                string data   = url.Substring(comma + 1);
                bool isBase64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
                string text   = isBase64
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data))
                    : Uri.UnescapeDataString(data);
                // Hand the decoded text to the runspace as a share action.
                _ = Subsystem.SubsystemApi.ExecuteCommandAsJson($"Invoke-Share -Text '{text.Replace("'", "''")}'");
            }
        } catch (Exception ex) {
            Subsystem.Dg.Warn("download", ex);
        }
    }
}

public class SubsystemWebViewClient : WebViewClient {
    // The shell document is up — flush any deep-link the launching intent carried (open=agent from
    // the chat head). The flush is a no-op when nothing is pending.
    public override void OnPageFinished(WebView? view, string? url) {
        base.OnPageFinished(view, url);
        try { MainActivity.Instance?.FlushPendingOpen(); } catch (Exception ex) { Dg.Warn("main", ex); }
    }

    public static string MimeFor(string path) {
        if (path.EndsWith(".html") || path.EndsWith(".obp")) return "text/html";
        if (path.EndsWith(".js")) return "application/javascript";
        if (path.EndsWith(".css")) return "text/css";
        if (path.EndsWith(".png")) return "image/png";
        if (path.EndsWith(".svg")) return "image/svg+xml";
        if (path.EndsWith(".json")) return "application/json";
        return "application/octet-stream";
    }

    public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request) {
        var url = request?.Url;
        if (url == null) return null;
        
        string host = url.Host ?? "";
        if (host.Equals("shell", StringComparison.OrdinalIgnoreCase)) {
            string path = url.Path ?? "/";
            if (path == "/") path = "/shell.obp";

            try {
                string resourcePath = "shell" + path;
                var stream = ObpHost.OpenRead(resourcePath);
                if (stream != null) {
                    string mime = MimeFor(path);
                    return new WebResourceResponse(mime, "UTF-8", stream);
                }
            } catch (Exception ex) {
                Subsystem.Dg.Warn("webview-intercept", ex);
            }
            return new WebResourceResponse("text/plain", "UTF-8", new System.IO.MemoryStream());
        }

        return base.ShouldInterceptRequest(view, request);
    }

    // HARD RULE: V8 can NEVER worm its way online. Allow only the local app origin + benign
    // schemes; swallow everything else. This blocks CDN/phishing/exfiltration — the renderer has zero
    // authority AND zero reach (a leaf, offline-first).
    public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) {
        var url = request?.Url;
        if (url == null) return false;
        string scheme = (url.Scheme ?? "").ToLowerInvariant();
        string host   = (url.Host ?? "").ToLowerInvariant();
        bool localHttp = (scheme == "http" || scheme == "https") && (host == "127.0.0.1" || host == "localhost" || host == "shell");
        bool allowed = localHttp || scheme == "file" || scheme == "data" || scheme == "about" || scheme == "blob";
        if (allowed) return false;                              // let the WebView load it
        Subsystem.Dg.Log("v8", $"blocked off-origin navigation: {url}");
        return true;                                           // swallow — never go online
    }
}

public class ImageAvailableListener : Java.Lang.Object, Android.Media.ImageReader.IOnImageAvailableListener {
    public ImageAvailableListener() { }
    public void OnImageAvailable(Android.Media.ImageReader? reader) {
        try {
            using var image = reader?.AcquireLatestImage();
            if (image == null) return;
            var plane = image.GetPlanes()![0];
            var buffer = plane.Buffer!;
            int pixelStride = plane.PixelStride;
            int rowStride = plane.RowStride;
            int rowPadding = rowStride - pixelStride * image.Width;
            
            using var bitmap = Android.Graphics.Bitmap.CreateBitmap(image.Width + rowPadding / pixelStride, image.Height, Android.Graphics.Bitmap.Config.Argb8888!);
            bitmap.CopyPixelsFromBuffer(buffer);
            
            using var ms = new System.IO.MemoryStream();
            using var cropped = Android.Graphics.Bitmap.CreateBitmap(bitmap, 0, 0, image.Width, image.Height);
            cropped.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 30, ms);
            // ProjectionServer.cs deleted, broadcast is skipped
        } catch (Exception ex) { Dg.Warn("main", ex); }
    }
}

public class CustomWebChromeClient : WebChromeClient {
    // Constructor takes Context (not MainActivity) so this class has no hard coupling to the
    // activity. When MainActivity is retired, move this file to Host/ and wire to whatever
    // replaces the activity — the interop contract stays identical.
    private readonly Android.Content.Context _ctx;
    public CustomWebChromeClient(Android.Content.Context ctx) { _ctx = ctx; }

    // ss: prompt interception — the unified JS→host IPC channel.
    // JS calls: const result = prompt('ss:CmdletName -Param value')
    // Host intercepts, dispatches through SubsystemApi (same pipe as /api/exec), returns JSON.
    // Zero JNI, zero network, zero ACW stubs. Fully in-process.
    public override bool OnJsPrompt(WebView? view, string? url, string? message, string? defaultValue, JsPromptResult? result) {
        if (message != null && message.StartsWith("ss:")) {
            try {
                string cmd = message.Substring(3);
                string response = Subsystem.SubsystemApi.ExecuteCommandAsJson(cmd).GetAwaiter().GetResult();
                result!.Confirm(response);
            } catch (Exception ex) {
                Subsystem.Dg.Warn("webview", ex);
                result!.Confirm("{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
            }
            return true;
        }
        return base.OnJsPrompt(view, url, message, defaultValue, result);
    }

    // Media-capture grant for the shell. The WebView only ever loads the loopback origin
    // (SubsystemWebViewClient blocks every off-origin navigation), so a capture request can only
    // come from our own presenters — granting the mic here lets the chat's voice-in (getUserMedia)
    // work. The app already holds RECORD_AUDIO (manifest + install -g).
    public override void OnPermissionRequest(Android.Webkit.PermissionRequest? request) {
        try { request?.Grant(request.GetResources()); }
        catch (System.Exception ex) { Subsystem.Dg.Warn("webview", ex); }
    }

    public override bool OnJsConfirm(WebView? view, string? url, string? message, JsResult? result) {
        new AlertDialog.Builder(_ctx)
            .SetTitle("Subsystem")
            .SetMessage(message)
            .SetPositiveButton(Android.Resource.String.Ok, (s, e) => result!.Confirm())
            .SetNegativeButton(Android.Resource.String.Cancel, (s, e) => result!.Cancel())
            .SetCancelable(false)
            .Show();
        return true;
    }

    public override bool OnJsAlert(WebView? view, string? url, string? message, JsResult? result) {
        new AlertDialog.Builder(_ctx)
            .SetTitle("Subsystem")
            .SetMessage(message)
            .SetPositiveButton(Android.Resource.String.Ok, (s, e) => result!.Confirm())
            .SetCancelable(false)
            .Show();
        return true;
    }
}
