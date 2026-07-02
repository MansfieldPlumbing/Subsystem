#requires -Version 7
# test.dpx.kokoro-android.ps1 — Kokoro-82M speech synthesis IN-PROC on the ANDROID head (CRQ193).
# ONE lane, two heads: DpxKokoro.Project (src/runspace/Dpx/DpxKokoro.cs) compiles into ss.exe AND the
# APK. A: this box synthesizes the reference utterance in-proc (re-proves the Windows lane + makes the
# reference wav). B: the S23 synthesizes the SAME inputs on-device — driven over the proven dev seam
# (adb push + run-as into private files/, /api/exec with the files/.cap token) — and the pulled wav is
# diffed sample-for-sample against the Windows one.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss -File tests/test.dpx.kokoro-android.ps1
# SKIPs (named): assets missing / adb missing / S23 not attached / app not installed.
# Env overrides: SS_KOKORO_ONNX, SS_KOKORO_IO, SS_KOKORO_SERIAL, SS_ADB.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$exe   = [Environment]::ProcessPath
$drive = Split-Path $exe -Qualifier

# ---- asset + tool discovery (drive-relative; env overrides) ----
$model  = if ($env:SS_KOKORO_ONNX)   { $env:SS_KOKORO_ONNX }   else { Join-Path $drive 'Kokoro-82M-v1.0-ONNX\onnx\model.onnx' }
$io     = if ($env:SS_KOKORO_IO)     { $env:SS_KOKORO_IO }     else { Join-Path $drive 'dp-onnx_qnn\laptop-handoff\ort-compare' }
$adb    = if ($env:SS_ADB)           { $env:SS_ADB }           else { Join-Path $drive 'bin\adb\adb.exe' }
$serial = if ($env:SS_KOKORO_SERIAL) { $env:SS_KOKORO_SERIAL } else { 'RFCWA0CE47F' }   # the S23; the razr is another track's
$pkg    = 'dev.mansfieldplumbing.subsystem'
$inputNames = @('input_ids','style','speed')

if (-not (Test-Path $model)) { Write-Host "SKIP - kokoro model missing: $model" -ForegroundColor Yellow; return [pscustomobject]@{ test='test.dpx.kokoro-android'; pass=$false; verdict='SKIP (model asset missing)' } }
$ioOk = $true; foreach ($n in $inputNames) { if (-not (Test-Path (Join-Path $io "$n.bin"))) { $ioOk = $false } }
if (-not $ioOk) { Write-Host "SKIP - reference inputs missing under $io" -ForegroundColor Yellow; return [pscustomobject]@{ test='test.dpx.kokoro-android'; pass=$false; verdict='SKIP (reference inputs missing)' } }

$K = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Dpx.DpxKokoro') } | Where-Object {$_} | Select-Object -First 1
if (-not $K) { Write-Host "Subsystem.Dpx.DpxKokoro type not found — cannot run." -ForegroundColor Red; return [pscustomobject]@{ test='test.dpx.kokoro-android'; pass=$false; verdict='DpxKokoro type absent from ss.exe' } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) 'ss-kokoro-android'
New-Item -ItemType Directory -Force $tmp | Out-Null

# ---- helpers: wav PCM16 samples, dp-onnx .bin floats, receipt parse ----
function Read-WavSamples([string]$path) {
    $raw = [IO.File]::ReadAllBytes($path)
    $p = 12; $off = -1; $sz = 0
    while ($p + 8 -le $raw.Length) {
        $id = [Text.Encoding]::ASCII.GetString($raw, $p, 4)
        $sz = [BitConverter]::ToInt32($raw, $p + 4)
        if ($id -eq 'data') { $off = $p + 8; break }
        $p += 8 + $sz + ($sz -band 1)
    }
    if ($off -lt 0) { throw "no data chunk in $path" }
    $n = [int]($sz / 2)
    $s = [int16[]]::new($n)
    [Buffer]::BlockCopy($raw, $off, $s, 0, $n * 2)
    ,$s
}
function Read-BinFloats([string]$path) {
    $raw = [IO.File]::ReadAllBytes($path)
    $rank = [BitConverter]::ToInt32($raw, 4)
    $n = 1; for ($i = 0; $i -lt $rank; $i++) { $n *= [BitConverter]::ToInt64($raw, 8 + 8*$i) }
    $off = 8 + 8*$rank
    $f = [float[]]::new($n)
    [Buffer]::BlockCopy($raw, $off, $f, 0, $n * 4)
    ,$f
}
function Get-Rms([int16[]]$s) {
    $acc = [double]0; foreach ($v in $s) { $x = $v / 32768.0; $acc += $x * $x }
    [Math]::Sqrt($acc / [Math]::Max(1, $s.Length))
}
function Get-RmseWavWav([int16[]]$a, [int16[]]$b) {
    $n = [Math]::Min($a.Length, $b.Length); $acc = [double]0
    for ($i = 0; $i -lt $n; $i++) { $d = ($a[$i] - $b[$i]) / 32768.0; $acc += $d * $d }
    [Math]::Sqrt($acc / [Math]::Max(1, $n))
}
function Get-RmseWavFloats([int16[]]$a, [float[]]$b) {
    $n = [Math]::Min($a.Length, $b.Length); $acc = [double]0
    for ($i = 0; $i -lt $n; $i++) { $d = $a[$i] / 32768.0 - $b[$i]; $acc += $d * $d }
    [Math]::Sqrt($acc / [Math]::Max(1, $n))
}
function Read-Receipt([string]$r) {
    $h = @{}
    foreach ($m in [regex]::Matches($r, '(\w+)=([^\s]+)')) { $h[$m.Groups[1].Value] = $m.Groups[2].Value }
    $h
}

