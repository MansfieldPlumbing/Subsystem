# Perceive -> act -> perceive, all through objects. HUD rendered as k/v + table, no JSON.
. 'S:\bin\firefox\firefox.work.ps1' -AsLibrary

Initialize-RDP | Out-Null

if ($State.CurrentTab.Url -notmatch 'google\.com') {
    Execute-Command '/goto https://google.com/ai'
}

# ACT: type a question into the AI box and submit
Execute-Command '/type What year did the Voyager 1 probe launch? Answer in one sentence.'

Start-Sleep -Seconds 8
Invoke-PageExtraction
$hud = Get-Hud

"===HUD (k/v)==="
$kv = [ordered]@{
    page      = $hud.page
    title     = $hud.title
    objective = $hud.objective
    intents   = $hud.intents.Count
}
$kv.GetEnumerator() | Format-Table -HideTableHeaders Key, Value | Out-String -Width 200

"===PAGE TEXT==="
$hud.text

"===INTENTS==="
$hud.intents | Format-Table id, type, label, command -AutoSize | Out-String -Width 200
