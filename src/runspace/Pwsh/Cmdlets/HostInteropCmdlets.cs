using System;
using System.Linq;
using System.Management.Automation;
using System.Text.Json;

namespace Subsystem.Pwsh.Cmdlets
{
    // Exit-App — unified application shutdown
    [Cmdlet("Exit", "App")]
    public sealed class ExitAppCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.RunOnUiThread(() => {
                MainActivity.Instance.FinishAffinity();
                Java.Lang.JavaSystem.Exit(0);
            });
#else
            System.Windows.Forms.Application.Exit();
            Environment.Exit(0);
#endif
        }
    }

    // Restart-Shell — reload the WebView presenter
    [Cmdlet("Restart", "Shell")]
    public sealed class RestartShellCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.ReloadShell();
#endif
        }
    }

    // Set-ShellReady — signals to host that React UI is up
    [Cmdlet("Set", "ShellReady")]
    public sealed class SetShellReadyCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.NotifyReactReady();
#endif
        }
    }

    // Open-TerminalSession — create new terminal session
    [Cmdlet("Open", "TerminalSession")]
    public sealed class OpenTerminalSessionCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public long TabId { get; set; }

        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.CreateSession(TabId);
#endif
        }
    }

    // Close-TerminalSession — close terminal session
    [Cmdlet("Close", "TerminalSession")]
    public sealed class CloseTerminalSessionCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public long TabId { get; set; }

        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.CloseSession(TabId);
#endif
        }
    }

    // Invoke-Share — share text/payload outward
    [Cmdlet("Invoke", "Share")]
    public sealed class InvokeShareCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Text { get; set; } = "";

        [Parameter]
        public string Title { get; set; } = "Share";

        [Parameter]
        public string Mime { get; set; } = "text/plain";

        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.ShareText(Title, Text, Mime);
#else
            // On Windows, copy to clipboard
            try
            {
                System.Windows.Forms.Clipboard.SetText(Text);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "ClipboardError", ErrorCategory.WriteError, Text));
            }
#endif
        }
    }

    // Set-ChatHead — show/hide chat head bubble
    [Cmdlet("Set", "ChatHead")]
    public sealed class SetChatHeadCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true)]
        public bool Show { get; set; }

        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) return;
            try
            {
                if (Show)
                {
                    if (!Android.Provider.Settings.CanDrawOverlays(MainActivity.Instance))
                    {
                        var intent = new Android.Content.Intent(
                            Android.Provider.Settings.ActionManageOverlayPermission,
                            Android.Net.Uri.Parse("package:" + MainActivity.Instance.PackageName));
                        MainActivity.Instance.StartActivity(intent);
                        return;
                    }
                    var i = new Android.Content.Intent(MainActivity.Instance, typeof(SubsystemService));
                    i.SetAction(SubsystemService.ActionShowBubble);
                    MainActivity.Instance.StartService(i);
                }
                else
                {
                    var i = new Android.Content.Intent(MainActivity.Instance, typeof(SubsystemService));
                    i.SetAction(SubsystemService.ActionHideBubble);
                    MainActivity.Instance.StartService(i);
                }
            }
            catch (Exception ex) { Dg.Log("bridge", "Set-ChatHead failed: " + ex.Message); }
#endif
        }
    }

    // Minimize-App — move application to back
    [Cmdlet("Minimize", "App")]
    public sealed class MinimizeAppCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.MoveTaskToBack(true);
#endif
        }
    }

    // Set-StatusBar — control status bar visibility
    [Cmdlet("Set", "StatusBar")]
    public sealed class SetStatusBarCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true)]
        public bool Hidden { get; set; }

        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.SetStatusBarHidden(Hidden);
#endif
        }
    }

    // Set-WindowBlur — set window blur radius
    [Cmdlet("Set", "WindowBlur")]
    public sealed class SetWindowBlurCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public int Radius { get; set; }

        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.SetWindowBlur(Radius);
#endif
        }
    }

    // Start-Projection / Stop-Projection
    [Cmdlet("Start", "Projection")]
    public sealed class StartProjectionCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.StartProjection();
#endif
        }
    }

    [Cmdlet("Stop", "Projection")]
    public sealed class StopProjectionCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            MainActivity.Instance?.StopProjection();
