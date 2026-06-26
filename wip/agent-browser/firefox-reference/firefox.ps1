<#
.SYNOPSIS
    Firefox RDP Controller (V15.3 - Agent Voice & Robust Stream)
    
.DESCRIPTION
    Browser automation via native RDP protocol.
    Now featuring Autonomous Agent capabilities via local LLM.
    
    ARCHITECTURE:
    1. NO global window pollution - uses closures only
    2. NO fetch/XHR hooking - monitors network via RDP protocol
    3. transient synthetic events
    
    UPDATES (V15.3):
    - AGENT VOICE: Added /respond command for direct user communication.
    - ROBUST STREAMING: State-based parser handles split tokens (e.g. < th ink >).
    - UI POLISH: Added explicit flush for smoother text animation.
    - UI FIX: Command palette now visible during Agent Mode.
    - PROMPT FIX: Command Palette moved to top for better adherence.
    - PARSER FIX: Regex Scraper implemented for chatty models.
    
.PARAMETER Port
    Remote debugging port (default: 9999)
.PARAMETER Objective
    Sets the initial goal for the autonomous agent.
#>

param(
    [int]$Port = 7777,
    [string]$RemoteIP = "127.0.0.1",
    [string]$BinaryPath = "AUTO",
    [switch]$Verbose,
    [string]$Objective = "",
    [switch]$Google
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# --- GOOGLE AI CLEAN UI OVERRIDE ---
if ($Google) {
    [Console]::Write("Initializing Google AI ")
    $global:GoogleReady = $false
    
    function Write-Host {
        param(
            [Parameter(ValueFromPipeline=$true, Position=0, ValueFromRemainingArguments=$true)][Object]$Object,
            [switch]$NoNewline,
            [ConsoleColor]$ForegroundColor = $null,
            [ConsoleColor]$BackgroundColor = $null,
            [Object]$Separator = " "
        )
        $msg = $Object -join $Separator
        
        # During loading phase, convert all architecture logs to loading dots
        if (-not $global:GoogleReady) {
            if ($msg -match "^\[(?:INIT|CONNECT|NAV|TARGET|AGENT)\]" -or $msg -match "^[╔║╚]") {
                [Console]::Write(".")
            }
            return
        }
        
        # After loading, permanently suppress all backend/network noise
        if ($msg -match "^\[(?:ACTION|FLOW|NAV|DOM|EXTRACT|SYNC|NET|TARGET|BASIC PAGE INFO|WARN)\]" -or
            $msg -match "^\s*URL: " -or 
            $msg -match "^\s*Title: " -or
            $msg -match "SETTLED" -or
            $msg -match "TIMEOUT" -or
            $msg -match "\[\d+/s\]") {
            return
        }
        
        # Allow normal output
        $params = @{ Object = $Object }
        if ($NoNewline) { $params.Add('NoNewline', $true) }
        if ($ForegroundColor) { $params.Add('ForegroundColor', $ForegroundColor) }
        if ($BackgroundColor) { $params.Add('BackgroundColor', $BackgroundColor) }
        if ($PSBoundParameters.ContainsKey('Separator')) { $params.Add('Separator', $Separator) }
        Microsoft.PowerShell.Utility\Write-Host @params
    }
}

function Read-MultilineInput {
    [CmdletBinding()]
    param([string]$Prompt = "`n[Google AI] (Shift+Enter to send) `n> ")
    
    Microsoft.PowerShell.Utility\Write-Host $Prompt -NoNewline -ForegroundColor Green
    
    $lines = [System.Collections.Generic.List[string]]::new()
    $currentLine = [System.Text.StringBuilder]::new()
    
    while ($true) {
        $key = [Console]::ReadKey($true)
        
        if ($key.Key -eq 'Enter') {
            if ($key.Modifiers -match "Shift" -or $key.Modifiers -match "Control") {
                $lines.Add($currentLine.ToString())
                Microsoft.PowerShell.Utility\Write-Host "" 
                return ($lines -join "`n").Trim()
            } else {
                $lines.Add($currentLine.ToString())
                $currentLine.Clear()
                Microsoft.PowerShell.Utility\Write-Host ""
                Microsoft.PowerShell.Utility\Write-Host "  " -NoNewline
            }
        } elseif ($key.Key -eq 'Backspace') {
            if ($currentLine.Length -gt 0) {
                $currentLine.Length--
                [Console]::CursorLeft--
                [Console]::Write(" ")
                [Console]::CursorLeft--
            }
        } elseif ($key.KeyChar -ne 0 -and -not [char]::IsControl($key.KeyChar)) {
            $null = $currentLine.Append($key.KeyChar)
            [Console]::Write($key.KeyChar)
        }
    }
}

# --- AUTO-LAUNCH & CONFIGURATION ---

function Get-FirefoxPath {
    if ($BinaryPath -ne "AUTO") { return $BinaryPath }
    
    $paths = @(
        "C:\bin\firefox\firefox.exe",
        "C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
        "$env:LOCALAPPDATA\Mozilla Firefox\firefox.exe"
    )
    
    foreach ($p in $paths) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Test-PortSilent {
    param($IP, $P)
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $connect = $client.BeginConnect($IP, $P, $null, $null)
        $success = $connect.AsyncWaitHandle.WaitOne(100, $false)
        if ($success) {
            $client.EndConnect($connect)
            $client.Close()
            return $true
        }
        return $false
    } catch {
        return $false
    }
}

$FirefoxBin = Get-FirefoxPath

Write-Host "╔═══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Firefox RDP Controller v15.3 - Agent Voice & Stream Fix     ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

# --- PROCESS MANAGEMENT (MODIFIED FOR PORTABLE) ---
$ExistingProc = Get-Process firefox -ErrorAction SilentlyContinue | Select-Object -First 1

# 1. Detect if we are in a portable folder (Script, Exe, and Profile in same dir)
$LocalExe = Join-Path $PSScriptRoot "firefox.exe"
$LocalProfile = Join-Path $PSScriptRoot "profile"
$IsPortable = (Test-Path $LocalExe) -and (Test-Path $LocalProfile)

# 2. Update Binary Path preference to local if portable
if ($IsPortable -and $BinaryPath -eq "AUTO") {
    $FirefoxBin = $LocalExe
}

if ($ExistingProc) {
    Write-Host "[INIT] Found existing Firefox process (PID: $($ExistingProc.Id))" -ForegroundColor Yellow
    
    if (Test-PortSilent -IP $RemoteIP -P $Port) {
        Write-Host "[INIT] ✓ Debug port $Port is open" -ForegroundColor Green
    } else {
        Write-Host "[WARN] Firefox is running but port $Port is closed." -ForegroundColor Red
        Write-Host "       Please close Firefox and let this script launch it." -ForegroundColor Yellow
        $ans = "y"
        if (-not $Google) { $ans = Read-Host "       Continue anyway? (y/n)" }
        if ($ans -ne "y") { exit }
    }
} else {
    if (!$FirefoxBin) {
        Write-Error "Firefox executable not found. Pass -BinaryPath or install to default location."
        exit 1
    }

    Write-Host "[INIT] Launching Firefox..." -ForegroundColor Cyan
    
    try {
        $wshell = New-Object -ComObject WScript.Shell
        
        # Build arguments based on detection
        $args = "--start-debugger-server $Port"
        
        if ($IsPortable) {
            Write-Host "[INIT] Mode: Portable (Profile: $LocalProfile)" -ForegroundColor Green
            # CRITICAL: -no-remote allows it to run alongside other instances
            # CRITICAL: -profile points to the local folder
            $args += " -no-remote -profile ""$LocalProfile"""
        } else {
            Write-Host "[INIT] Mode: Standard Installation" -ForegroundColor Gray
        }

        # WindowStyle 7 = Minimized, Not Active
        $wshell.Run("""$FirefoxBin"" $args", 7, $false)
    } catch {
        Write-Error "Failed to launch Firefox: $_"
        exit 1
    }

    Write-Host "[INIT] Waiting for debugger port $Port... " -NoNewline -ForegroundColor Gray
    $Timer = 0
    while (-not (Test-PortSilent -IP $RemoteIP -P $Port)) {
        if ($Timer -gt 30) { 
            Write-Host "TIMEOUT" -ForegroundColor Red
            exit 1 
        }
        Write-Host "." -NoNewline -ForegroundColor Gray
        Start-Sleep -Seconds 1
        $Timer++
    }
    Write-Host " OK" -ForegroundColor Green
}

# --- GLOBAL STATE ---
$State = @{
    Client = $null
    Stream = $null
    Buffer = New-Object System.Collections.Generic.List[byte]
    
    Actors = @{
        Root = "root"
        Watcher = $null
        Process = $null
    }
    
    CurrentTab = @{
        BrowserId = $null
        BrowsingContextID = $null
        Actor = $null
        Console = $null
        Url = "about:blank"
        Title = ""
    }
    
    # Network monitoring (RDP-based, not JS-based)
    NetworkActivity = @{
        ActiveRequests = @{}
        LastActivity = Get-Date
        TotalRequests = 0
        CompletedRequests = 0
    }
    
    # DOM monitoring (RDP-based)
    DOMActivity = @{
        LastChange = Get-Date
        NavigationStarted = $false
        DOMComplete = $false
        LastEventTime = Get-Date
        InitComplete = $false
    }
    
    # Process telemetry
    ProcessMetrics = @{
        Memory = 0
        IORate = 0
        TCPConnections = 0
    }
    
    # Timing Settings (human-like variance)
    InputTiming = @{
        MinTypingDelay = 50
        MaxTypingDelay = 150
        MinActionDelay = 200
        MaxActionDelay = 500
        HumanVariance = 0.3
    }
    
    # Agent / LLM Integration
    LLM = @{
        Host = "http://127.0.0.1:7777"
        Available = $false
        Objective = $Objective
    }
    
    # Cache
    CachedElements = ""
    LastEvalResult = $null
    LastThought = ""  # New: Persist reasoning across refreshes
    
    # URL Blacklist (filter noise)
    UrlBlacklist = @(
        "doubleclick", "googlesyndication", "googletagmanager", 
        "facebook.net", "analytics", "tracking", "ads", "adservice",
        "taboola", "outbrain", "criteo", "pubmatic", "bidswitch"
    )
}

# --- TIMING UTILITIES ---

function Get-HumanDelay {
    param(
        [int]$BaseMs,
        [double]$Variance = $State.InputTiming.HumanVariance
    )
    
    $jitter = (Get-Random -Minimum (-$Variance) -Maximum $Variance) * $BaseMs
    return [Math]::Max(10, $BaseMs + $jitter)
}

# --- ENVIRONMENT COMPATIBILITY SHIM ---
# This ensures navigator.webdriver is undefined for compatibility
# NO global variables, NO fetch hooks, NO persistent observers
$CompatibilityShim = @"
(function(){
    Object.defineProperty(navigator, 'webdriver', {
        get: () => undefined
    });
    
    window.chrome = window.chrome || {};
    Object.defineProperty(window.chrome, 'runtime', {
        get: () => undefined
    });
    
    return 'OK';
})()
"@

# --- PAGE EXTRACTION SCRIPT (Minimal, No Persistence) ---
# This runs once, returns data, and leaves NO TRACE
$ExtractPageScript = @"
(function(){
    try {
        var url = document.location.href;
        var title = document.title;
        var text = (document.body ? document.body.innerText : "") || "";
        text = text.replace(/[ \t]+/g, ' ').replace(/\n\s*\n/g, '\n').trim();
        if(text.length > 80000) text = text.substring(0, 80000) + "...[TRUNCATED]";

        var elements = Array.from(document.querySelectorAll(
            'a, button, input, textarea, select, [role="button"], [role="link"], ' +
            '[role="textbox"], [contenteditable="true"], [onclick]'
        ));
        
        var interactable = [];
        
        elements.forEach((el, index) => {
            if(el.offsetParent !== null && 
               el.style.display !== 'none' && 
               el.style.visibility !== 'hidden' &&
               el.offsetWidth > 0 && 
               el.offsetHeight > 0) {
                
                var label = (
                    el.innerText || 
                    el.value || 
                    el.placeholder || 
                    el.ariaLabel || 
                    el.title ||
                    el.name || 
                    el.alt ||
                    "element"
                ).replace(/\s+/g, " ").trim().substring(0, 80);
                
                var type = el.tagName.toLowerCase();
                if (el.type) type += '[' + el.type + ']';
                
                if(label.length > 0) {
                    interactable.push(index + "|" + type + "|" + label);
                }
            }
        });
        
        if(interactable.length > 500) interactable = interactable.slice(0, 500);

        var data = "DATA_START;;;" + 
                   url + ";;;" + 
                   title + ";;;TEXT_START;;;" + 
                   text + ";;;ELEMENTS_START;;;" + 
                   interactable.join("\n") + ";;;DATA_END";
        
        return btoa(encodeURIComponent(data));
    } catch(e) { 
        return "ERROR: " + e.message; 
    }
})()
"@

# --- AGENT / LLM INTEGRATION ---

function Test-LLM {
    Write-Host "[AGENT] Connecting to brain ($($State.LLM.Host))..." -NoNewline -ForegroundColor Gray
    try {
        # Simple health check to the server
        $null = Invoke-RestMethod -Uri "$($State.LLM.Host)/health" -Method Get -TimeoutSec 1 -ErrorAction Stop
        $State.LLM.Available = $true
        Write-Host " ONLINE" -ForegroundColor Green
    } catch {
        $State.LLM.Available = $false
        Write-Host " OFFLINE (Manual Mode Only)" -ForegroundColor Yellow
    }
}

function Get-LLMCommand {
    param([string]$PageState)
    
    Add-Type -AssemblyName System.Net.Http

    # PROMPT FIX: Command Palette moved to TOP for better model adherence
    $systemPrompt = @"
You are an autonomous web agent. This is your cockpit.

════════════════════════════════════════════════════════════════════════════
COMMAND PALETTE - USAGE GUIDE
════════════════════════════════════════════════════════════════════════════
You must use ONLY these commands to control the browser:

[NAVIGATION]
  /goto <url>           → Navigate to a specific URL.
                          Example: /goto https://www.wikipedia.org
  /back                 → Go back to the previous page.
  /refresh              → Reload the current page.

[INTERACTION]
  /<number>             → Click an interactive element by its ID number.
                          Example: /42 (Clicks element #42)
  /type <text>          → Type a query or text into the input field.
                          Example: /type who invented the lightbulb

[COMMUNICATION]
  /respond <message>    → Communicate with the user.
                          To answer the user or ask for clarification, type /respond followed by your message.
                          Example: /respond I need to know which city you are referring to.

[SYSTEM]
  DONE                  → Type this keyword alone when the OBJECTIVE is satisfied.

CRITICAL: 
- Return ONLY the command. 
- Do not output markdown or explanations outside of <think> tags.
- Choose the most direct path to the OBJECTIVE.

Thinking Process: You may use <think> tags to reason before outputting the command.

OBJECTIVE: $($State.LLM.Objective)

CURRENT STATE:
$PageState
"@

    try {
        $payload = @{
            prompt = $systemPrompt
            temperature = 0.2
            n_predict = 8192
            stream = $true  # ENABLE STREAMING
            stop = @("OBJECTIVE:", "CURRENT", "You are", "User:", "Human:")
        } | ConvertTo-Json

        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(600)
        
        $content = New-Object System.Net.Http.StringContent($payload, [System.Text.Encoding]::UTF8, "application/json")
        $response = $client.PostAsync("$($State.LLM.Host)/completion", $content).Result
        
        if (!$response.IsSuccessStatusCode) {
            Write-Warning "[LLM] API Error: $($response.StatusCode)"
            return $null
        }

        $stream = $response.Content.ReadAsStreamAsync().Result
        $reader = New-Object System.IO.StreamReader($stream)
        
        $fullText = ""
        $isThinking = $false
        $rawAccumulator = New-Object System.Text.StringBuilder
        $hasSSE = $false
        
        Write-Host "" # Newline for stream start
        
        # Read Stream Line-by-Line (SSE)
        while (!$reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            
            # Capture raw line for fallback
            $null = $rawAccumulator.AppendLine($line)
            
            if ($line.StartsWith("data: ")) {
                $hasSSE = $true
                $jsonStr = $line.Substring(6) # Remove "data: "
                
                if ($jsonStr.Trim() -eq "[DONE]") { break }

                try {
                    $chunk = $jsonStr | ConvertFrom-Json
                    
                    # UNIVERSAL PARSER: Handle both Llama.cpp and OpenAI formats
                    $token = $null
                    if ($chunk.content) { $token = $chunk.content }
                    elseif ($chunk.choices -and $chunk.choices[0].delta -and $chunk.choices[0].delta.content) { 
                        $token = $chunk.choices[0].delta.content 
                    }

                    if ($token) {
                        $fullText += $token
                        
                        # --- ROBUST STATE-BASED PARSER ---
                        # Instead of checking the single token, check the state of the FULL text accumulator.
                        # This handles split tags like ["<", "th", "ink", ">"] correctly.
                        
                        $hasThinkStart = $fullText.Contains("<think>")
                        $hasThinkEnd = $fullText.Contains("</think>")
                        
                        if ($hasThinkStart -and !$hasThinkEnd) {
                            # We are INSIDE a thinking block
                            $isThinking = $true
                        } else {
                            # We are OUTSIDE (or have finished) a thinking block
                            $isThinking = $false
                        }
                        
                        # Override color if we are currently printing a tag part
                        # (Visual polish to avoid cyan flickers on tag brackets)
                        if ($token -match "[<>/]") { 
                             Write-Host $token -NoNewline -ForegroundColor Gray
                        }
                        elseif ($isThinking) { 
                             Write-Host $token -NoNewline -ForegroundColor Gray 
                        }
                        else { 
                             Write-Host $token -NoNewline -ForegroundColor Cyan 
                        }
                        
                        # FORCE UI FLUSH
                        [System.Console]::Out.Flush()
                    }
                    
                    if ($chunk.stop) { break }
                } catch {
                    # Ignore JSON parse errors in chunks
                }
            }
        }

        # --- FALLBACK: Handle Non-Streaming Responses or Failed SSE ---
        if ($fullText.Length -eq 0) {
             try {
                $rawJson = $rawAccumulator.ToString()
                if (-not [string]::IsNullOrWhiteSpace($rawJson)) {
                    # Attempt 1: Parse as standard JSON response
                    try {
                        $json = $rawJson | ConvertFrom-Json
                        if ($json.content) { $fullText = $json.content }
                        elseif ($json.choices[0].message.content) { $fullText = $json.choices[0].message.content }
                        elseif ($json.choices[0].text) { $fullText = $json.choices[0].text }
                    } catch {
                        # Ignore parse error
                    }
                    
                    # Attempt 2: If parsing failed, or text still empty, checking for raw string dump
                    if (!$fullText) {
                         if ($Verbose) { Write-Warning "[LLM] Raw output dump: $rawJson" }
                    } else {
                         Write-Host $fullText -ForegroundColor Cyan
                    }
                }
             } catch {
                 Write-Warning "[LLM] Could not parse response."
             }
        }
        
        Write-Host "" # Newline at end
        $client.Dispose()
        
        # --- THINKING PERSISTENCE ---
        # Extract the logic block to save it for the UI refresh
        if ($fullText -match '(?s)<think>(.*?)</think>') {
            $State.LastThought = $matches[1].Trim()
            
            # Remove the thought block to isolate the command
            $cmdText = $fullText -replace '(?s)<think>.*?</think>', ''
        } else {
            $State.LastThought = "" # Clear if no thought
            $cmdText = $fullText
        }

        # --- COMMAND PARSING (REGEX SCRAPER) ---
        # Instead of trusting the first line, we look for the first VALID command pattern.
        # This handles "I will click the link. /42" correctly.
        
        $finalCmd = ""
        
        # 1. Match standard commands (/goto, /type, /respond, /<number>)
        # Regex explanation:
        #  /           = Literal slash
        #  (goto|type|respond|back|refresh) = keywords
        #  \s+         = space
        #  .*?         = arguments
        #  OR
        #  /(\d+)      = simple number
        #  OR
        #  DONE        = completion
        
        if ($cmdText -match "(?mi)^.*?(/(goto|type|respond|back|refresh)\s+.*|/\d+|DONE).*$") {
            # Extract specifically the command part
            if ($cmdText -match "(?i)(/goto\s+[^\s]+|/type\s+.+|/respond\s+.+|/\d+|/back|/refresh|DONE)") {
                $finalCmd = $matches[0].Trim()
            }
        }
        
        # Fallback: If no strict regex matched, try the old first-line method 
        # (useful if the model hallucinates a valid-looking but unknown command)
        if ([string]::IsNullOrWhiteSpace($finalCmd)) {
            $cleaner = $cmdText -replace '^COMMAND:\s*', ''
            $cleaner = $cleaner -replace '^CMD:\s*', ''
            $finalCmd = ($cleaner -split "`n")[0].Trim()
        }
        
        return $finalCmd
        
    } catch {
        Write-Warning "[LLM] Query failed: $($_.Exception.Message)"
        return $null
    }
}

# --- NETWORK CORE ---

function Connect-RDP {
    try {
        Write-Host "[CONNECT] Attempting connection to $RemoteIP`:$Port..." -ForegroundColor Gray
        $State.Client = New-Object System.Net.Sockets.TcpClient($RemoteIP, $Port)
        $State.Stream = $State.Client.GetStream()
        $State.Buffer.Clear()
        Write-Host "[CONNECT] ✓ Connected" -ForegroundColor Green
        return $true
    } catch {
        Write-Warning "[CONNECT] Failed: $($_.Exception.Message)"
        return $false
    }
}

function Send-Packet {
    param([string]$To, [string]$Type, [hashtable]$Data=@{})
    
    if (!$State.Stream -or !$State.Stream.CanWrite) { return }
    
    $payload = @{ to = $To; type = $Type }
    foreach($k in $Data.Keys) { $payload[$k] = $Data[$k] }
    
    $json = $payload | ConvertTo-Json -Compress -Depth 10
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $header = [System.Text.Encoding]::ASCII.GetBytes("$($bytes.Length):")
    
    try {
        $State.Stream.Write($header, 0, $header.Length)
        $State.Stream.Write($bytes, 0, $bytes.Length)
        
        if ($Verbose) {
            Write-Host "[SEND] $Type -> $To" -ForegroundColor DarkGray
        }
    } catch {
        Write-Warning "[SEND] Failed: $_"
    }
}

function Read-NextPacket {
    if (!$State.Stream.DataAvailable -and $State.Buffer.Count -eq 0) { 
        return $null 
    }
    
    # CRITICAL FIX: Drain the stream fully to prevent sleep-throttling on large payloads
    while ($State.Stream.DataAvailable) {
        $chunk = New-Object byte[] 65536
        $read = $State.Stream.Read($chunk, 0, $chunk.Length)
        if ($read -gt 0) {
            $sub = New-Object byte[] $read
            [Array]::Copy($chunk, $sub, $read)
            $State.Buffer.AddRange($sub)
        } else {
            break
        }
    }

    $bufArr = $State.Buffer.ToArray()
    $colonIdx = [Array]::IndexOf($bufArr, [byte]58)
    
    if ($colonIdx -eq -1) { return $null }

    $lenStr = [System.Text.Encoding]::ASCII.GetString($bufArr[0..($colonIdx-1)])
    if (-not [int]::TryParse($lenStr, [ref]$null)) { 
        $State.Buffer.Clear()
        return $null 
    }
    
    $packetLen = [int]$lenStr
    $totalLen = $colonIdx + 1 + $packetLen

    if ($State.Buffer.Count -lt $totalLen) { return $null }

    $jsonStr = [System.Text.Encoding]::UTF8.GetString(
        $bufArr[($colonIdx + 1)..($colonIdx + $packetLen)]
    )
    $State.Buffer.RemoveRange(0, $totalLen)

    try { 
        $packet = ($jsonStr | ConvertFrom-Json)
        
        if ($Verbose -and $packet.type) {
            Write-Host "[RECV] $($packet.type)" -ForegroundColor DarkGray
        }
        
        return $packet
    } catch { 
        return $null 
    }
}

function Expand-Grip {
    param($Grip, [int]$MaxWaitMs = 2000)
    
    if ($null -eq $Grip) { return $null }
    
    if ($Grip -is [string] -or $Grip -is [int] -or $Grip -is [bool]) { 
        return $Grip 
    }
    
    if ($Grip -is [PSCustomObject]) {
        
        if ($Grip.type -eq "undefined") { return $null }
        if ($Grip.type -eq "null") { return $null }
        
        if ($Grip.type -in @("string", "number", "boolean") -and $null -ne $Grip.value) {
            return $Grip.value
        }
        
        if ($Grip.type -eq "longString") {
            if ($Verbose) {
                Write-Host "[GRIP] Fetching longString (length: $($Grip.length))" -ForegroundColor DarkGray
            }
            
            Send-Packet -To $Grip.actor -Type "substring" -Data @{ 
                start = 0
                end = [Math]::Min($Grip.length, 200000)
            }
            
            $start = Get-Date
            while ((Get-Date) - $start -lt [TimeSpan]::FromMilliseconds($MaxWaitMs)) {
                $p = Read-NextPacket
                if ($p -and $p.substring) { 
                    return $p.substring 
                }
                Start-Sleep -Milliseconds 10
            }
            
            Write-Warning "[GRIP] Timeout fetching longString"
            return "[LONGSTRING_TIMEOUT]"
        }
        
        if ($Grip.type -eq "object" -and $Grip.preview) {
            $preview = $Grip.preview
            
            if ($preview.kind -eq "Error") {
                return "[Error: $($preview.message)]"
            }
            
            if ($preview.kind -eq "ArrayLike") {
                $items = $preview.items | ForEach-Object { Expand-Grip $_ }
                return "[Array($($preview.length)): $($items -join ', ')]"
            }
            
            return "[Object:$($Grip.class)]"
        }
        
        if ($Grip.type -eq "object") { 
            if ($Grip.class) {
                return "[Object:$($Grip.class)]"
            }
            return "[Object]"
        }
        
        if ($Grip.type -eq "symbol") {
            return "[Symbol:$($Grip.name)]"
        }
    }
    
    return $Grip
}

# --- PROCESS TELEMETRY ---

function Update-ProcessMetrics {
    try {
        # Fix: Firefox is multi-process. Sum all processes.
        $processes = Get-Process firefox -ErrorAction SilentlyContinue
        
        if (!$processes) { return }

        # Memory: Sum working set of all firefox processes
        $totalMem = ($processes | Measure-Object -Property WorkingSet64 -Sum).Sum
        $State.ProcessMetrics.Memory = [Math]::Round($totalMem / 1MB, 2)

        # IO: Sum cooked value of IO Data Bytes/sec (requires access, try/catch wrap)
        try {
            # Note: Wildcard in counter gets all firefox instances
            $counter = Get-Counter "\Process(firefox*)\IO Data Bytes/sec" -ErrorAction SilentlyContinue
            if ($counter) { 
                $State.ProcessMetrics.IORate = 
                    [Math]::Round(($counter.CounterSamples | 
                    Measure-Object -Property CookedValue -Sum).Sum / 1KB, 2)
            }
        } catch {
             $State.ProcessMetrics.IORate = 0
        }

        # TCP: Get connections for all PIDs
        try {
            $pids = $processes.Id
            $connections = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue | 
                          Where-Object { $pids -contains $_.OwningProcess }
            if ($connections) { 
                $State.ProcessMetrics.TCPConnections = $connections.Count 
            } else {
                $State.ProcessMetrics.TCPConnections = 0
            }
        } catch {
            $State.ProcessMetrics.TCPConnections = 0
        }

    } catch {}
}

function Test-BlacklistedUrl {
    param([string]$Url)
    
    if ([string]::IsNullOrEmpty($Url)) { return $false }
    
    foreach ($pattern in $State.UrlBlacklist) {
        if ($Url -match [regex]::Escape($pattern)) {
            return $true
        }
    }
    
    return $false
}

# --- EVENT PROCESSOR (Network Monitoring via RDP) ---

function Process-BackgroundEvents {
    $eventsProcessed = 0
    
    while ($packet = Read-NextPacket) {
        $eventsProcessed++
        
        # === EVALUATION RESULTS ===
        if ($packet.type -eq "evaluationResult") {
            $State.LastEvalResult = $packet
        }

        # === ATTACH CONFIRMATION (CRITICAL FIX) ===
        if ($packet.type -eq "tabAttached") {
            if ($packet.consoleActor) {
                if ($Verbose) {
                    Write-Host "[SYNC] Console Actor Attached: $($packet.consoleActor)" -ForegroundColor Green
                }
                $State.CurrentTab.Console = $packet.consoleActor
            }
        }

        # === DOCUMENT EVENTS (RDP-based, not JS-based) ===
        if ($packet.type -eq "resources-available-array") {
            foreach ($resourceGroup in $packet.resources) {
                $resourceType = ""
                $resources = @()
                
                if ($resourceGroup -is [Array]) {
                    $resourceType = $resourceGroup[0]
                    $resources = $resourceGroup[1]
                } else {
                    $resourceType = $resourceGroup.resourceType
                    $resources = @($resourceGroup)
                }
                
                foreach ($resource in $resources) {
                    
                    if ($resource.resourceType -eq "document-event") {
                        $resID = [string]$resource.browsingContextID
                        $curID = [string]$State.CurrentTab.BrowsingContextID
                        
                        if ($State.DOMActivity.InitComplete -and $resID -ne $curID) { 
                            continue 
                        }
                        
                        $State.DOMActivity.LastEventTime = Get-Date
                        $State.DOMActivity.LastChange = Get-Date
                        
                        if ($resource.name -eq "will-navigate") {
                            $State.DOMActivity.NavigationStarted = $true
                            $State.DOMActivity.DOMComplete = $false
                            $State.NetworkActivity.ActiveRequests.Clear()
                            
                            if ($resource.newURI) {
                                $State.CurrentTab.Url = $resource.newURI
                            }
                            
                            Write-Host "[NAV] → $($resource.newURI)" -ForegroundColor Yellow
                        }
                        
                        if ($resource.name -eq "dom-interactive") {
                            if ($resource.title) { 
                                $State.CurrentTab.Title = $resource.title 
                            }
                            Write-Host "[DOM] Interactive" -ForegroundColor Gray
                        }
                        
                        if ($resource.name -eq "dom-complete") {
                            $State.DOMActivity.DOMComplete = $true
                            Write-Host "[DOM] Complete ✓" -ForegroundColor Green
                        }
                    }
                    
                    # NETWORK MONITORING (RDP-based, not fetch hook)
                    if ($resource.resourceType -eq "network-event") {
                        $State.NetworkActivity.LastActivity = Get-Date
                        $State.NetworkActivity.TotalRequests++
                        
                        $State.NetworkActivity.ActiveRequests[$resource.resourceId] = @{
                            url = $resource.url
                            method = $resource.method
                            startTime = Get-Date
                            complete = $false
                        }
                        
                        if ($Verbose) {
                            Write-Host "[NET] START: $($resource.method) $($resource.url)" -ForegroundColor DarkCyan
                        }
                    }
                }
            }
        }
        
        # === NETWORK COMPLETIONS (RDP-based) ===
        if ($packet.type -eq "resources-updated-array") {
            foreach ($updateGroup in $packet.updates) {
                $resourceType = ""
                $updates = @()
                
                if ($updateGroup -is [Array]) {
                    $resourceType = $updateGroup[0]
                    $updates = $updateGroup[1]
                } else {
                    $resourceType = $updateGroup.resourceType
                    $updates = @($updateGroup)
                }
                
                foreach ($update in $updates) {
                    if ($update.resourceType -eq "network-event" -and 
                        $State.NetworkActivity.ActiveRequests.ContainsKey($update.resourceId)) {
                        
                        $req = $State.NetworkActivity.ActiveRequests[$update.resourceId]
                        
                        if ($update.updateType -match "complete|end") {
                            $req.complete = $true
                            $State.NetworkActivity.CompletedRequests++
                            $State.NetworkActivity.LastActivity = Get-Date
                            
                            if ($Verbose) {
                                $duration = ((Get-Date) - $req.startTime).TotalMilliseconds
                                Write-Host "[NET] COMPLETE: $($req.url) ($([Math]::Round($duration))ms)" -ForegroundColor DarkGreen
                            }
                        }
                    }
                }
            }
        }
        
        # === TARGET UPDATES (CRITICAL FOR GOTO RELIABILITY) ===
        if ($packet.type -eq "target-available-form") {
            $t = $(if ($packet.target) { $packet.target } else { $packet })
            
            if (Test-BlacklistedUrl -Url $t.url) {
                continue
            }
            
            # CRITICAL: Always check if the current Tab ID matches the update
            $targetBcID = [string]$t.browsingContextID
            $ourBcID = [string]$State.CurrentTab.BrowsingContextID
            
            if ($targetBcID -eq $ourBcID) {
                # FIX: Allow update if Actor changed OR if Console is missing (recovery mode)
                if ($t.actor -and $t.consoleActor -and ($t.actor -ne $State.CurrentTab.Actor -or !$State.CurrentTab.Console)) {
                    Write-Host "[TARGET] Tab Actor Changed (Navigated): $($t.url)" -ForegroundColor Cyan
                    $State.CurrentTab.Actor = $t.actor
                    $State.CurrentTab.Console = $t.consoleActor
                    
                    # Re-attach to the new actor immediately
                    Send-Packet -To $t.actor -Type "attach" -Data @{ 
                        options = @{ ignoreSubFrames = $true } 
                    }
                    
                    Invoke-CompatibilityShim
                }
            }
        }
    }
    
    if ($Verbose -and $eventsProcessed -gt 50) {
        Write-Host "[EVENTS] Processed $eventsProcessed" -ForegroundColor DarkGray
    }
}

# --- COMPATIBILITY SHIM ---

function Invoke-CompatibilityShim {
    if (!$State.CurrentTab.Console) { return }
    
    Send-Packet -To $State.CurrentTab.Console -Type "evaluateJSAsync" -Data @{ 
        text = $CompatibilityShim 
    }
    
    Start-Sleep -Milliseconds 100
    Process-BackgroundEvents
}

# --- ADAPTIVE FLOW CONTROL (NEW) ---

function Wait-NetworkFlowSettling {
    param([int]$TimeoutSec = 10)
    Write-Host "[FLOW] Watching network traffic (Timeout: ${TimeoutSec}s)..." -NoNewline -ForegroundColor DarkGray
    
    $start = Get-Date
    $buffer = New-Object byte[] 65536
    
    while ((Get-Date) - $start -lt [TimeSpan]::FromSeconds($TimeoutSec)) {
        $intervalStart = Get-Date
        $packetApprox = 0
        
        # Measure for 1 second or until activity drops
        while ((Get-Date) - $intervalStart -lt [TimeSpan]::FromSeconds(1)) {
            if ($State.Stream.DataAvailable) {
                # Blind Drain: Read and discard content
                $read = $State.Stream.Read($buffer, 0, $buffer.Length)
                if ($read -gt 0) {
                    # Fast Heuristic: Use Split to count colons (Packet headers)
                    # This is much faster than full parsing
                    $chunkStr = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
                    $packetApprox += ($chunkStr.Split(':').Length - 1)
                }
            } else {
                Start-Sleep -Milliseconds 50
            }
        }
        
        # Check flow rate
        if ($packetApprox -gt 0) {
             Write-Host " [${packetApprox}/s] " -NoNewline -ForegroundColor DarkGray
        } else {
             Write-Host "." -NoNewline -ForegroundColor DarkGray
        }
        
        # Requirement: Drop if < 5 packets/sec
        if ($packetApprox -lt 5) {
            Write-Host " SETTLED" -ForegroundColor Green
            return
        }
    }
    
    Write-Host " TIMEOUT (Proceeding)" -ForegroundColor Yellow
}

# --- PAGE EXTRACTION (FIX: Decode Base64 before checking) ---

function Invoke-PageExtraction {
    if (!$State.CurrentTab.Console) { 
        Write-Warning "[EXTRACT] No console actor available"
        return 
    }
    
    $State.LastEvalResult = $null
    
    if ($Verbose) {
        Write-Host "[EXTRACT] Sending extraction script" -ForegroundColor DarkGray
    }
    
    Send-Packet -To $State.CurrentTab.Console -Type "evaluateJSAsync" -Data @{ 
        text = $ExtractPageScript 
    }
    
    $start = Get-Date
    $foundResult = $false
    
    # TUNED: Increased timeout for heavy AI page extraction
    while ((Get-Date) - $start -lt [TimeSpan]::FromSeconds(8)) {
        
        Process-BackgroundEvents
        
        if ($State.LastEvalResult -and $State.LastEvalResult.result) {
            $result = $State.LastEvalResult.result
            $val = Expand-Grip $result
            
            # FIX: Decode Base64 BEFORE checking for marker
            if ($val -is [string] -and $val.Length -gt 0) {
                try {
                    # Try to decode as Base64
                    $decodedBytes = [System.Convert]::FromBase64String($val)
                    $decodedText = [System.Text.Encoding]::UTF8.GetString($decodedBytes)
                    $decodedText = [Uri]::UnescapeDataString($decodedText)

                    # NOW check for the marker
                    if ($decodedText.Contains("DATA_START")) {
                        if ($Verbose) {
                            Write-Host "[EXTRACT] ✓ Valid data found" -ForegroundColor Green
                        }
                        # Pass the raw Base64 to parser (it will decode again)
                        Parse-And-Display $val
                        $foundResult = $true
                        break
                    }
                } catch {
                    # Not Base64, check if it's an error message
                    if ($val.StartsWith("ERROR:")) {
                        Write-Warning "[EXTRACT] JS Error: $val"
                        break
                    }
                }
            }
        }
        
        Start-Sleep -Milliseconds 50
    }
    
    if (-not $foundResult) {
        Write-Warning "[EXTRACT] Timeout - no valid extraction data received"
        Show-BasicPageInfo
    }
}

function Show-BasicPageInfo {
    Write-Host "`n[BASIC PAGE INFO]" -ForegroundColor Yellow
    Write-Host "  URL: $($State.CurrentTab.Url)" -ForegroundColor Gray
    Write-Host "  Title: $($State.CurrentTab.Title)" -ForegroundColor Gray
}

function Parse-And-Display {
    param($Raw)
    
    try {
        $clean = [Uri]::UnescapeDataString(
            [System.Text.Encoding]::UTF8.GetString(
                [System.Convert]::FromBase64String($Raw)
            )
        )
        
        $parts = $clean -split ";;;"
        if ($parts.Count -lt 6) { 
            Write-Warning "[PARSE] Invalid format"
            return 
        }
        
        $url = $parts[1]
        $title = $parts[2]
        $text = $parts[4]
        $elements = $parts[6]
        
        # CRITICAL: Sync Global State with Extracted Data
        $State.CachedElements = $elements
        $State.CurrentTab.Title = $title
        $State.CurrentTab.Url = $url

        # --- SILENCE ASCII PARSING UI IN GOOGLE MODE ---
        if ($Google) { return }
        
        # CRITICAL FIX: Only clear screen if NOT verbose, otherwise we lose debug info
        if (-not $Verbose) {
            cls
        } else {
            Write-Host "`n[DISPLAY UPDATE]" -ForegroundColor Cyan
        }

        # --- AGENT COCKPIT HEADER ---
        if ($State.LLM.Objective) {
            Write-Host "╔═══════════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
            Write-Host "║ AGENT COCKPIT                                                                 ║" -ForegroundColor Magenta
            Write-Host "╠═══════════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Magenta
            
            # Word wrap objective
            $maxLen = 77
            $obj = $State.LLM.Objective
            if ($obj.Length -le $maxLen) {
                Write-Host "║ $($obj.PadRight($maxLen)) ║" -ForegroundColor White
            } else {
                $words = $obj -split ' '
                $line = ""
                foreach ($word in $words) {
                    if (($line + " " + $word).Length -le $maxLen) {
                        $line += $(if ($line) { " " } else { "" }) + $word
                    } else {
                        Write-Host "║ $($line.PadRight($maxLen)) ║" -ForegroundColor White
                        $line = $word
                    }
                }
                if ($line) {
                    Write-Host "║ $($line.PadRight($maxLen)) ║" -ForegroundColor White
                }
            }
            Write-Host "╚═══════════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
            
            # --- PERSISTENT THINKING BLOCK ---
            if (![string]::IsNullOrEmpty($State.LastThought)) {
                 Write-Host "`n[LAST THOUGHT]" -ForegroundColor Gray
                 $State.LastThought -split "`n" | ForEach-Object {
                     Write-Host "  $_" -ForegroundColor Gray
                 }
                 Write-Host ""
            }
            
            Write-Host ""
        }
        
        Write-Host "╔═══════════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
        Write-Host "║ $($title.PadRight(77)) ║" -ForegroundColor Cyan
        Write-Host "╠═══════════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Cyan
        Write-Host "║ URL: $($url.PadRight(72)) ║" -ForegroundColor White
        Write-Host "╚═══════════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
        
        Write-Host "[PAGE CONTENT]" -ForegroundColor Yellow
        Write-Host ("-" * 80) -ForegroundColor DarkGray
        
        if ($text.Length -gt 12000) {
            Write-Host $text.Substring(0, 4000) -ForegroundColor White
            Write-Host "... [$($text.Length - 12000) more characters]" -ForegroundColor Gray
        } else {
            Write-Host $text -ForegroundColor White
        }
        
        Write-Host "`n[INTERACTIVE ELEMENTS]" -ForegroundColor Cyan
        Write-Host ("-" * 80) -ForegroundColor DarkGray
        
        $elementLines = $elements -split "`n"
        if ($elementLines.Count -gt 40) {
            foreach ($line in $elementLines[0..39]) {
                $parts = $line -split "\|"
                if ($parts.Count -eq 3) {
                    Write-Host " [$($parts[0].PadLeft(3))] " -NoNewline -ForegroundColor Green
                    Write-Host "$($parts[1].PadRight(12)) " -NoNewline -ForegroundColor Gray
                    Write-Host $parts[2] -ForegroundColor White
                }
            }
            Write-Host " ... [$($elementLines.Count - 40) more elements - use /elements to view all]" -ForegroundColor Gray
        } else {
            foreach ($line in $elementLines) {
                $parts = $line -split "\|"
                if ($parts.Count -eq 3) {
                    Write-Host " [$($parts[0].PadLeft(3))] " -NoNewline -ForegroundColor Green
                    Write-Host "$($parts[1].PadRight(12)) " -NoNewline -ForegroundColor Gray
                    Write-Host $parts[2] -ForegroundColor White
                }
            }
        }
        
        # Show telemetry ONLY if Verbose
        if ($Verbose) {
            Write-Host "`n[TELEMETRY]" -ForegroundColor Magenta
            Write-Host ("-" * 80) -ForegroundColor DarkGray
            Write-Host "  Memory:        $($State.ProcessMetrics.Memory) MB" -ForegroundColor Gray
            Write-Host "  I/O Rate:      $($State.ProcessMetrics.IORate) KB/s" -ForegroundColor Gray
            Write-Host "  TCP Conns:     $($State.ProcessMetrics.TCPConnections)" -ForegroundColor Gray
            Write-Host "  Net Requests:  $($State.NetworkActivity.TotalRequests) total, $($State.NetworkActivity.CompletedRequests) completed" -ForegroundColor Gray
        }
        
    } catch {
        Write-Warning "[PARSE] Error: $($_.Exception.Message)"
    }
}

# --- INITIALIZATION ---

function Initialize-RDP {
    if (-not (Connect-RDP)) { exit 1 }
    
    Write-Host "[INIT] Handshaking..." -ForegroundColor Gray
    $greeting = $null
    $handshakeWait = Get-Date
    while (!$greeting -and (Get-Date) - $handshakeWait -lt [TimeSpan]::FromSeconds(5)) { 
        $greeting = Read-NextPacket
        Start-Sleep -Milliseconds 10 
    }
    
    if (!$greeting) {
        Write-Error "[INIT] No handshake received"
        exit 1
    }
    
    $State.Actors.Root = $greeting.from
    Write-Host "[INIT] ✓ Handshake complete (Root: $($State.Actors.Root))" -ForegroundColor Green
    
    # Get tab list
    Write-Host "[INIT] Getting tab list..." -ForegroundColor Gray
    Send-Packet -To $State.Actors.Root -Type "listTabs"
    $tabsRes = $null
    $tabsWait = Get-Date
    while (!$tabsRes -and (Get-Date) - $tabsWait -lt [TimeSpan]::FromSeconds(3)) { 
        $tabsRes = Read-NextPacket
        Start-Sleep -Milliseconds 10 
    }
    
    if (!$tabsRes -or !$tabsRes.tabs) {
        Write-Error "[INIT] Failed to get tabs"
        exit 1
    }
    
    $tab = $tabsRes.tabs | Where-Object { $_.selected -eq $true }
    if (!$tab) { 
        Write-Error "[INIT] No active tab found"
        exit 1
    }
    
    # Store both IDs
    $State.CurrentTab.BrowserId = $tab.browserId
    $State.CurrentTab.BrowsingContextID = $tab.browsingContextID
    $State.CurrentTab.Title = $tab.title
    $State.CurrentTab.Url = $tab.url
    
    Write-Host "[INIT] Target Tab:" -ForegroundColor Cyan
    Write-Host "  Title: $($tab.title)" -ForegroundColor Gray
    Write-Host "  URL: $($tab.url)" -ForegroundColor Gray
    Write-Host "  BrowsingContextID: $($tab.browsingContextID)" -ForegroundColor Yellow
    
    # Setup watchers
    if (-not (Setup-Watchers)) {
        Write-Error "[INIT] Failed to setup watchers"
        exit 1
    }
    
    # Wait for target
    Write-Host "[INIT] Waiting for target..." -ForegroundColor Gray
    
    $targetWait = Get-Date
    
    while (!$State.CurrentTab.Console) {
        if ((Get-Date) - $targetWait -gt [TimeSpan]::FromSeconds(10)) {
            Write-Warning "[INIT] Timeout waiting for console actor"
            exit 1
        }
        
        $packet = Read-NextPacket
        if ($packet -and $packet.type -eq "target-available-form") {
            $t = $(if ($packet.target) { $packet.target } else { $packet })
            
            if (Test-BlacklistedUrl -Url $t.url) {
                continue
            }
            
            if (!$t.consoleActor) {
                continue
            }
            
            $targetBcID = [string]$t.browsingContextID
            $ourBcID = [string]$State.CurrentTab.BrowsingContextID
            $isMatch = $false
            
            # Match by BrowsingContextID or URL
            if ($targetBcID -eq $ourBcID) {
                $isMatch = $true
            }
            elseif ($t.url -and $State.CurrentTab.Url -and $t.url -eq $State.CurrentTab.Url) {
                $isMatch = $true
            }
            
            if ($isMatch) {
                Write-Host "[INIT] ✓ MATCH!" -ForegroundColor Green
                Write-Host "  Actor: $($t.actor)" -ForegroundColor Gray
                Write-Host "  ConsoleActor: $($t.consoleActor)" -ForegroundColor Gray
                
                $State.CurrentTab.Actor = $t.actor
                $State.CurrentTab.Console = $t.consoleActor
                $State.CurrentTab.BrowsingContextID = $t.browsingContextID
                
                Send-Packet -To $t.actor -Type "attach" -Data @{ 
                    options = @{ ignoreSubFrames = $true } 
                }
                
                Start-Sleep -Milliseconds 200
                Process-BackgroundEvents
                
                break
            }
        }
        
        Start-Sleep -Milliseconds 50
    }
    
    if (!$State.CurrentTab.Console) {
        Write-Error "[INIT] Could not obtain console actor"
        exit 1
    }
    
    $State.DOMActivity.InitComplete = $true
    
    Invoke-CompatibilityShim
    
    Write-Host "[INIT] ✓ Initialization complete" -ForegroundColor Green
    return $true
}

function Setup-Watchers {
    Write-Host "[INIT] Setting up watchers... " -NoNewline -ForegroundColor Gray
    
    Send-Packet -To $State.Actors.Root -Type "getProcess" -Data @{ id=0 }
    $proc = $null
    $procWait = Get-Date
    while (!$proc -and (Get-Date) - $procWait -lt [TimeSpan]::FromSeconds(3)) { 
        $p = Read-NextPacket
        if ($p.processDescriptor) { $proc = $p.processDescriptor }
        elseif ($p.process) { $proc = $p.process }
        Start-Sleep -Milliseconds 10 
    }
    
    if (!$proc) { 
        Write-Host "Failed (process)" -ForegroundColor Red
        return $false 
    }
    
    $State.Actors.Process = $proc.actor
    
    Send-Packet -To $proc.actor -Type "getWatcher"
    $watcher = $null
    $watcherWait = Get-Date
    while (!$watcher -and (Get-Date) - $watcherWait -lt [TimeSpan]::FromSeconds(3)) { 
        $p = Read-NextPacket
        if ($p.actor) { 
            $watcher = $p.actor
            $State.Actors.Watcher = $p.actor 
        }
        Start-Sleep -Milliseconds 10 
    }
    
    if (!$State.Actors.Watcher) {
        Write-Host "Failed (watcher)" -ForegroundColor Red
        return $false
    }
    
    Send-Packet -To $State.Actors.Watcher -Type "watchTargets" -Data @{ 
        targetType="frame" 
    }
    Send-Packet -To $State.Actors.Watcher -Type "watchResources" -Data @{ 
        resourceTypes = @("document-event", "network-event") 
    }
    
    Write-Host "✓" -ForegroundColor Green
    return $true
}

# --- SYNC UTILITIES (BLOCKING/ROBUST) ---

function Sync-ActiveTab {
    param([switch]$ForceWait)
    
    # Proactively fetch the current authoritative state from the Root Actor.
    # We send listTabs and loop until we get the response.
    Send-Packet -To $State.Actors.Root -Type "listTabs"
    
    $syncStart = Get-Date
    $tabFound = $false
    $needsAttach = $false
    
    # 1. Wait for tab list response (BLOCKING)
    while ((Get-Date) - $syncStart -lt [TimeSpan]::FromSeconds(3)) {
        $packet = Read-NextPacket
        
        # Capture async events while waiting
        if ($packet -and $packet.type -ne "tabs" -and $packet.tabs -eq $null) {
            # Since we can't easily push back to the buffer without complexity,
            # we must process critical events here or assume they are handled by future syncs.
            if ($packet.type -in @("tabAttached", "target-available-form")) {
                if ($packet.consoleActor) { $State.CurrentTab.Console = $packet.consoleActor }
            }
        }
        
        if ($packet -and $packet.tabs) {
            $tab = $packet.tabs | Where-Object { $_.selected -eq $true }
            
            if ($tab) {
                $oldActor = $State.CurrentTab.Actor
                
                # Update State
                $State.CurrentTab.BrowserId = $tab.browserId
                $State.CurrentTab.Url = $tab.url
                $State.CurrentTab.Title = $tab.title
                
                $oldContext = $State.CurrentTab.BrowsingContextID
                $newContext = $tab.browsingContextID
                
                # Update context ID only if it actually changed
                if ($newContext) { $State.CurrentTab.BrowsingContextID = $newContext }
                
                # CRITICAL FIX: Downgrade Protection
                # If we are in the SAME context (tab didn't change, just navigated),
                # and we already have a working Console (or are waiting for one via events),
                # do NOT overwrite our WindowGlobalTarget Actor with this generic TabDescriptor.
                # The TabDescriptor lacks the consoleActor we need.
                
                $isSameContext = $newContext -eq $oldContext
                $shouldPreserveActor = $isSameContext -and ($null -ne $State.CurrentTab.Console)
                
                if ($shouldPreserveActor -and !$tab.consoleActor) {
                     if ($Verbose) { 
                        Write-Host "[SYNC] Ignoring TabDescriptor ($($tab.actor)) to preserve WindowTarget ($($State.CurrentTab.Actor))" -ForegroundColor DarkGray 
                     }
                }
                else {
                    # Safe to update if:
                    # 1. Context ID changed (Actual tab switch)
                    # 2. Or we have no console anyway (Recovery needed)
                    # 3. Or the new descriptor actually has a console (unlikely but possible)
                    
                    if ($tab.actor -ne $oldActor) {
                        if ($Verbose) { 
                            Write-Host "[SYNC] State Refresh: $oldActor -> $($tab.actor)" -ForegroundColor Yellow 
                        }
                        $State.CurrentTab.Actor = $tab.actor
                        
                        if ($tab.consoleActor) { 
                            $State.CurrentTab.Console = $tab.consoleActor 
                        } else {
                            # We switched actors but have no console.
                            # If this was a tab switch, we might need to Attach.
                            $State.CurrentTab.Console = $null
                            $needsAttach = $true
                        }
                        
                        Send-Packet -To $tab.actor -Type "attach" -Data @{ 
                            options = @{ ignoreSubFrames = $true } 
                        }
                    }
                }
                $tabFound = $true
                break
            }
        }
        Start-Sleep -Milliseconds 10
    }
    
    # 2. Wait for Console Actor (BLOCKING)
    # If we requested an attach, or if we are missing the console, we MUST wait.
    if ($needsAttach -or ($ForceWait -and !$State.CurrentTab.Console)) {
         if ($Verbose) { Write-Host "[SYNC] Waiting for Console Actor..." -ForegroundColor DarkGray }
         $waitStart = Get-Date
         while (!$State.CurrentTab.Console -and (Get-Date) - $waitStart -lt [TimeSpan]::FromSeconds(5)) {
             Process-BackgroundEvents # This handles 'tabAttached' and populates Console
             Start-Sleep -Milliseconds 50
         }
         
         if (!$State.CurrentTab.Console) {
             Write-Warning "[SYNC] Timed out waiting for console actor."
         }
    }
}

# --- COMMAND EXECUTION ---

function Execute-Command {
    param($Cmd)
    
    if ([string]::IsNullOrWhiteSpace($Cmd)) { return }
    
    $State.DOMActivity.NavigationStarted = $false
    $isNavigation = $false
    
    if ($Cmd -eq "/quit" -or $Cmd -eq "quit") { 
        Write-Host "[EXIT] Goodbye!" -ForegroundColor Cyan
        $State.Client.Close()
        exit 0
    }
    
    if ($Cmd -eq "/elements") {
        Write-Host "`n[ALL ELEMENTS]" -ForegroundColor Cyan
        Write-Host $State.CachedElements
        Read-Host "`nPress Enter to continue"
        return
    }
    
    # NEW: Help Command
    if ($Cmd -eq "/help") {
        Write-Host "`n════════════════════════════════════════════════════════════" -ForegroundColor Cyan
        Write-Host "                   MANUAL COMMAND REFERENCE                   " -ForegroundColor Cyan
        Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Cyan
        Write-Host " /goto <url>     " -NoNewline -ForegroundColor Yellow
        Write-Host "- Navigate to a specific URL" -ForegroundColor Gray
        Write-Host " /<number>       " -NoNewline -ForegroundColor Yellow
        Write-Host "- Click an element (e.g., /42)" -ForegroundColor Gray
        Write-Host " /type <text>    " -NoNewline -ForegroundColor Yellow
        Write-Host "- Type into the best available input" -ForegroundColor Gray
        Write-Host " /respond <text> " -NoNewline -ForegroundColor Yellow
        Write-Host "- Agent speaks to you (Agent only)" -ForegroundColor Gray
        Write-Host " /back           " -NoNewline -ForegroundColor Yellow
        Write-Host "- Go back in history" -ForegroundColor Gray
        Write-Host " /refresh        " -NoNewline -ForegroundColor Yellow
        Write-Host "- Reload the page" -ForegroundColor Gray
        Write-Host " /auto           " -NoNewline -ForegroundColor Magenta
        Write-Host "- Start autonomous agent loop" -ForegroundColor Gray
        Write-Host " /stop           " -NoNewline -ForegroundColor Magenta
        Write-Host "- Stop autonomous loop" -ForegroundColor Gray
        Write-Host ""
        return
    }
    
    # CRITICAL: Pre-Command Sync
    Sync-ActiveTab
    
    if ($Cmd -eq "/refresh" -or $Cmd -eq "/f5") {
        $State.DOMActivity.NavigationStarted = $true
        $State.DOMActivity.DOMComplete = $false
        Send-Packet -To $State.CurrentTab.Actor -Type "reload"
        $isNavigation = $true
    }
    elseif ($Cmd -eq "/back") {
        $State.DOMActivity.NavigationStarted = $true
        $State.DOMActivity.DOMComplete = $false
        $script = "window.history.back()"
        Send-Packet -To $State.CurrentTab.Console -Type "evaluateJSAsync" -Data @{ 
            text = $script 
        }
        $isNavigation = $true
    }
    elseif ($Cmd -match "^/?goto (.+)") {
        $url = $matches[1]
        if ($url -notmatch "://") { $url = "https://$url" }
        
        Write-Host "[NAV] Navigating to $url..." -ForegroundColor Cyan
        
        # 1. Send Navigation Command
        Send-Packet -To $State.CurrentTab.Actor -Type "navigateTo" -Data @{ url = $url }
        
        # 2. Watch for network flood and settle
        Wait-NetworkFlowSettling -TimeoutSec 10
        
        # 3. CRITICAL: Hard Reset (The "Clean" approach)
        Write-Host "[NAV] Re-initializing connection..." -ForegroundColor Yellow
        $State.Client.Close()
        Initialize-RDP
        
        $isNavigation = $false # We are already fresh
    }
    elseif ($Cmd -match "^/(\d+)$") {
        $idx = $matches[1]
        $delay = Get-HumanDelay -BaseMs 300
        
        Write-Host "[ACTION] Clicking element $idx (delay: $($delay)ms)..." -ForegroundColor Gray
        Start-Sleep -Milliseconds $delay
        
        # ACTION: Simple click with focus, no synthetic events
        $script = @"
(function(){ 
    var els = document.querySelectorAll(
        'a, button, input, textarea, select, [role="button"], [role="link"], ' +
        '[role="textbox"], [contenteditable="true"], [onclick]'
    );
    
    var el = els[$idx];
    
    if (el) { 
        el.focus();
        el.click();
        return "OK";
    }
    return "NOT_FOUND";
})()
"@
        
        Send-Packet -To $State.CurrentTab.Console -Type "evaluateJSAsync" -Data @{ 
            text = $script 
        }
        
        Start-Sleep -Milliseconds 500
        Process-BackgroundEvents
        
        if ($State.DOMActivity.NavigationStarted) {
            $isNavigation = $true
        }
    }
    elseif ($Cmd -match "^/type\s+(.+)") {
        # FIX: Explicit typing command only
        $textToType = $matches[1]
        Write-Host "[ACTION] Injecting text..." -ForegroundColor Gray
        
        # Replace newlines safely so the multiline strings don't break the JS injection
        $safeText = $textToType.Replace('"', '\"').Replace('\', '\\').Replace("`n", '\n').Replace("`r", '')
        
        $injectionScript = @"
(function(text) {
    var inputs = Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="submit"]):not([type="button"]):not([type="image"]):not([type="reset"]):not([type="checkbox"]):not([type="radio"]), textarea, [contenteditable="true"]'));
    var visible = inputs.filter(e => e.offsetParent !== null && e.getBoundingClientRect().height > 0);
    // Sort by vertical position (lowest first)
    var target = visible.sort((a, b) => b.getBoundingClientRect().top - a.getBoundingClientRect().top)[0];

    if (!target) return "NO_TARGET";

    // 1. Focus
    target.focus();

    // 2. Set Value & Trigger Events (React/Vue Support)
    // We use the setter from prototype if overridden to ensure React state updates
    var proto = Object.getPrototypeOf(target);
    var setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    
    if (setter && target.value !== undefined) {
        setter.call(target, text);
    } else {
        target.value = text;
        if(target.isContentEditable) target.innerText = text;
    }
    
    target.dispatchEvent(new Event('input', { bubbles: true }));
    target.dispatchEvent(new Event('change', { bubbles: true }));

    // 3. Simulate Enter Key
    var event = new KeyboardEvent('keydown', {
        key: 'Enter',
        code: 'Enter',
        which: 13,
        keyCode: 13,
        bubbles: true
    });
    target.dispatchEvent(event);

    // 4. Attempt Form Submit if not prevented
    if (!event.defaultPrevented) {
        if (target.form) {
            var submitBtn = target.form.querySelector('[type="submit"]');
            if (submitBtn) {
                submitBtn.click();
            } else {
                target.form.requestSubmit ? target.form.requestSubmit() : target.form.submit();
            }
        }
    }
    
    return "OK";
})("$safeText")
"@
        Send-Packet -To $State.CurrentTab.Console -Type "evaluateJSAsync" -Data @{ text = $injectionScript }
        Start-Sleep -Milliseconds 100
        
        Wait-NetworkFlowSettling -TimeoutSec 10
    }
    elseif ($Cmd -match "^/respond\s+(.+)") {
        # NEW: Agent Voice - Allows the agent to talk back to the user
        $msg = $matches[1]
        Write-Host "`n[AGENT SAYS] $msg" -ForegroundColor Green
    }
    else {
        Write-Warning "Unknown command. Use /type <text> to type or /respond <text> to speak."
    }
    
    if ($isNavigation) {
        Wait-NetworkFlowSettling -TimeoutSec 10
        Sync-ActiveTab -ForceWait
    } else {
        Start-Sleep -Milliseconds 500
        Process-BackgroundEvents
    }
    
    Invoke-PageExtraction
}

# --- MAIN LOOP ---

try {
    Initialize-RDP
    
    # NEW: Test LLM
    Test-LLM
    
    Invoke-PageExtraction
    
    # After initialization, set Google Ready state so the UI clears
    if ($Google) {
        [Console]::WriteLine()
        $global:GoogleReady = $true
        Clear-Host
    }
    
    while ($true) {
        
        if ($Google) {
            # Clean Google AI input loop
            $input = Read-MultilineInput
            
            # Automatically prepend /type to raw chat messages so you don't have to
            if ($input -and $input -notmatch "^/") {
                $input = "/type $input"
            }
        } else {
            # Original Command Bar & Telemetry UI
            $mem = $State.ProcessMetrics.Memory
            $io = $State.ProcessMetrics.IORate
            $net = $State.ProcessMetrics.TCPConnections
            
            Write-Host "`n────────────────────────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
            
            if ($State.LLM.Available -and $State.LLM.Objective) {
                Write-Host " [AGENT MODE] " -NoNewline -ForegroundColor Magenta
                Write-Host "/go" -NoNewline -ForegroundColor Yellow
                Write-Host " = next step  " -NoNewline -ForegroundColor Gray
                Write-Host "/auto" -NoNewline -ForegroundColor Yellow
                Write-Host " = continuous  " -NoNewline -ForegroundColor Gray
                Write-Host "/stop" -NoNewline -ForegroundColor Yellow
                Write-Host " = manual control" -ForegroundColor Gray
                Write-Host ""
            } 
            
            Write-Host " COMMANDS: " -NoNewline -ForegroundColor Green
            Write-Host "/goto <url> · /<id> · /type <text> · /respond <text> · /help · /quit" -ForegroundColor White
            
            Write-Host "`nCMD > " -NoNewline -ForegroundColor Green
            $input = Read-Host
        }
        
        # --- AGENT COMMANDS ---
        
        if ($input -eq "/go" -and $State.LLM.Available -and $State.LLM.Objective) {
            
            # Build minimal context
            $context = "URL: $($State.CurrentTab.Url)`n"
            $context += "Title: $($State.CurrentTab.Title)`n`n"
            
            # Truncate elements for context window safety
            $elementLines = $State.CachedElements -split "`n"
            $elementSample = $elementLines[0..[Math]::Min(30, $elementLines.Count-1)] -join "`n"
            $context += "Elements:`n$elementSample"
            
            if ($elementLines.Count -gt 30) {
                $context += "`n... and $($elementLines.Count - 30) more elements"
            }
            
            Write-Host "[→] Thinking..." -NoNewline -ForegroundColor Magenta
            $command = Get-LLMCommand -PageState $context
            
            if ($command -eq "DONE") {
                Write-Host " OBJECTIVE COMPLETE" -ForegroundColor Green
                $State.LLM.Objective = ""
            }
            elseif ($command) {
                Write-Host " $command" -ForegroundColor Cyan
                Start-Sleep -Milliseconds 1000
                Execute-Command $command
            }
            else {
                Write-Host " ERROR (No command generated - try again)" -ForegroundColor Red
            }
        }
        elseif ($input -eq "/auto" -and $State.LLM.Available -and $State.LLM.Objective) {
            
            Write-Host "[AGENT] Starting autonomous execution (Ctrl+C to stop)..." -ForegroundColor Yellow
            $step = 0
            $maxSteps = 30
            
            while ($step -lt $maxSteps) {
                $step++
                
                # Build context
                $context = "URL: $($State.CurrentTab.Url)`n"
                $context += "Title: $($State.CurrentTab.Title)`n`n"
                $elementLines = $State.CachedElements -split "`n"
                $elementSample = $elementLines[0..[Math]::Min(30, $elementLines.Count-1)] -join "`n"
                $context += "Elements:`n$elementSample"
                if ($elementLines.Count -gt 30) {
                    $context += "`n... and $($elementLines.Count - 30) more elements"
                }
                
                Write-Host "`n[$step/$maxSteps] " -NoNewline -ForegroundColor Magenta
                $command = Get-LLMCommand -PageState $context
                
                if ($command -eq "DONE") {
                    Write-Host "DONE - Objective complete" -ForegroundColor Green
                    $State.LLM.Objective = ""
                    break
                }
                elseif ($command) {
                    Write-Host $command -ForegroundColor Cyan
                    Start-Sleep -Milliseconds 1500
                    Execute-Command $command
                }
                else {
                    Write-Host "WARN - No command generated. Pausing." -ForegroundColor Yellow
                    break
                }
                
                Start-Sleep -Milliseconds 2000
            }
            
            if ($step -eq $maxSteps) {
                Write-Host "`n[AGENT] Reached step limit" -ForegroundColor Yellow
            }
        }
        elseif ($input -eq "/stop") {
            $State.LLM.Objective = ""
            Write-Host "[MODE] Agent disabled - manual control" -ForegroundColor Yellow
        }
        else {
            Execute-Command $input
        }
        
        Update-ProcessMetrics
    }
    
} catch {
    Write-Error "Fatal error: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
} finally {
    if ($State.Client) {
        $State.Client.Close()
    }
}