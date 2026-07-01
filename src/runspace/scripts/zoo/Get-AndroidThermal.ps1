[CmdletBinding()]
param()
Invoke-AdbShell 'dumpsys thermalservice' | ConvertFrom-DumpsysTree