#endif
        }
    }

    // Get-Permission / Request-Permission / Revoke-Permission
    [Cmdlet("Get", "Permission")]
    public sealed class GetPermissionCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Permission { get; set; } = "";

        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) { WriteObject(false); return; }
            WriteObject(MainActivity.Instance.CheckSelfPermission(Permission) == Android.Content.PM.Permission.Granted);
#else
            WriteObject(true);
#endif
        }
    }

    [Cmdlet("Request", "Permission")]
    public sealed class RequestPermissionCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Permission { get; set; } = "";

        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) { WriteObject(false); return; }
            WriteObject(MainActivity.Instance.RequestRuntimePermission(Permission));
#else
            WriteObject(true);
#endif
        }
    }

    [Cmdlet("Revoke", "Permission")]
    public sealed class RevokePermissionCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Permission { get; set; } = "";

        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) { WriteObject(false); return; }
            try
            {
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
                {
                    MainActivity.Instance.RevokeSelfPermissionOnKill(Permission);
                    WriteObject(true);
                    return;
                }
            }
            catch (Exception ex) { Dg.Log("bridge", "RevokePermission: " + ex.Message); }
            WriteObject(false);
#else
            WriteObject(true);
#endif
        }
    }

    // Get-AllFilesAccess / Request-AllFilesAccess
    [Cmdlet("Get", "AllFilesAccess")]
    public sealed class GetAllFilesAccessCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            try { WriteObject(Android.OS.Environment.IsExternalStorageManager); } catch { WriteObject(false); }
#else
            WriteObject(true);
#endif
        }
    }

    [Cmdlet("Request", "AllFilesAccess")]
    public sealed class RequestAllFilesAccessCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) return;
            try
            {
                var uri = Android.Net.Uri.Parse("package:" + MainActivity.Instance.PackageName);
                var intent = new Android.Content.Intent(
                    Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, uri);
                MainActivity.Instance.StartActivity(intent);
            }
            catch (Exception ex) { Dg.Log("bridge", "RequestAllFilesAccess: " + ex.Message); }
#endif
        }
    }

    // Open-AccessibilitySettings / Get-AccessibilityStatus
    [Cmdlet("Open", "AccessibilitySettings")]
    public sealed class OpenAccessibilitySettingsCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) return;
            MainActivity.Instance.StartActivity(new Android.Content.Intent(Android.Provider.Settings.ActionAccessibilitySettings));
#endif
        }
    }

    [Cmdlet("Get", "AccessibilityStatus")]
    public sealed class GetAccessibilityStatusCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) { WriteObject(false); return; }
            WriteObject(MainActivity.Instance.IsAccessibilityEnabled());
#else
            WriteObject(false);
#endif
        }
    }

    // Open-AppSettings
    [Cmdlet("Open", "AppSettings")]
    public sealed class OpenAppSettingsCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) return;
            try
            {
                var uri = Android.Net.Uri.Parse("package:" + MainActivity.Instance.PackageName);
                MainActivity.Instance.StartActivity(new Android.Content.Intent(
                    Android.Provider.Settings.ActionApplicationDetailsSettings, uri));
            }
            catch (Exception ex) { Dg.Log("bridge", "OpenAppSettings: " + ex.Message); }
#endif
        }
    }

    // Get-AutoListen / Set-AutoListen
    [Cmdlet("Get", "AutoListen")]
    public sealed class GetAutoListenCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) { WriteObject(false); return; }
            WriteObject(AgentSettings.AutoListenAssist(MainActivity.Instance));
#else
            WriteObject(false);
#endif
        }
    }

    [Cmdlet("Set", "AutoListen")]
    public sealed class SetAutoListenCmdlet : PSCmdlet
    {
        [Parameter(Mandatory = true)]
        public bool Enable { get; set; }

        protected override void ProcessRecord()
        {
#if ANDROID
            if (MainActivity.Instance == null) return;
            AgentSettings.SetAutoListenAssist(MainActivity.Instance, Enable);
#endif
        }
    }

    // Get-ObpScripts
    [Cmdlet("Get", "ObpScripts")]
    public sealed class GetObpScriptsCmdlet : PSCmdlet
    {
        protected override void ProcessRecord()
        {
            try
            {
                var files = ObpHost.Enumerate("shell/scripts")
                    .Select(p => p.Substring(p.LastIndexOf('/') + 1)).ToArray();
                WriteObject(files);
            }
            catch { WriteObject(Array.Empty<string>()); }
        }
    }
}
