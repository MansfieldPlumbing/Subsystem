#requires -Version 7
# test.pwsh.project-cmdlets.ps1 — Proves ss is a pwsh SUPERSET: the project's own cmdlets are loaded
# into the hosted runspace alongside every built-in. (COLD-START: in-process pwsh 7.7 + project cmdlets.)
# Authority = the binary; this comment is not. Run via ss (the hosted runspace). Dot-source-safe.
#   Dogfood:  ss run tests/test.pwsh.project-cmdlets.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$expected = 'Get-CodeContext','Get-GitGraphContext','Invoke-ModelAnalysis','Restore-CodeContext'
$found = @($expected | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue })
$psVer = $PSVersionTable.PSVersion.ToString()
$builtinOk = $null -ne (Get-Command Get-Process -ErrorAction SilentlyContinue)
Write-Host "pwsh: v$psVer ; project cmdlets found $($found.Count)/$($expected.Count): $($found -join ', ')"
Assert $builtinOk                          "a pwsh built-in (Get-Process) is present (it IS a real pwsh host)"
Assert ($found.Count -eq $expected.Count)  "all $($expected.Count) project cmdlets are loaded in the runspace"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — ss is a pwsh superset: built-ins + the project cmdlets in one hosted runspace."}else{"FAIL ($($fails.Count)): $($fails -join '; ') — run via ss (the project cmdlets only load in the hosted runspace)."})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.pwsh.project-cmdlets'; pass=$pass; psVersion=$psVer; projectCmdlets=$found.Count; verdict=$(if($pass){'pwsh superset: built-ins + project cmdlets loaded'}else{'see failures'}) }
