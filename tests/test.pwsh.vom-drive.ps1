#requires -Version 7
# test.pwsh.vom-drive.ps1 — Proves the vom: drive PowerShell provider projects the live VOM.
# Projects the live VOM kernel as a navigable sessions/capability/device namespace.
# Runs via ss (the hosted runspace).

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# 1. Create live VOM owner and handles to test navigation
$ownerPath = "\Sessions\vom-drive-test-owner"
$owner = [Subsystem.Vom.Vom]::CreateOwner($ownerPath, 1024*1024, 100)
Assert ($null -ne $owner) "created VOM owner: $ownerPath"

$h1 = [Subsystem.Vom.Vom]::Alloc($owner, 256, [Subsystem.Vom.VomFormat]::Float32, "TestRegionFloat", $false, "Objects", "h1")
$h2 = [Subsystem.Vom.Vom]::Alloc($owner, 512, [Subsystem.Vom.VomFormat]::Bytes, "TestRegionBytes", $false, "Objects", "h2")
Assert ($null -ne $h1) "allocated h1 (Float32, 256B)"
Assert ($null -ne $h2) "allocated h2 (Bytes, 512B)"

# 2. Test navigation locally in the console host
$rootItems = Get-ChildItem vom:\
Assert ($rootItems.Count -gt 0) "Get-ChildItem vom:\ returned $($rootItems.Count) items"
Assert ($null -ne ($rootItems | Where-Object { $_.Path -eq "\Sessions" })) "found \Sessions namespace at root"

$ownerItems = Get-ChildItem vom:\Sessions
Assert ($null -ne ($ownerItems | Where-Object { $_.Path -eq $ownerPath })) "found test owner $ownerPath under vom:\Sessions"

$handleItems = Get-ChildItem vom:\Sessions\vom-drive-test-owner\Objects
Assert ($handleItems.Count -eq 2) "Get-ChildItem vom:\Sessions\vom-drive-test-owner\Objects returned 2 handles"

$node1 = $handleItems | Where-Object { $_.Name -eq "h1" }
Assert ($null -ne $node1) "found h1 node"
Assert ($node1.Type -eq "TestRegionFloat") "h1 has correct Type: $($node1.Type)"
Assert ($node1.Format -eq "Float32") "h1 has correct Format: $($node1.Format)"
Assert ($node1.Bytes -eq 256) "h1 has correct Bytes: $($node1.Bytes)"

$node2 = $handleItems | Where-Object { $_.Name -eq "h2" }
Assert ($null -ne $node2) "found h2 node"
Assert ($node2.Type -eq "TestRegionBytes") "h2 has correct Type: $($node2.Type)"
Assert ($node2.Format -eq "Bytes") "h2 has correct Format: $($node2.Format)"
Assert ($node2.Bytes -eq 512) "h2 has correct Bytes: $($node2.Bytes)"

# Clean up VOM owner locally
[Subsystem.Vom.Vom]::Terminate($owner)
Assert ($null -eq [Subsystem.Vom.Vom]::GetOwner($ownerPath)) "VOM owner terminated and cleaned up"

# 3. Test that it works identically over MCP
$ssExe = [System.Environment]::ProcessPath
Write-Host "testing MCP call using: $ssExe"

$mcpCmd = @'
$mcpOwnerPath = "\Sessions\mcp-test"
$mcpOwner = [Subsystem.Vom.Vom]::CreateOwner($mcpOwnerPath, 1024*1024, 100)
$h = [Subsystem.Vom.Vom]::Alloc($mcpOwner, 256, [Subsystem.Vom.VomFormat]::Float32, "McpFloat", $false, "Objects", "h1")
Get-ChildItem vom:\Sessions\mcp-test\Objects | ConvertTo-Json
[Subsystem.Vom.Vom]::Terminate($mcpOwner)
'@

$mcpOutputRaw = & $ssExe mcp call ss_run -command $mcpCmd
Assert ($null -ne $mcpOutputRaw) "MCP command returned output"

# Filter out Dg log lines from output before parsing JSON
$cleanJson = ($mcpOutputRaw -split "`r?`n" | Where-Object { $_ -notmatch '^\d{2}:\d{2}:\d{2} \[' }) -join "`n"
$mcpJson = $cleanJson | ConvertFrom-Json
Assert ($null -ne $mcpJson) "parsed MCP output as JSON"

# In PowerShell, if 1 item is returned, ConvertFrom-Json can return an object or array. Ensure we check it.
$mcpNodes = @($mcpJson)
Assert ($mcpNodes.Count -eq 1) "MCP returned exactly 1 handle node"

$mcpH1 = $mcpNodes[0]
Assert ($null -ne $mcpH1) "MCP output contains h1"
Assert ($mcpH1.Type -eq "McpFloat") "MCP h1 Type: $($mcpH1.Type)"
Assert ($mcpH1.Format -eq "Float32") "MCP h1 Format: $($mcpH1.Format)"
Assert ($mcpH1.Bytes -eq 256) "MCP h1 Bytes: $($mcpH1.Bytes)"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — vom: PowerShell provider projects VOM live namespace successfully."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})

[pscustomobject]@{ test='test.pwsh.vom-drive'; pass=$pass; verdict=$(if($pass){'VOM drive navigable, handles project correct Type/Format/Bytes'}else{'failures observed'}) }
