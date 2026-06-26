. 'S:\bin\firefox\firefox.work.ps1' -AsLibrary
Initialize-RDP | Out-Null
Invoke-PageExtraction
$hud = Get-Hud

"===HUD==="
$kv = [ordered]@{ page = $hud.page; title = $hud.title; intents = $hud.intents.Count }
$kv.GetEnumerator() | Format-Table -HideTableHeaders Key, Value | Out-String -Width 200
"===ANSWER (shaped text)==="
$hud.text
"===INTENTS==="
$hud.intents | Format-Table id, type, label, command -AutoSize | Out-String -Width 200
