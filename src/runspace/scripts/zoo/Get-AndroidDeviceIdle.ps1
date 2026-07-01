[CmdletBinding()]
param()
Invoke-AdbShell 'dumpsys deviceidle' | ConvertFrom-DumpsysTree
