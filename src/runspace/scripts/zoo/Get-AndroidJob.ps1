[CmdletBinding()]
param()
Invoke-AdbShell 'dumpsys jobscheduler' | ConvertFrom-DumpsysTree
