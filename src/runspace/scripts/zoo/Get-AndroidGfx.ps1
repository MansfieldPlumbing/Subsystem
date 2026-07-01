[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Package
)
$cmd = "dumpsys gfxinfo"
if (-not [string]::IsNullOrWhiteSpace($Package)) {
    $cmd += " $Package"
}
Invoke-AdbShell $cmd | ConvertFrom-DumpsysTree
