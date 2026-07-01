[CmdletBinding()]
param()

$raw = Invoke-AdbShell 'dumpsys cpuinfo'
$lines = $raw -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$load = $null
if ($lines.Count -gt 0 -and $lines[0] -match 'Load:\s*(.*)') {
    $load = $Matches[1].Trim()
}

$totalCpu = $null
$totalLine = $lines | Where-Object { $_ -match 'TOTAL:' } | Select-Object -First 1
if ($totalLine -and $totalLine -match '^\s*([0-9.]+%)\s*TOTAL:') {
    $totalCpu = $Matches[1]
}

$procObjects = [System.Collections.Generic.List[PSCustomObject]]::new()
foreach ($line in $lines) {
    if ($line -match '^\s*([0-9.]+%)\s+(\d+)/([^:]+):\s*(.*)$') {
        $usage = $Matches[1]
        $procId = $Matches[2]
        $name = $Matches[3]
        $details = $Matches[4].Trim()
        [void]$procObjects.Add([pscustomobject]@{
            Usage = $usage
            ProcessId = $procId
            Name = $name
            Details = $details
        })
    }
}

[pscustomobject]@{
    Load = $load
    TotalCpu = $totalCpu
    Processes = $procObjects.ToArray()
}
