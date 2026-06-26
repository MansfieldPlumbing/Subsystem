$Q = (Get-Content -LiteralPath 'S:\bin\firefox\_q.txt' -Raw).Trim()
. 'S:\bin\firefox\firefox.work.ps1' -AsLibrary
Initialize-RDP | Out-Null

# Continue the existing AI Mode thread by typing into the on-page box.
Execute-Command "/type $Q"
Start-Sleep -Seconds 9
Invoke-PageExtraction
$hud = Get-Hud

"===PAGE: $($hud.page)==="
"You asked: $Q"
""
$hud.text
