[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Key,

    [Parameter(Position = 1)]
    [ValidateSet('global', 'system', 'secure')]
    [string]$Scope = 'global'
)

if (-not [string]::IsNullOrWhiteSpace($Key)) {
    $val = Invoke-AdbShell "settings get $Scope $Key"
    if ($null -ne $val) {
        $val = $val.Trim()
        if ($val -eq 'null') { return $null }
        [pscustomobject]@{
            Scope = $Scope
            Key   = $Key
            Value = $val
        }
    }
} else {
    $list = Invoke-AdbShell "settings list $Scope" | ConvertFrom-Settings
    if ($list) {
        foreach ($item in $list) {
            [pscustomobject]@{
                Scope = $Scope
                Key   = $item.Key
                Value = $item.Value
            }
        }
    }
}
