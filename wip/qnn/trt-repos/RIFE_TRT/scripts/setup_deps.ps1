#Requires -Version 7.5
param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary] $Manifest)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "  ── [3] Install Dependencies ────────────────────────────────────────────`n" -ForegroundColor Cyan
Write-Host "  Manual installs (links only):`n" -ForegroundColor DarkGray
foreach ($key in @('driver', 'cuda', 'tensorrt')) {
    $dep = $Manifest[$key]
    Write-Host "  $($dep.Label.PadRight(22)) $($dep.Url)" -ForegroundColor White
    if ($dep.Note) { Write-Host "  $(' ' * 22)   ↑ $($dep.Note)" -ForegroundColor DarkGray }
    Write-Host ""
}

Write-Host "  ── Install via winget ──────────────────────────────────────────────────`n" -ForegroundColor DarkGray
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Write-Host "  ❌ winget not found." -ForegroundColor Red; return }

foreach ($key in ($Manifest.Keys | Where-Object { $Manifest[$_].WingetId })) {
    $dep = $Manifest[$key]; $alreadyInstalled = $false
    switch ($key) {
        'ffmpeg' { $alreadyInstalled = [bool](Get-Command ffmpeg -ErrorAction SilentlyContinue) }
        'buildtools' { if (Test-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe") { $alreadyInstalled = [bool](& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 2>$null) } }
        'dotnet' { if (Get-Command dotnet -ErrorAction SilentlyContinue) { if ((dotnet --version 2>&1) -match '(\d+)\.' -and [int]$Matches[1] -ge $dep.MinVersion.Major) { $alreadyInstalled = $true } } }
    }
    $tag = if ($alreadyInstalled) { " (already installed)" } else { "" }
    Write-Host "  $($dep.Label)$tag" -ForegroundColor $(if ($alreadyInstalled) { 'Green' } else { 'White' })
    if (-not $alreadyInstalled) {
        $answer = Read-Host "  Install $($dep.Label) now? [Y/N]"
        if ($answer.Trim().ToUpper() -eq 'Y') {
            $cmd = "winget install --id $($dep.WingetId) --accept-package-agreements --accept-source-agreements"
            if ($dep.WingetArgs) { $cmd += " --override `"$($dep.WingetArgs)`"" }
            Invoke-Expression $cmd; Write-Host "  ✅ Done.`n" -ForegroundColor Green
        } else { Write-Host "  Skipped.`n" -ForegroundColor DarkGray }
    } else { Write-Host "" }
}
Write-Host "  After installing, run [2] Preflight to validate." -ForegroundColor Cyan