$RMSE_FLOOR = 3.0e-2   # bench.rb.graphruntime-kokoro-parity regression floor (known baseline 2.516e-2 vs ORT)

# ---- A: Windows lane (in-proc) — re-prove + produce the reference wav ----
Write-Host "A: Windows lane — DpxKokoro.Project in-proc"
$winWav = Join-Path $tmp 'kokoro-win.wav'
$winReceipt = $K.GetMethod('Project').Invoke($null, [object[]]@([string]$model, [string]$io, [string]$winWav))
Write-Host "    $winReceipt"
$wr = Read-Receipt $winReceipt
Assert ($wr.nodes -match '^(\d+)/\1$') "A windows: all nodes ran ($($wr.nodes))"
Assert ([int]$wr.naninf -eq 0) "A windows: waveform finite (naninf=$($wr.naninf))"
$winS = Read-WavSamples $winWav
$oraclePath = Join-Path $io 'oracle.bin'
if (Test-Path $oraclePath) {
    $oracle = Read-BinFloats $oraclePath
    $winOracleRmse = Get-RmseWavFloats $winS $oracle
    Write-Host ("    windows vs ORT oracle: rmse={0:E3} (floor {1:E3})" -f $winOracleRmse, $RMSE_FLOOR)
    Assert ($winOracleRmse -le $RMSE_FLOOR) ("A windows parity floor: rmse {0:E3} <= {1:E3}" -f $winOracleRmse, $RMSE_FLOOR)
} else { Write-Host "    (oracle.bin absent — windows parity floor not re-proved this run)" -ForegroundColor Yellow }

