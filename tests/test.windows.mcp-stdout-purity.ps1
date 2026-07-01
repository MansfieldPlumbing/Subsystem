#requires -Version 7
# test.windows.mcp-stdout-purity.ps1 — Proves the MCP protocol channel cannot be poisoned (CRQ188 rung 1):
# in `ss mcp` stdio mode every stdout line is JSON-RPC (stray console writes are stderr-routed), lifecycle
# breadcrumbs land on stderr where the client logs them, EOF exits 0 (a clean disconnect is never a crash
# exit), and the courier announce key (HKCU:\Software\Subsystem\Mcp\<pid>) exists while serving, gone after.
# Authority = the binary; this comment is not. Run via ss (the hosted runspace). Dot-source-safe.
#   Dogfood:  ss -File tests/test.windows.mcp-stdout-purity.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$psi = [System.Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
$psi.Arguments = 'mcp'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
$serverPid = $p.Id

$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}')
$initLine = $p.StandardOutput.ReadLine()

$announceLive = Test-Path "HKCU:\Software\Subsystem\Mcp\$serverPid"

# The poison probe: a raw [Console]::WriteLine from INSIDE the runspace bypasses the tool-capture seam and,
# unpatched, lands mid-stream on the JSON-RPC wire. Patched, it must surface on stderr only.
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ss_run","arguments":{"command":"[Console]::WriteLine(''stray-console-write''); 42"}}}')
$callLine = $p.StandardOutput.ReadLine()

$p.StandardInput.Close()
$restOut = $p.StandardOutput.ReadToEnd()
$errText = $p.StandardError.ReadToEnd()
if (-not $p.WaitForExit(15000)) { $p.Kill($true); $fails.Add('server did not exit within 15s of EOF') }
$exit = $p.ExitCode
$announceGone = -not (Test-Path "HKCU:\Software\Subsystem\Mcp\$serverPid")

$initOk = $false; $callOk = $false
try { $j1 = $initLine | ConvertFrom-Json; $initOk = ($j1.id -eq 1 -and $null -ne $j1.result) } catch {}
try { $j2 = $callLine | ConvertFrom-Json; $callOk = ($j2.id -eq 2 -and ($j2.result.content[0].text -match '42')) } catch {}
$stdoutPure = $true
foreach ($l in @($initLine, $callLine) + ($restOut -split "`n")) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    try { $null = $l | ConvertFrom-Json } catch { $stdoutPure = $false }
}

Write-Host "server pid=$serverPid exit=$exit ; stdout lines pure JSON: $stdoutPure ; announce live/gone: $announceLive/$announceGone"
Assert $initOk        'initialize answered with JSON-RPC id=1'
Assert $callOk        'tools/call ss_run answered with JSON-RPC id=2 carrying the command result (42)'
Assert (-not ($initLine + $callLine + $restOut).Contains('stray-console-write')) 'the poison write never reached stdout'
Assert ($errText.Contains('stray-console-write')) 'the poison write surfaced on stderr instead'
Assert $stdoutPure    'every stdout line parses as JSON (the wire cannot be poisoned)'
Assert ($errText.Contains('[ss mcp] serving pid=')) 'stderr carries the serving breadcrumb (client log gets a cause line)'
Assert ($errText.Contains('stdin EOF'))             'stderr carries the clean-shutdown breadcrumb'
Assert ($exit -eq 0)  "EOF exits 0 (clean disconnect is never a crash exit) — was $exit"
Assert $announceLive  'courier announce key existed while serving (HKCU:\Software\Subsystem\Mcp\<pid>)'
Assert $announceGone  'courier announce key removed on clean exit'

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — the MCP wire is pure, exits are clean, and the courier announces itself."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.windows.mcp-stdout-purity'; pass=$pass; exit=$exit; stdoutPure=$stdoutPure; verdict=$(if($pass){'protocol channel invariant: JSON-only stdout, stderr breadcrumbs, exit 0, announce lifecycle'}else{'see failures'}) }
