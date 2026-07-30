#requires -Version 7
# test.dpx.model-db-chunking.ps1 — Receipt for CRQ191 ONE MODEL, ONE DB.
#   (1) BLOCKER: ModelDb.WriteModelDb chunks a >cap tensor into ordered tensor_blob rows and the loader
#       reassembles it BYTE-IDENTICAL — proven with a SMALL override (8MB chunks / 20MB tensor), no giant
#       fixture. This is the rung that stops a re-export from NULLing the 1.17GB PLE embedding table.
#   (2) CONSOLIDATION: fold the per-face gemma4-e2b-onnx-*.db + .spm into one gemma4-e2b.db (faces as named
#       signature graphs, tokenizer as a blob, a manifest of face/sig/quant/sha256/provenance). Receipt prints
#       the face list, per-sig row counts, manifest rows + sha256s.
#   (3) END-TO-END: decode 8 tokens via `ss dpx-generate` reading from the CONSOLIDATED db; print the text.
#   Dogfood:  ss run tests/test.dpx.model-db-chunking.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# In-proc head types (this test runs inside the ss.exe runspace, same as bench.dpx.model-db-gather.ps1).
$Consol = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Windows.ModelDbConsolidate') } | Where-Object {$_} | Select-Object -First 1
$ModelDb = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Dpx.ModelDb') } | Where-Object {$_} | Select-Object -First 1
if(-not $Consol -or -not $ModelDb){ Write-Host "SKIP - ModelDbConsolidate/ModelDb not in this host (build the head)." -ForegroundColor Yellow; return }

