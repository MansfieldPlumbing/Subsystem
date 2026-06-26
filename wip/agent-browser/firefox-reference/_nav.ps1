# One action per call, from _act.txt:
#   goto <url> | type <text> | click <handle> | back | forward | refresh | dismiss
#   chunk <n> | look
$act = (Get-Content -LiteralPath 'S:\bin\firefox\_act.txt' -Raw).Trim()
. 'S:\bin\firefox\firefox.work.ps1' -AsLibrary
Initialize-RDP | Out-Null

$verb, $rest = $act -split '\s+', 2
$chunkN = 0
switch ($verb) {
    'goto'  { Execute-Command "/goto $rest" }
    'type'  { Execute-Command "/type $rest"; Start-Sleep -Seconds 9; Invoke-PageExtraction }
    'click' {
        Invoke-PageExtraction          # rebuild handle map for THIS page
        $null = Get-Hud                 # populates $State.IntentMap
        Execute-Command "/$rest"        # resolves handle -> DOM index
        Start-Sleep -Seconds 4
        Invoke-PageExtraction
    }
    { $_ -in 'back', 'forward', 'refresh', 'dismiss' } {
        Execute-Command "/$verb"
        Start-Sleep -Seconds 3
        Invoke-PageExtraction
    }
    'chunk'     { Invoke-PageExtraction; $chunkN = [int]$rest }
    'nextchunk' { Invoke-PageExtraction; $chunkN = 1 }
    default     { Invoke-PageExtraction }
}

$hud = Get-Hud -Chunk $chunkN
"===HUD==="
([ordered]@{
    page     = $hud.page
    title    = $hud.title
    device   = "$($hud.device.time) $($hud.device.weekday) ($($hud.device.timezone)) @ $($hud.device.location)"
    intents  = $hud.intents.Count
}).GetEnumerator() | Format-Table -HideTableHeaders Key, Value | Out-String -Width 240
if ($hud.chunk) { "  $($hud.chunk)" }
"===TEXT==="
$hud.text
"===INTENTS==="
$hud.intents | Format-Table id, type, label, command -AutoSize | Out-String -Width 240
