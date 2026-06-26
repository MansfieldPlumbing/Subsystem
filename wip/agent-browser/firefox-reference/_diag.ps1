. 'S:\bin\firefox\firefox.work.ps1' -AsLibrary
Initialize-RDP | Out-Null
$js = @'
(function(){
  var ces = Array.from(document.querySelectorAll('[contenteditable="true"]'));
  var out = [];
  out.push("contenteditables=" + ces.length);
  var ce = ces[ces.length-1];
  if (ce){
    ce.focus();
    document.execCommand('selectAll',false,null);
    document.execCommand('insertText',false,'diag probe text');
    out.push("editorText=[" + (ce.innerText||"").slice(0,60) + "]");
    out.push("editorTag=" + ce.tagName + " cls=" + (ce.className||"").slice(0,40));
  }
  var sb = document.querySelector('[data-testid="send-button"]');
  out.push("sendBtn=" + (!!sb) + " disabled=" + (sb?sb.disabled:"n/a"));
  var ta = document.querySelector('textarea');
  out.push("textarea=" + (!!ta));
  out.push("btns=" + Array.from(document.querySelectorAll('button')).map(function(b){return (b.getAttribute("data-testid")||b.getAttribute("aria-label")||"").trim();}).filter(function(x){return x;}).slice(0,20).join(" | "));
  return out.join("\n");
})()
'@
$State.LastEvalResult = $null
Send-Packet -To $State.CurrentTab.Console -Type "evaluateJSAsync" -Data @{ text = $js }
$t = Get-Date
while ((Get-Date) - $t -lt [TimeSpan]::FromSeconds(5)) {
    Process-BackgroundEvents
    if ($State.LastEvalResult -and $State.LastEvalResult.result) { break }
    Start-Sleep -Milliseconds 50
}
"===DIAG==="
Expand-Grip $State.LastEvalResult.result
