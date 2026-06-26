# Prove: (1) bridge answers initialize/tools-list, spawns daemon on first browse; (2) daemon + warm browser
# SURVIVE bridge disconnect; (3) a fresh bridge REATTACHES to the same session; (4) the new graph HUD.
$exe = 'S:\agent-browser-project\bin\Release\net11.0-windows\win-x64\agent-browser.exe'

function Bridge([object[]]$calls) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $resps = @()
    foreach ($c in $calls) {
        $p.StandardInput.WriteLine(($c | ConvertTo-Json -Compress -Depth 12)); $p.StandardInput.Flush()
        if ($c.id) { $resps += $p.StandardOutput.ReadLine() }
    }
    $p.StandardInput.Close(); $p.WaitForExit(8000) | Out-Null
    return $resps
}
function HudOf($resp) { try { ($resp | ConvertFrom-Json).result.content[0].text } catch { "RAW: $resp" } }
$init = @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 't'; version = '1' } } }
$inited = @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

"=== RUN 1 (bridge #1): initialize + browse_goto google.com/ai, then DISCONNECT ==="
$r1 = Bridge @($init, $inited, @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'browse_goto'; arguments = @{ url = 'google.com/ai' } } })
HudOf $r1[1]

"`n=== bridge #1 disconnected — daemon should persist ==="
Start-Sleep -Seconds 2
"agent-browser processes alive: $((Get-Process agent-browser -ErrorAction SilentlyContinue | Measure-Object).Count)  (expect 1 = the daemon)"

"`n=== RUN 2 (FRESH bridge #2): browse_hud — should REATTACH to the same warm page ==="
$r2 = Bridge @($init, $inited, @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'browse_hud'; arguments = @{} } })
HudOf $r2[1]
