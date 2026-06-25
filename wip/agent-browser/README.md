# askgoogle

A tiny WebView2 host for Windows with two modes:

1. **Headless one-shot (`--ask`)** — drives Google's AI Mode for a question, waits for the answer to settle,
   prints it to stdout, and exits. This is the agent-consumable mode: it is the Windows prototype of
   Subsystem's `Rd.Consult` ("phone a friend") escape hatch — a local model forwarding a question it can't
   answer to a frontier model over a real browser, no API key.
2. **Ambient widget (default)** — a translucent, always-on-top, drag-anywhere mini-browser pinned in the
   corner, display-only (draggable but click-through-disabled). Defaults to `google.com/ai`. ~1 MB; every
   knob is a CLI flag.

## Why it exists
The on-device agent needs a way to consult a frontier model for things it can't answer locally. `--ask`
provides that as a one-shot, no-API-key, browser-driven query. The widget mode is the same host left
visible — an ambient web pane that doesn't steal focus.

## Usage
```
askgoogle.exe                                   # 80% opaque, tiny, top-left, AI Mode, display-only
askgoogle.exe -o 0.6 -w 320 --height 220 -z 0.6 # bigger, more opaque, less zoomed-out
askgoogle.exe -u https://news.ycombinator.com --interactive   # any URL, clickable
```

| Flag | Alias | Default | Meaning |
|---|---|---|---|
| `--opacity` | `-o` | `0.80` | window opacity, 0.05–1.0 (clamped so it can't vanish) |
| `--width` | `-w` | `220` | width in px |
| `--height` | | `150` | height in px |
| `--x` / `--y` | | `12` / `12` | top-left position |
| `--zoom` | `-z` | `0.40` | webview zoom (0.4 = zoomed way out) |
| `--url` | `-u` | `google.com/ai` | page to load |
| `--interactive` | | off | allow clicking the page (default is display-only / drag-only) |

## Build — a single, self-sufficient ~100 MB exe
The goal is **one exe that runs on a bare Windows box with NO .NET and NO WebView2 installed.** Both are
bundled: the .NET runtime via self-contained single-file publish, and the WebView2 **Fixed Version** engine
embedded as a compressed zip that self-extracts once on first launch (see `CreateEnvironment`/
`ExtractEmbeddedRuntime` in Program.cs). Needs the .NET SDK (developed on .NET 11).

```
# 1. Populate the embedded engine (one-time / on version bump). Download the Fixed Version x64 cab from
#    https://developer.microsoft.com/en-us/microsoft-edge/webview2/  (the "Fixed Version" section), then:
pwsh fetch-runtime.ps1 -Cab "<path-to>\Microsoft.WebView2.FixedVersionRuntime.<ver>.x64.cab"
#    -> trims locales to English, flattens, writes WebView2Runtime\webview2-fixed.zip (~90-110 MB)

# 2. Publish the single self-sufficient exe (all publish props are pinned in the csproj):
dotnet publish -c Release
#    -> bin\Release\net11.0-windows\win-x64\publish\agent-browser.exe  (~100-130 MB, zero dependencies)
```

First launch extracts the engine to `%LOCALAPPDATA%\agent-browser\wv2-<n>\` (~250 MB cache, re-extracts on
version bump); the warm **profile** lives beside it at `…\profile\` and persists logins/cookies/history (the
precious, save-able / eventually-SQLite-backed state). On a clean checkout where the zip hasn't been fetched,
the build still works and falls back to the system Evergreen runtime — just not yet self-sufficient.

## Notes
- **WebView2 is bundled, not assumed.** It is **not** guaranteed on Windows (LTSC/Server/enterprise-stripped
  images ship without it, and it can be removed), so we ship the Fixed Version engine inside the exe. The
  engine is a multi-process Chromium (`msedgewebview2.exe` + `msedge.dll` + `*.pak` + `icudtl.dat`) that the
  loader `CreateProcess`-launches **by path** — it can't be a DLL or run from memory, so it materializes to a
  real folder once. After the first cold load, the read-only engine pages stay resident in the OS standby
  cache → effectively RAM-served with no RAM disk.
- **Why WinForms, not WPF:** `Form.Opacity` is a layered-window alpha that fades the *whole* window including
  the WebView2 child. WPF's `Opacity`/`AllowsTransparency` can't reach the WebView2 (it's an `HwndHost` DWM
  composites separately — "airspace"), so WPF leaves the web content opaque. WinForms is the correct host.

## License
MIT — see [LICENSE](LICENSE).
