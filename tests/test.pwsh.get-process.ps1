#requires -Version 7
# test.pwsh.get-process.ps1 — Receipt that the in-process shell is real PowerShell 7 on CoreCLR/.NET,
# with full reflection and live OS access. This is the category proof: NOT Mono, NOT AOT (those cannot
# host full pwsh). Authority = the binary; the receipt the run prints is the authority, not this comment.
#   Dogfood:  ss run tests/test.pwsh.get-process.ps1
#
#   PWSH      $PSVersionTable says PowerShell Core 7.x (7.6 legacy / 7.7 current). Asserted.
#   CORECLR   RuntimeInformation.FrameworkDescription says .NET (CoreCLR), NOT Mono. Asserted.
#   GET-PROC  Get-Process returns live System.Diagnostics.Process objects; this host resolves by PID. Asserted.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# PWSH — PowerShell Core 7.x
$v=$PSVersionTable.PSVersion; $ed=$PSVersionTable.PSEdition
Write-Host "PWSH      version=$v edition=$ed"
Assert ($ed -eq 'Core') "edition is Core (CoreCLR-hosted shell, not Desktop)"
Assert ($v.Major -eq 7) "PowerShell 7.x ($v)"

# CORECLR — the runtime is CoreCLR/.NET, NOT Mono (the category receipt)
$fw=[System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
Write-Host "CORECLR   framework='$fw'"
Assert ($fw -like '.NET*' -and $fw -notlike '*Mono*') "runtime is CoreCLR/.NET, not Mono ('$fw')"

# GET-PROC — a stock cmdlet reaches the live OS through the full BCL + reflection
$procs=Get-Process; $self=Get-Process -Id $PID
Write-Host "GET-PROC  $($procs.Count) live processes; this host='$($self.ProcessName)' pid=$PID"
Assert ($procs.Count -gt 0) "Get-Process returned live OS Process objects"
Assert ($self -and $self.Id -eq $PID) "resolved THIS process by PID ($PID) — a real System.Diagnostics.Process"

$pass=$fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — real PowerShell $v on $fw, full reflection, live OS; not Mono, not AOT."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.pwsh.get-process'; pass=$pass
    pwsh="$v"; edition="$ed"; framework=$fw; processes=$procs.Count
    verdict=$(if($pass){'in-process PowerShell 7 on CoreCLR/.NET — full reflection, live OS access; NOT Mono-AOT'}else{'see failures'})
}