# ---- B: device lane — SKIP cleanly (named) when the seam is absent ----
function Skip([string]$why) {
    Write-Host "SKIP - $why" -ForegroundColor Yellow
    $pass = $fails.Count -eq 0
    Write-Host ($(if($pass){"PASS — Windows lane green; device lane skipped: $why"}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
    [pscustomobject]@{ test='test.dpx.kokoro-android'; pass=$pass; windows=$winReceipt; verdict="device SKIP ($why)" }
}
if (-not (Test-Path $adb)) { return Skip "adb not found at $adb" }
$devs = & $adb devices | Select-String "^$serial\s+device"
if (-not $devs) { return Skip "S23 $serial not attached" }
$pmPath = & $adb -s $serial shell pm path $pkg 2>$null
if (-not ($pmPath -match 'package:')) { return Skip "$pkg not installed on $serial" }

Write-Host "B: device lane — S23 $serial"

# Push the model + reference inputs into the app's PRIVATE files/kokoro/ (debuggable dev build:
# adb push to /data/local/tmp, then run-as cp — the documented model-push seam). Size-matched files
# are not re-pushed (the 325 MB model rides USB once).
$stage = '/data/local/tmp/ss-kokoro'
& $adb -s $serial shell "run-as $pkg mkdir -p files/kokoro" | Out-Null
function Push-IntoApp([string]$local, [string]$leaf) {
    $want = (Get-Item $local).Length
    $have = (& $adb -s $serial shell "run-as $pkg stat -c %s files/kokoro/$leaf" 2>$null | Out-String).Trim()
    if ($have -eq "$want") { Write-Host "    $leaf already on device ($want B)"; return }
    & $adb -s $serial shell "mkdir -p $stage" | Out-Null
    & $adb -s $serial push $local "$stage/$leaf" | Out-Null
    & $adb -s $serial shell "run-as $pkg cp $stage/$leaf files/kokoro/$leaf" | Out-Null
    & $adb -s $serial shell "rm -f $stage/$leaf" | Out-Null
    $have = (& $adb -s $serial shell "run-as $pkg stat -c %s files/kokoro/$leaf" 2>$null | Out-String).Trim()
    if ($have -ne "$want") { throw "push failed for ${leaf}: device has '$have', want $want" }
    Write-Host "    pushed $leaf ($want B)"
}
Push-IntoApp $model 'model.onnx'
foreach ($n in $inputNames) { Push-IntoApp (Join-Path $io "$n.bin") "$n.bin" }

# Wake + start the app, tunnel loopback, read the per-boot cap token from the private dir.
& $adb -s $serial shell input keyevent KEYCODE_WAKEUP | Out-Null
& $adb -s $serial shell "am start -n $pkg/$pkg.MainActivity" | Out-Null
$port = 18080
& $adb -s $serial forward "tcp:$port" tcp:8080 | Out-Null
$base = "http://127.0.0.1:$port"
$up = $false
for ($i = 0; $i -lt 30 -and -not $up; $i++) {
    try { Invoke-RestMethod -Uri "$base/apps" -TimeoutSec 3 | Out-Null; $up = $true } catch { Start-Sleep -Seconds 2 }
}
if (-not $up) { return Skip "app HTTP backend (:8080) did not come up on $serial" }
$cap = (& $adb -s $serial shell "run-as $pkg cat files/.cap" | Out-String).Trim()
if ($cap.Length -lt 8) { return Skip "cap token unreadable (files/.cap)" }
Assert $true "B device: app up, loopback tunneled, cap token in hand"

# Run the SAME lane on-device over /api/exec (the app's FullLanguage API pool calls the shared type).
$deviceCmd = '$f=[Subsystem.MainActivity]::Instance.FilesDir.AbsolutePath; [Subsystem.Dpx.DpxKokoro]::Project("$f/kokoro/model.onnx", "$f/kokoro", "$f/kokoro/kokoro-android.wav")'
Write-Host "    synthesizing on-device (2463-node graph on NEON — minutes, not seconds)…"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$resp = Invoke-RestMethod -Uri "$base/api/exec" -Method Post -Headers @{ 'X-Subsystem-Cap' = $cap } -Body $deviceCmd -ContentType 'text/plain' -TimeoutSec 1800
$sw.Stop()
if ($resp -isnot [string]) {
    $err = if ($resp.error) { $resp.error } else { ($resp | ConvertTo-Json -Compress -Depth 3) }
    Assert $false "B device: /api/exec synthesis failed: $err"
    return Skip "device synthesis errored (receipt above)"
}
Write-Host "    DEVICE RECEIPT: $resp"
Write-Host ("    wall (incl HTTP): {0:F1}s" -f $sw.Elapsed.TotalSeconds)
$dr = Read-Receipt $resp
Assert ($dr.nodes -match '^(\d+)/\1$') "B device: all nodes ran on-device ($($dr.nodes))"
Assert ([int]$dr.naninf -eq 0) "B device: waveform finite (naninf=$($dr.naninf))"

# Pull the on-device wav (binary-safe: exec-out + raw stream copy) and diff vs the Windows wav.
$devWav = Join-Path $tmp 'kokoro-android.wav'
$psi = [System.Diagnostics.ProcessStartInfo]::new($adb)
$psi.Arguments = "-s $serial exec-out run-as $pkg cat files/kokoro/kokoro-android.wav"
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
$fs = [IO.File]::Create($devWav)
$p.StandardOutput.BaseStream.CopyTo($fs)
$fs.Close(); $p.WaitForExit()
Assert ((Get-Item $devWav).Length -gt 44) "B device: wav pulled ($((Get-Item $devWav).Length) B)"

$devS = Read-WavSamples $devWav
$devRms = Get-Rms $devS
$devDur = $devS.Length / 24000.0
Write-Host ("    on-device wav: samples={0} sr=24000 dur={1:F2}s rms={2:F5}" -f $devS.Length, $devDur, $devRms)
Assert ($devS.Length -eq $winS.Length) "B parity: sample count matches Windows ($($devS.Length) vs $($winS.Length))"
Assert ($devRms -ge 0.01) ("B receipt: non-silence (rms {0:F5} >= 0.01)" -f $devRms)
$xRmse = Get-RmseWavWav $devS $winS
Write-Host ("    android vs windows: rmse={0:E3}" -f $xRmse)
Assert ($xRmse -le $RMSE_FLOOR) ("B parity: android-vs-windows rmse {0:E3} <= {1:E3}" -f $xRmse, $RMSE_FLOOR)

& $adb -s $serial forward --remove "tcp:$port" 2>$null | Out-Null

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — Kokoro-82M synthesized IN-PROC on the Android head; on-device wav matches the Windows lane (rmse $('{0:E3}' -f $xRmse))."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test = 'test.dpx.kokoro-android'; pass = $pass
    windows = $winReceipt; device = $resp
    deviceSamples = $devS.Length; deviceDurS = [Math]::Round($devDur, 2); deviceRms = [Math]::Round($devRms, 5)
    androidVsWindowsRmse = $xRmse
    verdict = $(if($pass){'kokoro runs in-proc on the S23 (CPU/NEON), wav receipt pulled and matched'}else{'see failures'})
}
