# Object-driven harness: load the controller as a library, perceive the web through it.
. 'S:\bin\firefox\firefox.work.ps1' -AsLibrary

Initialize-RDP | Out-Null
Execute-Command '/goto https://google.com/ai'

$hud = Get-Hud
$hud | ConvertTo-Json -Depth 6 | Set-Content -Path 'S:\bin\firefox\_hud.json' -Encoding utf8
"`n===HUD OBJECT==="
$hud | Format-List url, title, objective
"intents: $($hud.intents.Count)"
