[CmdletBinding()]
param()
Invoke-AdbShell 'dumpsys netstats' | ConvertFrom-DumpsysTree