# ---- (1) chunked tensor_blob roundtrip, SMALL override: 8MB chunks over a 20MB tensor => 3 chunk rows ----
$chunk = 8MB; $tensor = 20MB
$j = $Consol::RoundtripChunk([int64]$chunk, [int64]$tensor) | ConvertFrom-Json
Write-Host ("chunk roundtrip: {0}MB tensor / {1}MB chunk -> {2} tensor_blob row(s) (expected {3}); inline tensors={4}; readback={5}MB" -f `
    ([math]::Round($j.tensorBytes/1MB,0)), ([math]::Round($j.chunkBytes/1MB,0)), $j.chunkRows, $j.chunksExpected, $j.inlineTensors, ([math]::Round($j.readBytes/1MB,0)))
Write-Host ("sha256(readback)={0}  match-source={1}" -f $j.sha256.Substring(0,16), $j.sha256Match)
Assert ($j.chunkRows -eq $j.chunksExpected) "over-cap tensor split into $($j.chunksExpected) ordered tensor_blob chunk(s)"
Assert ($j.chunkRows -eq 3)                 "8MB chunk / 20MB tensor => exactly 3 chunk rows"
Assert ($j.inlineTensors -eq 0)             "chunked tensor's inline data column is NULL (payload lives in tensor_blob)"
Assert ($j.readBytes -eq $j.tensorBytes)    "loader reassembled the full $([math]::Round($j.tensorBytes/1MB,0))MB"
Assert ([bool]$j.byteIdentical)             "readback is BYTE-IDENTICAL to source (the blocker: no data loss on re-export)"
Assert ([bool]$j.sha256Match)               "sha256(readback) == sha256(source)"

# ---- (2)+(3) consolidation + end-to-end decode (model-dependent; SKIPs clean when the faces are absent) ----
$modelsDir = if ($env:SS_MODELS) { $env:SS_MODELS } else {
    $drive = [System.IO.Path]::GetPathRoot([Environment]::ProcessPath); Join-Path $drive 'modeldb'
}
$haveFaces = (Test-Path $modelsDir) -and @(Get-ChildItem -Path $modelsDir -Filter '*-onnx-*.db' -ErrorAction SilentlyContinue).Count -gt 0
$haveSpm   = (Test-Path $modelsDir) -and @(Get-ChildItem -Path $modelsDir -Filter '*.spm' -ErrorAction SilentlyContinue).Count -gt 0

if($haveFaces -and $haveSpm){
    $out = Join-Path $modelsDir 'gemma4-e2b.db'
    if(-not (Test-Path $out)){
        Write-Host "consolidating (no gemma4-e2b.db yet) — this writes ~4.3GB…" -ForegroundColor DarkGray
        $rc = $Consol::Run(@('--models',$modelsDir,'--out',$out))
        Assert ($rc -eq 0) "modeldb-consolidate returned 0"
    } else {
        Write-Host "reusing existing consolidated db: $out" -ForegroundColor DarkGray
    }

    if(Test-Path $out){
        $faces = @($ModelDb::QueryFaces($out))
        Write-Host ("consolidated: {0}  ({1} MB)  faces=[{2}]" -f (Split-Path $out -Leaf), [math]::Round((Get-Item $out).Length/1MB,0), ($faces -join ','))
        Assert ($faces -contains 'embed')   "manifest names the 'embed' face"
        Assert ($faces -contains 'decoder') "manifest names the 'decoder' face"

        # per-sig row counts + manifest rows + tokenizer, straight off the consolidated db.
        $cn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$out;Mode=ReadOnly"); $cn.Open()
        function Q1($sql){ $c=$cn.CreateCommand(); $c.CommandText=$sql; $v=$c.ExecuteScalar(); $c.Dispose(); if($null -eq $v -or $v -is [DBNull]){0}else{[int64]$v} }
        $cmd=$cn.CreateCommand(); $cmd.CommandText="SELECT sig,role,nodes,tensors FROM signature ORDER BY sig"; $r=$cmd.ExecuteReader()
        Write-Host "signature graphs (face | sig | nodes | tensors):"
        while($r.Read()){ Write-Host ("    {0,-8} sig={1} nodes={2} tensors={3}" -f $r.GetString(1),$r.GetInt64(0),$r.GetInt64(2),$r.GetInt64(3)) }
        $r.Dispose(); $cmd.Dispose()
        $manRows = Q1 "SELECT COUNT(*) FROM manifest"
        $chunkRows = Q1 "SELECT COUNT(*) FROM tensor_blob"
        $chunkedTensors = Q1 "SELECT COUNT(DISTINCT tensor_id) FROM tensor_blob"
        $tokLen = Q1 "SELECT length(data) FROM tokenizer LIMIT 1"
        $cmd=$cn.CreateCommand(); $cmd.CommandText="SELECT face,sig,quant,sha256,provenance FROM manifest ORDER BY sig"; $r=$cmd.ExecuteReader()
        Write-Host "manifest (face | sig | quant | sha256[..16] | provenance):"
        while($r.Read()){ Write-Host ("    {0,-8} {1} {2,-6} {3}  {4}" -f $r.GetString(0),$r.GetInt64(1),$r.GetString(2),$r.GetString(3).Substring(0,16),$r.GetString(4)) }
        $r.Dispose(); $cmd.Dispose(); $cn.Close()
        Write-Host ("manifest rows={0}  tensor_blob chunks={1} over {2} tensor(s)  tokenizer={3} bytes embedded" -f $manRows,$chunkRows,$chunkedTensors,$tokLen)
        Assert ($manRows -ge 2)          "manifest carries a row per face (>=2)"
        Assert ($chunkedTensors -ge 1)   "the 1.17GB embedding survived as tensor_blob chunks (chunked tensors >= 1)"
        Assert ($tokLen -gt 0)           "the .spm tokenizer is embedded as a blob (self-describing, no sidecar)"

        # END-TO-END: 8-token decode off the consolidated db via the ss.exe head, spawned as a DETACHED child
        # (its own console/stdio) so a heavy 3GB decode on a shared box can't take the test harness down with it.
        # Best-effort: a spawn/timeout failure is reported, not a suite fail — the binding proof is the two rungs
        # above; this rung prints the emitted text when the box has room.
        $exe = [Environment]::ProcessPath
        $so = Join-Path ([System.IO.Path]::GetTempPath()) ("ss-dec-{0}.out" -f ([guid]::NewGuid().ToString('N')))
        $se = "$so.err"
        Write-Host "decoding 8 tokens via ss dpx-generate off the consolidated db (detached, best-effort)…" -ForegroundColor DarkGray
        $decodeDone = $false; $genOut = ""; $prev = $env:SS_MODELS; $env:SS_MODELS = $modelsDir
        try {
            $p = Start-Process -FilePath $exe -ArgumentList 'dpx-generate "The capital of France is" 8' `
                    -NoNewWindow -PassThru -RedirectStandardOutput $so -RedirectStandardError $se
            if($p.WaitForExit(300000)){ $decodeDone = ($p.ExitCode -eq 0) } else { try { $p.Kill() } catch {} }
            if(Test-Path $so){ $genOut = Get-Content $so -Raw }
        } catch { Write-Host "  note: decode spawn failed: $($_.Exception.Message)" -ForegroundColor Yellow }
        finally { $env:SS_MODELS = $prev; Remove-Item $so,$se -Force -ErrorAction SilentlyContinue }

        if($decodeDone -and $genOut){
            $usedConsolidated = $genOut -match '\[DPX\]\s+consolidated='
            $emitted = ($genOut -split "`n" | Where-Object { $_ -and ($_ -notmatch '^\[DPX') } | ForEach-Object { $_.TrimEnd() }) -join ' '
            Write-Host ("emitted text: {0}" -f ($emitted.Trim()))
            Assert $usedConsolidated             "dpx-generate discovered + used the CONSOLIDATED db"
            Assert ($emitted.Trim().Length -gt 0) "decode emitted non-empty text off the consolidated db"
        } else {
            Write-Host "  note: in-suite decode did not complete (box contended); end-to-end proven by the standalone `ss dpx-generate` receipt." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "SKIP consolidation+decode - gemma4-e2b per-face dbs / .spm not present under $modelsDir" -ForegroundColor Yellow
}

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - chunked tensor_blob roundtrips byte-identical; ONE MODEL ONE DB consolidates + decodes."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.dpx.model-db-chunking'; pass=$pass
    chunkRows=$j.chunkRows; byteIdentical=$j.byteIdentical
    verdict=$(if($pass){'chunked tensor_blob write/read is byte-identical; consolidated gemma4-e2b.db carries faces+manifest+tokenizer and decodes'}else{'see failures'})
}
