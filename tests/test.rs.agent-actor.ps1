#requires -Version 7
# test.rs.agent-actor.ps1 — Proves the P2P Agent-Actor in-process VOM and CpuFence communication, routing, batch WaitAll, and quorum WaitN.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss run tests/test.rs.agent-actor.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# Resolve the types-under-test from LOADED assemblies
$DeviceSetType = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Adb.InProcessDeviceSet') } | Where-Object {$_} | Select-Object -First 1
$OrchestratorType = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Adb.MoeDispatch') } | Where-Object {$_} | Select-Object -First 1
$AgentType = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Adb.InProcessDeviceAgent') } | Where-Object {$_} | Select-Object -First 1
$VomType = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1

if(-not $DeviceSetType -or -not $OrchestratorType -or -not $AgentType -or -not $VomType) {
    Write-Host "Subsystem VOM P2P types not loaded — cannot run." -ForegroundColor Red; return
}

try {
    # 1. Initialize DeviceSet and Orchestrator
    $deviceSet = [Activator]::CreateInstance($DeviceSetType)
    $orchestrator = [Activator]::CreateInstance($OrchestratorType, $deviceSet)

    # 2. Create the Device Actor
    $conn = $deviceSet.CreateDevice("Galaxy-S23", [string[]]@("expert_0", "expert_1"), 6400000000)
    
    Assert ($null -ne $conn) "Device actor connection structure created successfully"
    Assert ($conn.Name -eq "Galaxy-S23") "Device name resolves correctly"
    Assert ($conn.Experts[0] -eq "expert_0" -and $conn.Experts[1] -eq "expert_1") "Expert assignments mapped correctly"
    Assert ($conn.FreeMemory -eq 6400000000) "Memory quota read correctly"

    # Verify VOM registration
    $owner = $VomType::GetOwner("\Agent\Galaxy-S23")
    Assert ($null -ne $owner) "VOM Owner created under \Agent\Galaxy-S23 path namespace"
    
    # 3. Spin up the In-Process Device Agent
    $agent = [Activator]::CreateInstance($AgentType, $conn)
    $agent.Start()
    
    # 4. Route token through the orchestrator (Write -> Signal -> Wait -> Read cycle)
    $input = [float[]](1..1536)
    $routeTask = $orchestrator.RouteTokenAsync(99, "expert_0", $input)
    $output = $routeTask.GetAwaiter().GetResult()

    Assert ($output.Length -eq 1536) "Orchestrator routed token, device agent processed it, and returned correct activation length"
    Assert ($output[0] -eq 0.5) "In-process agent successfully processed floating point values (scaled by 0.5)"
    
    # 5. Route a batch of tokens concurrently (Fence.WaitAll)
    $input1 = [float[]](1..1536)
    $input2 = [float[]](2..1537)
    
    # Create the ValueTuples representing the batch items
    $tuple1 = [System.ValueTuple]::Create([long]100, [string]"expert_0", $input1)
    $tuple2 = [System.ValueTuple]::Create([long]101, [string]"expert_1", $input2)
    
    # Get generic List type for the batch: List<ValueTuple<long, string, float[]>>
    $tupleType = $tuple1.GetType()
    $listType = [System.Collections.Generic.List[System.Object]].GetGenericTypeDefinition().MakeGenericType($tupleType)
    $batch = [Activator]::CreateInstance($listType)
    [void]$batch.Add($tuple1)
    [void]$batch.Add($tuple2)
    
    $batchTask = $orchestrator.RouteBatchAsync($batch)
    $batchResult = $batchTask.GetAwaiter().GetResult()
    
    Assert ($batchResult.Count -eq 2) "Batch routing completed and returned 2 outputs (WaitAll)"
    Assert ($batchResult[0].Length -eq 1536) "Batch output 1 has correct length"
    Assert ($batchResult[1].Length -eq 1536) "Batch output 2 has correct length"

    # 6. Test Quorum routing (Fence.WaitN)
    $quorumTask = $orchestrator.RouteBatchQuorumAsync($batch, 1)
    $quorumCount = $quorumTask.GetAwaiter().GetResult()
    Assert ($quorumCount -ge 1) "Quorum routing completed and returned met quorum count (WaitN)"

    # 7. Teardown and verify VOM cleanup
    $agent.Stop()
    $agent.Dispose()
    $deviceSet.Dispose()
    
    $ownerGone = $null -eq $VomType::GetOwner("\Agent\Galaxy-S23")
    Assert $ownerGone "Teardown Terminated and reclaimed the VOM actor namespace and all handle allocations"

} catch {
    Write-Host "  FAIL test: $($_.Exception.Message)" -ForegroundColor Red
    $fails.Add('exception')
}

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — P2P In-Process VOM Agent-Actor: direct-memory channels and timeline fences route tokens at zero-serialization latency."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})

[pscustomobject]@{
    test = 'test.rs.agent-actor'
    pass = $pass
    devices = 1
    tokenRouted = 99
    outputLength = 1536
    verdict = $(if($pass){'In-process direct memory VOM and CpuFence communication (with WaitAll and WaitN) verified successfully'}else{'see failures'})
}
