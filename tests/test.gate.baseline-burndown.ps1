#requires -Version 7
# test.gate.baseline-burndown.ps1 — Receipt for CRQ182's baseline burndown (SS007/SS009/SS018/SS019).
# Authority = the binary. This comment is not authority; the receipt the run prints is.
# Runs `ss.exe check --gate` over this worktree and asserts GREEN (new=0) — the ratchet holds: no
# fresh violation was introduced. Then runs `ss.exe check` (the full roster, grouped `=== SSxxx (N) ===`
# per rule) and compares the live per-rule count for SS007/SS009/SS018/SS019 against both CRQ182's
# recorded assignment counts (SS007 132, SS009 31, SS018 1, SS019 1) and the count still parked in
# src/analyzers/SS-BASELINE.txt for those same 4 rules — a before/after burndown table.
# Dot-source-safe; no mutation (read-only `ss.exe check` invocations).
#   Dogfood:  ss run tests/test.gate.baseline-burndown.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$ssExe = "S:\subsystem\ss.exe"
$repo  = "S:\subsystem\.claude\worktrees\vigorous-greider-7bdad9"
if(-not (Test-Path $ssExe)){ Write-Host "SKIP - $ssExe not found." -ForegroundColor Yellow; return }

# --- 1) the gate: GREEN, new = 0 -------------------------------------------------------------
$gateOut = & $ssExe check --gate --path $repo 2>&1 | Out-String
$gateLine = ($gateOut -split "`r?`n" | Where-Object { $_ -match '^gate:\s+\d+\s+findings;\s+baseline\s+\d+;\s+new\s+\d+;\s+retired\s+\d+' } | Select-Object -First 1)
Assert ($null -ne $gateLine) "gate summary line found ('gate: N findings; baseline N; new N; retired N')"

$gFindings = $gBaseline = $gNew = $gRetired = -1
if($gateLine -match 'gate:\s+(\d+)\s+findings;\s+baseline\s+(\d+);\s+new\s+(\d+);\s+retired\s+(\d+)'){
    $gFindings = [int]$Matches[1]; $gBaseline = [int]$Matches[2]; $gNew = [int]$Matches[3]; $gRetired = [int]$Matches[4]
}
$isGreen = ($gateOut -match 'gate:\s+GREEN\s+—\s+no new violations\.') -or ($gNew -eq 0)
Write-Host "gate: $gateLine"
Write-Host "gate: green=$isGreen findings=$gFindings baseline=$gBaseline new=$gNew retired=$gRetired  (expect new=0)"
Assert ($gNew -eq 0)   "gate 'new' is 0 — no fresh (non-baselined) violation introduced"
Assert ($isGreen)      "gate prints GREEN (no new violations)"

# --- 2) the burndown table for SS007 / SS009 / SS018 / SS019 --------------------------------
# CRQ182's assignment text recorded these counts at claim time (the "before" for this receipt):
$assigned = [ordered]@{ SS007 = 132; SS009 = 31; SS018 = 1; SS019 = 1 }

# Live count per rule: `ss check` (no --gate) groups the full roster as `=== SSxxx (N) — Title ===`;
# the (N) IS the live count of that rule's findings — the console equivalent of grepping `SSxxx|`
# lines out of the pipe-delimited baseline key format (SSxxx|file|message).
$rosterOut = & $ssExe check --path $repo 2>&1 | Out-String
$rosterLines = $rosterOut -split "`r?`n"

$baselinePath = Join-Path $repo 'src\analyzers\SS-BASELINE.txt'
if(-not (Test-Path $baselinePath)){ Write-Host "SS-BASELINE.txt not found at $baselinePath." -ForegroundColor Red; return }
$baselineLines = Get-Content $baselinePath

$rows = [System.Collections.Generic.List[object]]::new()
foreach($rule in $assigned.Keys){
    $hdr = $rosterLines | Where-Object { $_ -match "^===\s+$rule\s+\((\d+)\)" } | Select-Object -First 1
    $live = if($hdr -and $hdr -match '\((\d+)\)'){ [int]$Matches[1] } else { 0 }
    $baselined = @($baselineLines | Where-Object { $_ -match "^$rule\|" }).Count
    $rows.Add([pscustomobject]@{ rule=$rule; assignedCount=$assigned[$rule]; liveCount=$live; baselineCount=$baselined })
}

Write-Host ""
Write-Host "burndown (CRQ182 SS007/SS009/SS018/SS019):"
Write-Host ("  {0,-6} {1,-14} {2,-10} {3,-14}" -f 'rule','assigned(before)','live(now)','baseline(now)')
foreach($r in $rows){
    Write-Host ("  {0,-6} {1,-14} {2,-10} {3,-14}" -f $r.rule, $r.assignedCount, $r.liveCount, $r.baselineCount)
}

foreach($r in $rows){
    Assert ($r.liveCount -eq $r.assignedCount)     "$($r.rule) live count ($($r.liveCount)) matches CRQ182's recorded assignment count ($($r.assignedCount))"
    Assert ($r.baselineCount -eq $r.assignedCount) "$($r.rule) SS-BASELINE.txt entries ($($r.baselineCount)) still match the assigned count ($($r.assignedCount)) — no silent baseline drift"
}

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — gate is GREEN (new=0); SS007/SS009/SS018/SS019 live + baseline counts match CRQ182's recorded assignment (132/31/1/1)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.gate.baseline-burndown'; pass=$pass
    gateGreen=$isGreen; gateFindings=$gFindings; gateBaseline=$gBaseline; gateNew=$gNew; gateRetired=$gRetired
    rows=$rows
    verdict=$(if($pass){'gate GREEN, new=0; SS007/SS009/SS018/SS019 unchanged at 132/31/1/1 (live + SS-BASELINE.txt)'}else{'see failures'})
}
