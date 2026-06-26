# Drive agent-browser over ONE persistent MCP connection (session stays warm across calls; only closes when
# we close stdin at the very end). Logs every step to _drive.log so nothing is lost if a call blocks.
param([string]$Url = 'google.com/ai')
$ErrorActionPreference = 'Continue'
$log = 'S:\agent-browser-project\_drive.log'
Remove-Item $log -ErrorAction SilentlyContinue
function Log($m) { $m | Tee-Object -FilePath $log -Append }

$exe = 'S:\agent-browser-project\bin\Release\net11.0-windows\win-x64\agent-browser.exe'
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false           # stderr inherits -> no pipe-deadlock, diagnostics show in our output
$p = [System.Diagnostics.Process]::Start($psi)
$sw = $p.StandardInput; $sr = $p.StandardOutput

function Call($id, $method, $params) {
    $req = @{ jsonrpc = '2.0'; id = $id; method = $method }
    if ($params) { $req.params = $params }
    $sw.WriteLine(($req | ConvertTo-Json -Compress -Depth 12)); $sw.Flush()
    return $sr.ReadLine()
}
function Notify($method) { $sw.WriteLine((@{ jsonrpc = '2.0'; method = $method } | ConvertTo-Json -Compress)); $sw.Flush() }
function Tool($id, $name, $a) {
    $r = Call $id 'tools/call' @{ name = $name; arguments = $a }
    try { ($r | ConvertFrom-Json).result.content[0].text } catch { "RAW: $r" }
}
function FindInput($hud) {
    foreach ($l in ($hud -split "`n")) {
        if ($l -match '^\[(\d+)\]\s+(textarea|input)' ) { return [int]$matches[1] }
    }
    foreach ($l in ($hud -split "`n")) {
        if ($l -match '^\[(\d+)\].*(Ask anything|Ask|Search|Message)') { return [int]$matches[1] }
    }
    return -1
}

Log "init: $(Call 1 'initialize' @{ protocolVersion='2024-11-05'; capabilities=@{}; clientInfo=@{ name='drive'; version='1' } })"
Notify 'notifications/initialized'

Log "`n=== browse_goto $Url ==="
$h1 = Tool 2 'browse_goto' @{ url = $Url }
Log $h1

$inp = FindInput $h1
Log "`n[input element] = $inp"
if ($inp -ge 0) {
    Log "`n=== browse_type [$inp] Q1 ==="
    $h2 = Tool 3 'browse_type' @{ id = $inp; text = 'What year did the Voyager 1 probe launch? Answer in one sentence.' }
    Log $h2
}

$sw.Close()
$p.WaitForExit(8000) | Out-Null
Log "`n[done]"
