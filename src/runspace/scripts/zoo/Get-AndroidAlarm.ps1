[CmdletBinding()]
param()
Invoke-AdbShell 'dumpsys alarm' | ConvertFrom-DumpsysTree
