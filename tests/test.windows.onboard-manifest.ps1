#requires -Version 7
# test.windows.onboard-manifest.ps1 — Proves `ss onboard` emits the FULL file manifest as a mandatory
# section (CRQ178): every file in the tree, all extensions, so a cold session cannot truthfully claim it
# did not see that a file exists. Also proves the whole onboard brief fits the 40k MCP clip, so the
# manifest (or the dedication after it) can never be the part that gets cut.
# Authority = the binary; this comment is not. Run via ss (the hosted runspace). Dot-source-safe.
#   Dogfood:  ss run tests/test.windows.onboard-manifest.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$exe  = [Environment]::ProcessPath
$repo = Split-Path $PSScriptRoot -Parent
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$out = (& $exe onboard --path $repo 2>&1) -join "`n"

$hasSection = $out.Contains('THE FULL MANIFEST')
$countLine  = [regex]::Match($out, '(\d+) files · every file under')
$count      = if ($countLine.Success) { [int]$countLine.Groups[1].Value } else { 0 }
$chars      = $out.Length

# Breadth sentinels — one from each corner of the tree the old .cs-only keyhole missed.
$sentinels = 'Onboard.cs', 'LiveMap.cs', 'directport.h', 'test.windows.onboard-manifest.ps1'
Write-Host "onboard: $chars chars total ; manifest: $count files ; section present: $hasSection"
Assert $hasSection                    "onboard emits the FILES manifest section (mandatory, not a follow-up)"
Assert ($count -gt 500)               "manifest counts the whole tree ($count files > 500 — all extensions, not just .cs)"
foreach ($s in $sentinels) { Assert ($out.Contains($s)) "manifest lists $s" }
Assert (-not $out.Contains('.claude/worktrees/')) "nested worktree copies are pruned (no .claude/worktrees/ entries)"
Assert ($chars -lt 40000)             "whole brief fits the 40k MCP clip ($chars chars) — nothing gets cut off"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — onboard shows every file in the tree; the keyhole is closed."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.windows.onboard-manifest'; pass=$pass; files=$count; chars=$chars; verdict=$(if($pass){"manifest mandatory in onboard: $count files in $chars chars, under the 40k clip"}else{'see failures'}) }
