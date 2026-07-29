using System;
using System.Linq;
using System.Management.Automation;
using System.Text.Json;
using Subsystem.Vom;

namespace Subsystem.Pwsh.Cmdlets;



// The Wp control plane — the system wallpaper is driven through the registry, never a private
// setting: \Shell\SystemWallpaper names the active Shader playlist; \Capability\Shader\* is the
// catalog (the SAME records every picker renders). Set bumps WpService's change generation so
// every live engine re-resolves on its next frame — no restart, no rebind.

[Cmdlet(VerbsCommon.Get, "SystemWallpaper")]
public sealed class GetSystemWallpaperCmdlet : WrapperCmdlet
{
    protected override void ProcessRecord()
    {
        string playlist = "";   // OFF by default: no shader until the owner opts in via Set-SystemWallpaper (\Shell\SystemWallpaper). Silence => the static Mica backdrop, zero GPU.
        try
        {
            var pref = Subsystem.Cm.Cm.Get("\\Shell\\SystemWallpaper");
            if (pref?.ManifestJson != null)
            {
                using var doc = JsonDocument.Parse(pref.ManifestJson);
                if (doc.RootElement.TryGetProperty("playlist", out var plv) && plv.ValueKind == JsonValueKind.String)
                    playlist = plv.GetString() ?? playlist;
            }
        }
        catch (Exception ex) { Dg.Warn("wallpaper", ex); }
        var shaders = Subsystem.Cm.Cm.List()
            .Where(r => r.Type == "Shader" && r.Enabled)
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var available = shaders.Select(r => r.Name).ToArray();
        var ids = shaders.Select(r => r.Path.Substring(r.Path.LastIndexOf('\\') + 1)).ToArray();
        
        var results = new System.Collections.Generic.List<WallpaperRecord>();
        for (int i = 0; i < shaders.Length; i++)
        {
            var id = ids[i];
            var name = available[i];
            var active = string.Equals(id, playlist, StringComparison.OrdinalIgnoreCase);
            results.Add(new WallpaperRecord(id, name, active));
        }
        Emit(results.ToArray());
    }
}

[Cmdlet(VerbsCommon.Set, "SystemWallpaper")]
public sealed class SetSystemWallpaperCmdlet : WrapperCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Playlist { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var id = Playlist.Trim();
        if (Subsystem.Cm.Cm.Get("\\Capability\\Shader\\" + id) == null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException($"No shader playlist '{id}' in \\Capability\\Shader (try Get-SystemWallpaper)."),
                "ShaderNotFound", ErrorCategory.ObjectNotFound, id));
            return;
        }
        var result = Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
        {
            Path = "\\Shell\\SystemWallpaper",
            Name = "SystemWallpaper",
            Type = "ShellObject",
            Integrity = "User",
            StartType = "auto",
            Enabled = true,
            ManifestJson = JsonSerializer.Serialize(new { version = 1, id = "system-wallpaper", playlist = id }),
        });
        Subsystem.WpService.NotifyChanged();   // live engines re-resolve on their next frame
        Emit(result);
    }
}

// Hands the user the system's own confirm UI with Wp preselected (apps cannot self-apply a live
// wallpaper — SET_WALLPAPER_COMPONENT is signature-level; the chooser IS the consent gate).
[Cmdlet(VerbsLifecycle.Start, "SystemWallpaperPicker")]
public sealed class StartSystemWallpaperPickerCmdlet : WrapperCmdlet
{
    protected override void ProcessRecord()
    {
        try
        {
            var ctx = (Android.Content.Context?)Subsystem.MainActivity.Instance ?? Android.App.Application.Context;
            var intent = new Android.Content.Intent(Android.App.WallpaperManager.ActionChangeLiveWallpaper);
            intent.PutExtra(Android.App.WallpaperManager.ExtraLiveWallpaperComponent,
                new Android.Content.ComponentName(ctx.PackageName!, "dev.mansfieldplumbing.subsystem.WpService"));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(intent);
            Emit(true);
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "WallpaperPickerFailed", ErrorCategory.InvalidOperation, null));
        }
    }
}
