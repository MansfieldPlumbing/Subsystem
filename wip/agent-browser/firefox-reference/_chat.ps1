$Q = (Get-Content -LiteralPath 'S:\bin\firefox\_q.txt' -Raw).Trim()
. 'S:\bin\firefox\firefox.work.ps1' -AsLibrary
Initialize-RDP | Out-Null

if ($State.CurrentTab.Url -notmatch 'chatgpt\.com') { Execute-Command '/goto https://chatgpt.com' }
Execute-Command '/dismiss'
Start-Sleep -Seconds 1
Execute-Command "/type $Q"     # inserts into ProseMirror (early Enter may no-op)

Start-Sleep -Seconds 3         # let ProseMirror/React register text -> Send enables

# Explicit submit: prefer the now-enabled send button, else Enter on the editor.
$submit = @'
(function(){
  var ce = document.querySelector('.ProseMirror') || document.querySelector('[contenteditable="true"]');
  if (ce) ce.focus();
  var sb = document.querySelector('[data-testid="send-button"], button[aria-label*="Send"]');
  if (sb && !sb.disabled) { sb.click(); return "CLICKED_SEND"; }
  if (ce) {
    ['keydown','keyup'].forEach(function(t){ ce.dispatchEvent(new KeyboardEvent(t,{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true})); });
    return "ENTER sb=" + (sb?("disabled:"+sb.disabled):"none");
  }
  return "NO_TARGET";
})()
'@
$State.LastEvalResult = $null
Send-Packet -To $State.CurrentTab.Console -Type "evaluateJSAsync" -Data @{ text = $submit }
$t = Get-Date
while ((Get-Date) - $t -lt [TimeSpan]::FromSeconds(4)) {
    Process-BackgroundEvents
    if ($State.LastEvalResult -and $State.LastEvalResult.result) { break }
    Start-Sleep -Milliseconds 50
}
"[SUBMIT] $(Expand-Grip $State.LastEvalResult.result)"

Start-Sleep -Seconds 15        # let the answer stream
Invoke-PageExtraction
$hud = Get-Hud

"===PAGE: $($hud.page) | $($hud.device.time)==="
"You asked: $Q"
""
$hud.text
