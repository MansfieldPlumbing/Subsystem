<#
.SYNOPSIS
  Populate WebView2Runtime\webview2-fixed.zip — the Fixed Version WebView2 engine that agent-browser embeds
  and self-extracts, so the published exe is self-sufficient (no system WebView2 needed).

.DESCRIPTION
  Microsoft gates the Fixed Version download behind a version picker (no stable URL), so you give this script
  EITHER a downloaded .cab (-Cab) OR a direct link to one (-Url). It expands the cab, trims the locales down to
  English (the big, safe win), optionally drops swiftshader, flattens so msedgewebview2.exe sits at the zip
  root, and writes WebView2Runtime\webview2-fixed.zip. Re-run after `dotnet publish`-ing to embed a new engine.

  Get the cab: https://developer.microsoft.com/en-us/microsoft-edge/webview2/  -> "Fixed Version" -> pick x64.

.EXAMPLE
  pwsh fetch-runtime.ps1 -Cab "$env:USERPROFILE\Downloads\Microsoft.WebView2.FixedVersionRuntime.x.x64.cab"
.EXAMPLE
  pwsh fetch-runtime.ps1 -Url "https://msedge.sf.dl.delivery.mp.microsoft.com/.../...x64.cab" -TrimSwiftshader
#>
param(
    [string]$Cab,
    [string]$Url,
    [switch]$TrimSwiftshader,        # off by default: swiftshader is the software-GL fallback (render safety)
    [string[]]$KeepLocales = @('en-US.pak','en-GB.pak')
)

$ErrorActionPreference = 'Stop'
$proj = $PSScriptRoot
$work = Join-Path $env:TEMP ("agentbrowser-rt-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
$expand = Join-Path $work 'expand'
$outDir = Join-Path $proj 'WebView2Runtime'
$outZip = Join-Path $outDir 'webview2-fixed.zip'
New-Item -ItemType Directory -Force -Path $work, $expand, $outDir | Out-Null

try {
    if (-not $Cab) {
        if (-not $Url) { throw "Provide -Cab <path-to-.cab> or -Url <direct-cab-link>. Get it from https://developer.microsoft.com/en-us/microsoft-edge/webview2/ (Fixed Version, x64)." }
        $Cab = Join-Path $work 'runtime.cab'
        Write-Host "[fetch] downloading $Url"
        Invoke-WebRequest -Uri $Url -OutFile $Cab
    }
    if (-not (Test-Path -LiteralPath $Cab)) { throw "cab not found: $Cab" }

    Write-Host "[fetch] expanding $([IO.Path]::GetFileName($Cab)) (this is the ~250 MB engine)..."
    & expand.exe $Cab -F:* $expand | Out-Null   # MS docs: use expand, NOT Explorer, for correct structure

    # Find the folder that actually holds msedgewebview2.exe (cab nests it under a versioned folder).
    $exe = Get-ChildItem -LiteralPath $expand -Recurse -Filter 'msedgewebview2.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $exe) { throw "msedgewebview2.exe not found under the expanded cab — wrong package?" }
    $runtime = $exe.Directory.FullName
    Write-Host "[fetch] engine root: $runtime"

    # --- TRIM (the path to ~100 MB shipped) ---
    # Locales: ~100 language .pak files; keep only English. The biggest, safest cut.
    Get-ChildItem -LiteralPath $runtime -Recurse -Directory -Filter 'Locales' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter '*.pak' | Where-Object { $KeepLocales -notcontains $_.Name } | Remove-Item -Force
    }
    if ($TrimSwiftshader) {
        Get-ChildItem -LiteralPath $runtime -Recurse -Directory -Filter 'swiftshader' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    }

    $rawMb = [Math]::Round((Get-ChildItem -LiteralPath $runtime -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "[fetch] trimmed engine on disk: $rawMb MB"

    # Flatten: zip CONTENTS of $runtime so msedgewebview2.exe is at the zip ROOT (Program.cs extracts flat).
    if (Test-Path -LiteralPath $outZip) { Remove-Item -LiteralPath $outZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($runtime, $outZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $zipMb = [Math]::Round((Get-Item -LiteralPath $outZip).Length / 1MB, 1)
    Write-Host "[fetch] DONE -> $outZip  ($zipMb MB embedded; ~+25-35 MB for the .NET runtime = shipped exe)"
    Write-Host "[fetch] now:  dotnet publish -c Release"
}
finally {
    try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } catch {}
}
