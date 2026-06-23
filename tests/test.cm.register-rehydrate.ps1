#requires -Version 7
# test.cm.register-rehydrate.ps1 — Proves the Cm registry projects truth into both planes: a capability
# Register lands in-memory (Get) AND in the durable list (List), and Unregister removes it. (The
# durable SQLite rehydration across a cold restart is proven by ss selftest's Cm.Rehydration.)
# Authority = the binary; this comment is not. Dot-source-safe; registers a throwaway marker it removes.
#   Dogfood:  ss run tests/test.cm.register-rehydrate.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$Cm = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Cm.Cm') } | Where-Object {$_} | Select-Object -First 1
$recType = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Cm.CapabilityRecord') } | Where-Object {$_} | Select-Object -First 1
if(-not $Cm -or -not $recType){ Write-Host "Subsystem.Cm not loaded." -ForegroundColor Red; return }

$path = "\Capability\__cmtest_$(Get-Date -f HHmmssfff)"
try {
    $rec = [Activator]::CreateInstance($recType)
    $rec.Path = $path; $rec.Name = 'CmProbe'; $rec.Type = 'Probe'; $rec.Integrity = 'System'; $rec.StartType = 'manual'; $rec.Enabled = $true
    $Cm::Register($rec)
    $got = $Cm::Get($path)
    $inList = @($Cm::List() | Where-Object { $_.Path -eq $path }).Count -ge 1
    $dbPath = $Cm::DbPath
    [void]$Cm::Unregister($path)
    $gone = $null -eq $Cm::Get($path)
    Write-Host "cm: db=$dbPath registered=$($null -ne $got) inList=$inList removed=$gone"
    Assert ($null -ne $got) "Register landed the capability in-memory (Get resolves it)"
    Assert $inList          "the capability appears in the durable registry list"
    Assert $gone            "Unregister removed it cleanly (no residue)"
} catch { Write-Host "  FAIL cm: $($_.Exception.Message)" -ForegroundColor Red; $fails.Add('exception') ; try { [void]$Cm::Unregister($path) } catch {} }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — Cm registry: register projects to memory + durable list; unregister is clean."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.cm.register-rehydrate'; pass=$pass; verdict=$(if($pass){'register -> memory + durable list; clean unregister'}else{'see failures'}) }
