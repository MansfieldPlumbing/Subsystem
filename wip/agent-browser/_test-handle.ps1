# Prove the architecture: daemon publishes a CDP HANDLE to disk; the agent reads it and reaches the page via CDP.
$exe = 'S:\agent-browser-project\bin\Release\net11.0-windows\win-x64\agent-browser.exe'
Start-Process -FilePath $exe -ArgumentList '--daemon' -RedirectStandardError 'S:\agent-browser-project\_daemon.err' -WindowStyle Minimized | Out-Null

# connect the (vestigial) pipe just to TRIGGER a browse so the engine + handle come up
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'agent-browser', 'InOut')
$pipe.Connect(8000)
$r = New-Object System.IO.StreamReader($pipe); $w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
function Send($o) { $w.WriteLine(($o | ConvertTo-Json -Compress -Depth 12)) }
Send @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; capabilities=@{}; clientInfo=@{ name='t'; version='1' } } }; $null = $r.ReadLine()
Send @{ jsonrpc='2.0'; id=2; method='tools/call'; params=@{ name='browse_goto'; arguments=@{ url='example.com' } } }
$goto = $r.ReadLine()
"=== browse_goto returned (deadlocks fixed if this prints) ==="
try { ($goto | ConvertFrom-Json).result.content[0].text } catch { "RAW: $goto" }

"`n=== HANDLE ON DISK (session.json) ==="
$hp = "$env:LOCALAPPDATA\agent-browser\session.json"
if (Test-Path $hp) { Get-Content $hp -Raw } else { "NOT WRITTEN" }

"`n=== AGENT reaches the page via CDP (http://127.0.0.1:9333/json) ==="
try {
    $pages = Invoke-RestMethod 'http://127.0.0.1:9333/json' -TimeoutSec 5
    $pages | Where-Object { $_.type -eq 'page' } | ForEach-Object { "  url=$($_.url)`n  ws=$($_.webSocketDebuggerUrl)" }
} catch { "CDP ENDPOINT UNREACHABLE: $_" }

$pipe.Dispose()
