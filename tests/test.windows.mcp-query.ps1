#requires -Version 7
# test.windows.mcp-query.ps1 — Proves e2b is interrogable over MCP (CRQ183): `ss mcp` exposes a `query`
# tool that lazily constructs ONE resident DpxDecoder (the multi-GB q4 load happens on the first call,
# reused for the process lifetime), drains one turn TO COMPLETION synchronously (completed-thought
# granularity, CRQ175 — no token firehose on the wire), and answers with completed text + a receipt line
# (token count, measured decode tok/s). Absent model dbs must yield a clean isError result, never a crash.
# Authority = the binary; this comment is not. Run via ss (the hosted runspace). Dot-source-safe.
#   Dogfood:  ss -File tests/test.windows.mcp-query.ps1
# NOTE: tok/s printed here is measured on a shared dev box while other builders run — PROVISIONAL.

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
# Drain stderr concurrently (breadcrumbs + decoder chatter) so a full pipe buffer can never deadlock the server.
$errTask = $p.StandardError.ReadToEndAsync()

$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}')
$initLine = $p.StandardOutput.ReadLine()

$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
$listLine = $p.StandardOutput.ReadLine()

# The interrogation: one call, one completed reply. First call carries the resident model load, so this
# line can take minutes on a cold cache — ReadLine blocks until the completed turn comes back.
$swCall = [System.Diagnostics.Stopwatch]::StartNew()
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"query","arguments":{"prompt":"What is the capital of France?","max_tokens":16}}}')
$callLine = $p.StandardOutput.ReadLine()
$swCall.Stop()

$p.StandardInput.Close()
$restOut = $p.StandardOutput.ReadToEnd()
if (-not $p.WaitForExit(30000)) { $p.Kill($true); $fails.Add('server did not exit within 30s of EOF') }
$errText = $errTask.GetAwaiter().GetResult()
$exit = $p.ExitCode

$initOk = $false; $queryListed = $false; $schemaOk = $false
try { $j1 = $initLine | ConvertFrom-Json; $initOk = ($j1.id -eq 1 -and $null -ne $j1.result) } catch {}
$toolNames = @()
try {
    $j2 = $listLine | ConvertFrom-Json
    $toolNames = @($j2.result.tools.name)
    $q = $j2.result.tools | Where-Object name -eq 'query'
    $queryListed = ($null -ne $q)
    $schemaOk = ($q.inputSchema.type -eq 'object' -and $null -ne $q.inputSchema.properties.prompt -and $null -ne $q.inputSchema.properties.max_tokens -and $q.inputSchema.required -contains 'prompt')
} catch {}

$callOk = $false; $text = ''; $isError = $true
try {
    $j3 = $callLine | ConvertFrom-Json
    $callOk = ($j3.id -eq 3 -and $null -ne $j3.result)
    $isError = [bool]$j3.result.isError
    $text = [string]$j3.result.content[0].text
} catch {}

# Split the completed text from the receipt line the tool appends. Content is NOT asserted (a 2B q4 may
# ramble) — only that a non-empty completed reply came back in one piece.
$receiptLine = ($text -split "`n" | Where-Object { $_ -match '^\[query\] tokens=' } | Select-Object -First 1)
$completed = ($text -replace '(?s)\n\n\[query\] tokens=.*$', '').Trim()
$tokens = -1; $tokSec = -1.0
if ($receiptLine -match 'tokens=(\d+)') { $tokens = [int]$Matches[1] }
if ($receiptLine -match 'decode_tok_s=([\d.]+)') { $tokSec = [double]$Matches[1] }

$stdoutPure = $true
foreach ($l in @($initLine, $listLine, $callLine) + ($restOut -split "`n")) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    try { $null = $l | ConvertFrom-Json } catch { $stdoutPure = $false }
}

Write-Host "tools listed: $($toolNames -join ', ')"
Write-Host "completed text ($($completed.Length) chars): $completed"
Write-Host "receipt: $receiptLine"
Write-Host ("query call wall time: {0:F0} ms (includes the one-time resident model load)" -f $swCall.Elapsed.TotalMilliseconds)
Assert $initOk       'initialize answered with JSON-RPC id=1'
Assert $queryListed  'query appears in tools/list'
Assert $schemaOk     'query carries a JSON schema (object; prompt required; max_tokens integer)'
Assert $callOk       'tools/call query answered with JSON-RPC id=3'
Assert (-not $isError) 'query result is not isError (model dbs discovered, decode completed)'
Assert ($completed.Length -gt 0) 'completed text is non-empty (content deliberately not asserted)'
Assert ($tokens -ge 1 -and $tokens -le 16) "token count in receipt within max_tokens=16 — was $tokens"
Assert ($tokSec -gt 0) "measured decode tok/s present in receipt — $tokSec tok/s (PROVISIONAL: shared box)"
Assert $stdoutPure   'every stdout line parses as JSON (query never poisoned the wire)'
Assert ($errText.Contains('[ss mcp] query: loading the resident decoder')) 'stderr carries the one-time resident load breadcrumb'
Assert ($exit -eq 0) "EOF exits 0 with the resident decoder loaded — was $exit"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — e2b is interrogable over MCP: one call, one completed reply, $tokens tokens at $tokSec tok/s (PROVISIONAL)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.windows.mcp-query'; pass=$pass; tokens=$tokens; decodeTokSec=$tokSec; callMs=[math]::Round($swCall.Elapsed.TotalMilliseconds); verdict=$(if($pass){'query = resident decoder, completed-thought turns, measured receipts'}else{'see failures'}) }
