# Drive agent-browser over ONE warm MCP connection and DOCUMENT findings (no fixes). Scenarios exercise
# back-and-forth with AI sites + browsing bot-walled sites. Telemetry (time/location) is added by the driver
# since the exe's HUD lacks it (a documented gap). Logs to _convo-<scenario>.log.
param([string]$Scenario = 'google')

$exe = 'S:\agent-browser-project\bin\Release\net11.0-windows\win-x64\agent-browser.exe'
$log = "S:\agent-browser-project\_convo-$Scenario.log"
Remove-Item $log -ErrorAction SilentlyContinue
function Log($m) { $m | Tee-Object -FilePath $log -Append }

# --- device telemetry (same as the firefox HUD) ---
$loc = '(unknown)'
try { $r = Invoke-RestMethod 'http://ip-api.com/json/?fields=status,country,regionName,city,lat,lon' -TimeoutSec 3; if ($r.status -eq 'success') { $loc = "$($r.city), $($r.regionName), $($r.country) [$($r.lat), $($r.lon)]" } } catch {}
Log "[DEVICE] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $((Get-Date).DayOfWeek) ($([System.TimeZoneInfo]::Local.Id)) @ $loc"

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
$sw = $p.StandardInput; $sr = $p.StandardOutput
$script:id = 0
function Call($method, $params) { $script:id++; $req = @{ jsonrpc = '2.0'; id = $script:id; method = $method }; if ($params) { $req.params = $params }; $sw.WriteLine(($req | ConvertTo-Json -Compress -Depth 12)); $sw.Flush(); $sr.ReadLine() }
function Notify($method) { $sw.WriteLine((@{ jsonrpc = '2.0'; method = $method } | ConvertTo-Json -Compress)); $sw.Flush() }
function Tool($name, $a) { $r = Call 'tools/call' @{ name = $name; arguments = $a }; try { ($r | ConvertFrom-Json).result.content[0].text } catch { "RAW: $r" } }
function FindInput($hud) {
    foreach ($l in ($hud -split "`n")) { if ($l -match '^\[(\d+)\]\s+(textarea|input)') { return [int]$matches[1] } }
    foreach ($l in ($hud -split "`n")) { if ($l -match '^\[(\d+)\].*(Ask anything|Message|Search|Ask|Chat with|Send a message|Compose|Write)') { return [int]$matches[1] } }
    return -1
}
function PollAnswer($maxTries = 8) {
    $last = ''
    for ($i = 0; $i -lt $maxTries; $i++) {
        Start-Sleep -Seconds 3
        $h = Tool 'browse_hud' @{}
        $txt = ($h -split '\[ELEMENTS\]')[0]
        if ($txt -notmatch '(Searching|Thinking|Generating|…)\s*$' -and $txt.Length -gt 0 -and $txt -eq $last) { return $h }  # stable
        $last = $txt
    }
    return $h
}

Call 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'convo'; version = '1' } } | Out-Null
Notify 'notifications/initialized'

switch ($Scenario) {
    'google' {
        Log "`n========== GOTO google.com/ai =========="; $h = Tool 'browse_goto' @{ url = 'google.com/ai' }; Log $h
        $inp = FindInput $h
        Log "`n---------- TURN 1: type [$inp] ----------"; $null = Tool 'browse_type' @{ id = $inp; text = 'What year did the Voyager 1 probe launch? Answer in one sentence.' }
        Log (PollAnswer)
        $h = Tool 'browse_hud' @{}; $inp = FindInput $h
        Log "`n---------- TURN 2 (follow-up): type [$inp] ----------"; $null = Tool 'browse_type' @{ id = $inp; text = 'And where is its twin Voyager 2 now?' }
        Log (PollAnswer)
    }
    'chatgpt' {
        Log "`n========== GOTO chatgpt.com =========="; $h = Tool 'browse_goto' @{ url = 'chatgpt.com' }; Log $h
        $inp = FindInput $h
        Log "`n[found input] = $inp"
        if ($inp -ge 0) {
            Log "`n---------- TURN 1: type [$inp] ----------"; $null = Tool 'browse_type' @{ id = $inp; text = 'In one sentence, what is a GGUF file?' }
            Log (PollAnswer)
        }
    }
    'arxiv'  { Log "`n========== GOTO arxiv =========="; Log (Tool 'browse_goto' @{ url = 'arxiv.org/list/cs.AI/recent' }) }
    'reddit' { Log "`n========== GOTO reddit =========="; Log (Tool 'browse_goto' @{ url = 'reddit.com' }) }
}

$sw.Close(); $p.WaitForExit(8000) | Out-Null
Log "`n[done — NOTE: session closes here because stdin EOF triggers Shutdown(); documented friction]"
