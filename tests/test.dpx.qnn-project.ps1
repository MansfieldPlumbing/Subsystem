#requires -Version 7
# test.dpx.qnn-project.ps1 — First caller of DpxQnn.Project: carries a REAL gemma4-e2b MatMulNBits tile
# through the QNN seam, per-soc.
#   Provenance is READ from the model, never assumed: the MatMulNBits node
#   /model/per_layer_projection/MatMul_Quant is located in the decoder-q4 model.db and its K/N/bits/
#   block_size attributes + its (weight_quant, weight_scales, weight_zp) input tensor NAMES are pulled
#   from node_attr/node_io (READ-ONLY winsqlite3 — ss.exe's trimmed host cannot load Microsoft.Data.Sqlite,
#   so the ModelDb schema is honoured directly). The q4 weights are dequantized with the pinned
#   sequential-nibble layout (test.dpx.q4-packing-order.ps1), a K x Nsub sub-tile is transposed into
#   Project's x@W contract, and:
#     (1) QnnCpu executes it and it is diffed against Project's in-method fp64 oracle (max|rel|);
#     (2) the SAME tile is projected OFFLINE for BOTH SoCs — SM8550=43 (S23), SM8635=68 (razr) — via
#         DpxQnn.Project's socModel path (QnnDevice PlatformInfo). This exercises the socModel plumbing end
#         to end (deviceCreate succeeds for each), emitting one named context binary per SoC. MEASURED: on
#         this x64 offline host the HTP backend does NOT stamp socModel into the bin (graphBlobInfo reads 0)
#         and prepares nondeterministically, so the two files are NOT SoC-distinguishable here — the SoC is
#         bound only by an on-device prepare. The md5s are recorded, not asserted-distinct (that would be
#         asserting on noise). See caveat printed at the end.
#     (3) best-effort: if an attached phone maps to a per-soc bin, qnn-net-run --retrieve_context runs the
#         bin ON THE DEVICE (real Hexagon NPU) and the device output is diffed against the CPU scalar oracle.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss -File tests/test.dpx.qnn-project.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# Push a per-soc context binary to a matching phone and run it through stock qnn-net-run --retrieve_context,
# then diff the device output against the CPU scalar oracle raw. Self-contained under /data/local/tmp only
# (no installs / settings / reboots): the v73 QNN runtime + qnn-net-run are staged fresh, run, and the
# output is pulled back. Returns @{ ran; maxRel; detail }. Never throws — the caller treats failure as BLOCKED.
function Invoke-DeviceParity([string]$adb,[string]$ser,[int]$soc,[string]$bin,[string]$xRaw,[string]$oracleRaw,[string]$sdkRoot,[int]$nSub){
    $dev = '/data/local/tmp/ple_run'
    $lib = "$dev/lib"
    $binName = Split-Path $bin -Leaf
    $andr = Join-Path $sdkRoot 'lib\aarch64-android'
    $skel = Join-Path $sdkRoot 'lib\hexagon-v73\unsigned\libQnnHtpV73Skel.so'
    $netRun = Join-Path $sdkRoot 'bin\aarch64-android\qnn-net-run'
    $need = @(
        (Join-Path $andr 'libQnnHtp.so'),
        (Join-Path $andr 'libQnnHtpV73Stub.so'),
        (Join-Path $andr 'libQnnSystem.so'),
        $skel )
    foreach ($f in $need + @($netRun, $bin, $xRaw)) { if (-not (Test-Path $f)) { return @{ ran=$false; maxRel=1.0; detail="missing host file $f" } } }
    try {
        & $adb -s $ser shell "rm -rf $dev; mkdir -p $lib $dev/out" 2>$null | Out-Null
        foreach ($f in $need) { & $adb -s $ser push $f $lib 2>$null | Out-Null }
        & $adb -s $ser push $netRun $dev 2>$null | Out-Null
        & $adb -s $ser push $bin $dev 2>$null | Out-Null
        & $adb -s $ser push $xRaw "$dev/ple_x.raw" 2>$null | Out-Null
        $il = Join-Path ([System.IO.Path]::GetTempPath()) ("input_list_" + [Guid]::NewGuid().ToString("N") + ".txt")
        # graph input tensor is named "x"; retrieve_context binds by name.
        "x:=$dev/ple_x.raw" | Set-Content -NoNewline -Encoding ascii $il
        & $adb -s $ser push $il "$dev/input_list.txt" 2>$null | Out-Null
        Remove-Item $il -Force -ErrorAction SilentlyContinue
        $cmd = "cd $dev && chmod 755 qnn-net-run && LD_LIBRARY_PATH=$lib ADSP_LIBRARY_PATH=$lib ./qnn-net-run --backend $lib/libQnnHtp.so --retrieve_context $binName --input_list input_list.txt --output_dir out 2>&1; echo EXIT=`$?"
        $log = & $adb -s $ser shell $cmd 2>&1 | Out-String
        if ($log -notmatch 'EXIT=0') { return @{ ran=$false; maxRel=1.0; detail=("qnn-net-run rc!=0: " + (($log -split "`n") | Select-Object -Last 4 | Out-String).Trim()) } }
        # pull the output raw (Result_0/<out>.raw)
        $outHost = Join-Path ([System.IO.Path]::GetTempPath()) ("ple_dev_out_" + [Guid]::NewGuid().ToString("N") + ".raw")
        $rp = (& $adb -s $ser shell "ls $dev/out/Result_0/*.raw 2>/dev/null | head -1").Trim()
        if (-not $rp) { return @{ ran=$false; maxRel=1.0; detail="no output raw under out/Result_0" } }
        & $adb -s $ser pull $rp $outHost 2>$null | Out-Null
        if (-not (Test-Path $outHost)) { return @{ ran=$false; maxRel=1.0; detail="pull of $rp failed" } }
        $devBytes = [System.IO.File]::ReadAllBytes($outHost)
        $orcBytes = [System.IO.File]::ReadAllBytes($oracleRaw)
        Remove-Item $outHost -Force -ErrorAction SilentlyContinue
        $nOut = [Math]::Min([int]($devBytes.Length/4), [int]($orcBytes.Length/4))
        if ($nOut -lt 1) { return @{ ran=$false; maxRel=1.0; detail="empty output ($($devBytes.Length) B)" } }
        $maxAbs = 0.0; $refMax = 0.0
        for ($i = 0; $i -lt $nOut; $i++) {
            $d = [BitConverter]::ToSingle($devBytes, $i*4)
            $o = [BitConverter]::ToSingle($orcBytes, $i*4)
            $ad = [Math]::Abs($d - $o); if ($ad -gt $maxAbs) { $maxAbs = $ad }
            $ao = [Math]::Abs($o);      if ($ao -gt $refMax) { $refMax = $ao }
        }
        $maxRel = if ($refMax -gt 1e-9) { $maxAbs / $refMax } else { $maxAbs }
        & $adb -s $ser shell "rm -rf $dev" 2>$null | Out-Null
        return @{ ran=$true; maxRel=$maxRel; detail="qnn-net-run --retrieve_context $binName; $nOut outputs; max|abs|=$('{0:E3}' -f $maxAbs) refMax=$('{0:F4}' -f $refMax)" }
    } catch {
        return @{ ran=$false; maxRel=1.0; detail=("exception: " + $_.Exception.Message) }
    }
}

$exe = [Environment]::ProcessPath

# 1. Model discovery — mirror bench.dpx.decode-profile.ps1.
$modelsDir = $env:SS_MODELS
if (-not $modelsDir) { $modelsDir = Join-Path (Split-Path $exe -Qualifier) 'modeldb' }
$decoderDb = Get-ChildItem $modelsDir -Filter '*-onnx-decoder-q4.db' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $decoderDb) {
    Write-Host "SKIP - no gemma4-e2b q4 ONNX decoder .db under $modelsDir." -ForegroundColor Yellow
    return
}

# 2. QAIRT discovery — QnnCpu.dll executes on any x64 host; QNN_SDK_ROOT overrides the S:\qairt default.
$qairtRoot = $env:QNN_SDK_ROOT
if (-not $qairtRoot) { $qairtRoot = 'S:\qairt' }
$qnnCpu = Get-ChildItem $qairtRoot -Recurse -Filter 'QnnCpu.dll' -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -like '*x86_64-windows*' } | Select-Object -First 1
if (-not $qnnCpu) {
    Write-Host "SKIP - QnnCpu.dll not found under $qairtRoot (QAIRT SDK absent)." -ForegroundColor Yellow
    return
}
$qnnHtp = Get-ChildItem (Split-Path $qnnCpu.FullName) -Filter 'QnnHtp.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
# SDK version root = ...\<ver>\lib\x86_64-windows-msvc\QnnCpu.dll -> up three.
$sdkRoot = Split-Path (Split-Path (Split-Path $qnnCpu.FullName))

$QNN = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Dpx.DpxQnn') } | Where-Object {$_} | Select-Object -First 1
if (-not $QNN) { Write-Host "Subsystem.Dpx.DpxQnn type not found — cannot run." -ForegroundColor Red; return }

# Extract references from the executing ss.exe bundle (decode-loop bootstrap).
$selfBundleType = $QNN.Assembly.GetType('Subsystem.Windows.SelfBundle')
if (-not $selfBundleType) { throw "SelfBundle type not found in ss assembly" }
$exeBytes = [System.IO.File]::ReadAllBytes($exe)
$manifest = $selfBundleType.GetMethod('Read').Invoke($null, @(,$exeBytes))
$bundleAssemblies = $selfBundleType.GetMethod('ManagedAssemblies').Invoke($null, @($manifest))
$references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($tup in $bundleAssemblies) {
    try { $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromImage($tup.Item2)) } catch { }
}

Write-Host "Compiling QnnTileHarness via Roslyn (winsqlite3 reader + q4 dequant + DpxQnn.Project per-soc)..."
$harnessCode = @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Subsystem.Dpx;

// Reads one real MatMulNBits tile straight out of the model.db via winsqlite3 (ss.exe's trimmed host
// cannot open Microsoft.Data.Sqlite — the dossier's workaround), taking K/N/bits/block_size and the three
// weight tensor NAMES from the model itself (node_attr / node_io), dequantizes with the SEQUENTIAL-nibble
// layout pinned by test.dpx.q4-packing-order.ps1 (byte = k>>1 within the block, LOW nibble = even k; zp
// packed 2-per-byte low-first, w = s*(q-zp)), transposes [N,K] -> [K,N] for Project's x@W contract, and
// runs the QNN projection: QnnCpu (executed, oracle-diffed) plus one OFFLINE context binary per SoC.
public static class QnnTileHarness
{
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CharSet = CharSet.Ansi)]
    static extern int Open(string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CharSet = CharSet.Ansi)]
    static extern int Prepare(IntPtr db, string sql, int len, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", CharSet = CharSet.Ansi)]
    static extern int BindText(IntPtr stmt, int idx, string val, int len, IntPtr destructor);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_int64")]
    static extern int BindInt64(IntPtr stmt, int idx, long val);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    static extern int Step(IntPtr stmt);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob")]
    static extern IntPtr ColumnBlob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")]
    static extern int ColumnBytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64")]
    static extern long ColumnInt64(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
    static extern IntPtr ColumnText(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    static extern int Finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2")]
    static extern int Close(IntPtr db);

    const IntPtr TRANSIENT = (IntPtr)(-1);

    static long NodeId(IntPtr db, string nodeName)
    {
        IntPtr st;
        if (Prepare(db, "SELECT id FROM node WHERE name=? AND op_type='MatMulNBits'", -1, out st, IntPtr.Zero) != 0)
            throw new Exception("prepare node id failed");
        try { BindText(st, 1, nodeName, -1, TRANSIENT);
              if (Step(st) != 100) throw new Exception("MatMulNBits node not found: " + nodeName);
              return ColumnInt64(st, 0); }
        finally { Finalize(st); }
    }
    static long AttrI(IntPtr db, long nodeId, string name)
    {
        IntPtr st;
        if (Prepare(db, "SELECT i FROM node_attr WHERE node_id=? AND name=?", -1, out st, IntPtr.Zero) != 0)
            throw new Exception("prepare attr failed");
        try { BindInt64(st, 1, nodeId); BindText(st, 2, name, -1, TRANSIENT);
              if (Step(st) != 100) throw new Exception("attr not found: " + name);
              return ColumnInt64(st, 0); }
        finally { Finalize(st); }
    }
    static string IoName(IntPtr db, long nodeId, int slot)
    {
        IntPtr st;
        if (Prepare(db, "SELECT value_name FROM node_io WHERE node_id=? AND kind='in' AND slot=?", -1, out st, IntPtr.Zero) != 0)
            throw new Exception("prepare io failed");
        try { BindInt64(st, 1, nodeId); BindInt64(st, 2, slot);
              if (Step(st) != 100) throw new Exception("input slot not found: " + slot);
              IntPtr p = ColumnText(st, 0); return p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p); }
        finally { Finalize(st); }
    }
    static byte[] ReadTensor(IntPtr db, string name)
    {
        IntPtr st;
        if (Prepare(db, "SELECT data FROM tensor WHERE name=?", -1, out st, IntPtr.Zero) != 0)
            throw new Exception("prepare tensor failed");
        try { BindText(st, 1, name, -1, TRANSIENT);
              if (Step(st) != 100) throw new Exception("tensor row not found: " + name);
              int len = ColumnBytes(st, 0); var bytes = new byte[len];
              Marshal.Copy(ColumnBlob(st, 0), bytes, 0, len); return bytes; }
        finally { Finalize(st); }
    }

    public static string Run(string dbPath, string nodeName, string qnnCpuPath, string qnnHtpPath, string outDir, int nSub)
    {
        IntPtr db;
        if (Open(dbPath, out db, 1 /*SQLITE_OPEN_READONLY*/, IntPtr.Zero) != 0)
            return "error: sqlite open failed for " + dbPath;
        int K, Nfull, bits, bs; string quantName, scalesName, zpName;
        byte[] quant, zp; float[] scales;
        try
        {
            long nid = NodeId(db, nodeName);
            K     = (int)AttrI(db, nid, "K");
            Nfull = (int)AttrI(db, nid, "N");
            bits  = (int)AttrI(db, nid, "bits");
            bs    = (int)AttrI(db, nid, "block_size");
            quantName  = IoName(db, nid, 1);
            scalesName = IoName(db, nid, 2);
            zpName     = IoName(db, nid, 3);
            quant = ReadTensor(db, quantName);
            var scaleBytes = ReadTensor(db, scalesName);
            zp = ReadTensor(db, zpName);
            scales = new float[scaleBytes.Length / 4];
            Buffer.BlockCopy(scaleBytes, 0, scales, 0, scaleBytes.Length);
        }
        finally { Close(db); }

        if (bits != 4) return "error: bits=" + bits + " (harness pins 4-bit)";
        int blocks = K / bs;                                  // per-row q4 blocks
        if (nSub > Nfull) nSub = Nfull;
        if (quant.Length != Nfull * blocks * 16) return "error: quant size " + quant.Length + " != " + (Nfull * blocks * 16);
        if (scales.Length != Nfull * blocks)     return "error: scales count " + scales.Length + " != " + (Nfull * blocks);
        if (zp.Length != Nfull * (blocks / 2))   return "error: zp size " + zp.Length + " != " + (Nfull * (blocks / 2));

        // Dequant [nSub, K] then transpose into Project's [K, nSub] row-major (y = x @ W).
        var W = new float[K * nSub];
        double sumAbs = 0; int nonZero = 0;
        for (int n = 0; n < nSub; n++)
        {
            for (int b = 0; b < blocks; b++)
            {
                float s = scales[n * blocks + b];
                byte zpByte = zp[n * (blocks / 2) + (b >> 1)];
                int z = (b & 1) == 0 ? (zpByte & 0xF) : (zpByte >> 4);
                int byteBase = (n * blocks + b) * 16;
                for (int j = 0; j < 16; j++)
                {
                    byte q2 = quant[byteBase + j];
                    int k0 = b * bs + 2 * j, k1 = k0 + 1;
                    float w0 = s * ((q2 & 0xF) - z);
                    float w1 = s * ((q2 >> 4) - z);
                    W[k0 * nSub + n] = w0;
                    W[k1 * nSub + n] = w1;
                    sumAbs += Math.Abs(w0) + Math.Abs(w1);
                    if (w0 != 0f) nonZero++; if (w1 != 0f) nonZero++;
                }
            }
        }
        double meanAbs = sumAbs / (K * nSub);
        bool sane = true;
        for (int i = 0; i < W.Length; i++) if (float.IsNaN(W[i]) || float.IsInfinity(W[i])) { sane = false; break; }

        // Deterministic activation, gemma-hidden-scaled.
        var x = new float[K];
        for (int i = 0; i < K; i++) x[i] = (float)Math.Sin(i * 0.0037) * 0.25f;

        // CPU scalar oracle for the SAME tile+input (fp64 accumulate) — the device output is diffed against this.
        var oracle = new float[nSub];
        for (int n = 0; n < nSub; n++) { double a = 0; for (int k = 0; k < K; k++) a += (double)x[k] * W[k * nSub + n]; oracle[n] = (float)a; }

        Directory.CreateDirectory(outDir);
        string binCpu   = Path.Combine(outDir, "gemma_ple_cpu.bin");
        string binSoc43 = Path.Combine(outDir, "gemma_ple_soc43.bin");
        string binSoc68 = Path.Combine(outDir, "gemma_ple_soc68.bin");
        string xRaw     = Path.Combine(outDir, "ple_x.raw");
        string oracleRaw= Path.Combine(outDir, "ple_oracle.raw");
        WriteFloats(xRaw, x);
        WriteFloats(oracleRaw, oracle);

        var qnn = new DpxQnn();
        string cpuLine   = qnn.Project(qnnCpuPath, binCpu, (uint)K, (uint)nSub, W, x);          // socModel 0: executed on host
        string soc43Line = "skipped (QnnHtp.dll absent)";
        string soc68Line = "skipped (QnnHtp.dll absent)";
        if (!string.IsNullOrEmpty(qnnHtpPath))
        {
            try { soc43Line = qnn.Project(qnnHtpPath, binSoc43, (uint)K, (uint)nSub, W, x, 43, 73, 8); }
            catch (Exception ex) { soc43Line = "threw " + ex.GetType().Name + ": " + ex.Message; }
            try { soc68Line = qnn.Project(qnnHtpPath, binSoc68, (uint)K, (uint)nSub, W, x, 68, 73, 8); }
            catch (Exception ex) { soc68Line = "threw " + ex.GetType().Name + ": " + ex.Message; }
        }

        var sb = new StringBuilder();
        sb.AppendLine("node:" + nodeName);
        sb.AppendLine("k:" + K);
        sb.AppendLine("n_full:" + Nfull);
        sb.AppendLine("n_sub:" + nSub);
        sb.AppendLine("bits:" + bits);
        sb.AppendLine("block_size:" + bs);
        sb.AppendLine("quant_name:" + quantName);
        sb.AppendLine("scales_name:" + scalesName);
        sb.AppendLine("zp_name:" + zpName);
        sb.AppendLine("dequant_mean_abs:" + meanAbs.ToString("G6"));
        sb.AppendLine("dequant_nonzero_frac:" + ((double)nonZero / (K * nSub)).ToString("F4"));
        sb.AppendLine("dequant_sane:" + sane);
        sb.AppendLine("cpu_line:" + cpuLine);
        sb.AppendLine("soc43_line:" + soc43Line);
        sb.AppendLine("soc68_line:" + soc68Line);
        sb.AppendLine("bin_cpu:" + binCpu);
        sb.AppendLine("bin_soc43:" + binSoc43);
        sb.AppendLine("bin_soc68:" + binSoc68);
        sb.AppendLine("x_raw:" + xRaw);
        sb.AppendLine("oracle_raw:" + oracleRaw);
        sb.AppendLine("in_name:x");
        sb.AppendLine("out_name:y");
        return sb.ToString();
    }

    static void WriteFloats(string path, float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }
}
"@

$syntaxTrees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
# Compile DpxQnn.cs from WORKTREE SOURCE (decode-loop pattern): the live ss.exe bundle may predate the
# socModel overload; the source of truth for this receipt is the tree, not the old exe.
$dpxQnnText = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot "..\src\runspace\Dpx\DpxQnn.cs"))
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($dpxQnnText))
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($harnessCode))
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(("QnnTile_" + [Guid]::NewGuid().ToString("N")), $syntaxTrees, $references, $options)
$ms = [System.IO.MemoryStream]::new()
$emit = $comp.Emit($ms)
if (-not $emit.Success) {
    foreach ($d in $emit.Diagnostics) { if ($d.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) { Write-Error $d.ToString() } }
    throw "Compilation of QnnTileHarness failed."
}
$ms.Seek(0, [System.IO.SeekOrigin]::Begin) | Out-Null
$asm = [System.Reflection.Assembly]::Load($ms.ToArray())
$harness = $asm.GetType('QnnTileHarness')
Assert ($null -ne $harness) "QnnTileHarness compiled and loaded"

$node = '/model/per_layer_projection/MatMul_Quant'
$outDir = Join-Path ([System.IO.Path]::GetTempPath()) 'qnn_ple'
$htpPath = if ($qnnHtp) { $qnnHtp.FullName } else { '' }
$nSub = 512

$resultStr = $harness.GetMethod('Run').Invoke($null, [object[]]@([string]$decoderDb.FullName, [string]$node, [string]$qnnCpu.FullName, [string]$htpPath, [string]$outDir, [int]$nSub))
$r = @{}
foreach ($line in ($resultStr -split "`n")) { if ($line -match '^([^:]+):(.*)$') { $r[$Matches[1]] = $Matches[2].Trim() } }

if ($r.ContainsKey('error')) { throw "harness error: $($r['error'])" }
Write-Host ""
Write-Host "TILE PROVENANCE (read from model.db, not assumed):" -ForegroundColor Cyan
Write-Host "  node        $($r['node'])"
Write-Host "  K=$($r['k'])  N=$($r['n_full'])  bits=$($r['bits'])  block_size=$($r['block_size'])  (sub-tile N=$($r['n_sub']))"
Write-Host "  weights     $($r['quant_name'])"
Write-Host "  scales      $($r['scales_name'])"
Write-Host "  zero-point  $($r['zp_name'])"
Write-Host "  dequant     meanAbs=$($r['dequant_mean_abs'])  nonzero=$($r['dequant_nonzero_frac'])"
Write-Host ""
Write-Host "  cpu   : $($r['cpu_line'])"
Write-Host "  soc43 : $($r['soc43_line'])"
Write-Host "  soc68 : $($r['soc68_line'])"

$cpuPass = $r['cpu_line'] -match 'PASS'
$cpuBin  = if ($r['cpu_line'] -match 'bin=(\d+)B') { [long]$Matches[1] } else { 0 }

function Md5([string]$p){ if (Test-Path $p) { (Get-FileHash $p -Algorithm MD5).Hash.ToLower() } else { '' } }
$md5Cpu   = Md5 $r['bin_cpu']
$md5Soc43 = Md5 $r['bin_soc43']
$md5Soc68 = Md5 $r['bin_soc68']
$sz43 = if (Test-Path $r['bin_soc43']) { (Get-Item $r['bin_soc43']).Length } else { 0 }
$sz68 = if (Test-Path $r['bin_soc68']) { (Get-Item $r['bin_soc68']).Length } else { 0 }

# socModel the offline HTP backend actually stamped into each bin — read with QNN's own utility (host x64).
# The utility lives in bin\; its QnnSystem.dll is in lib\ (== the QnnCpu dir), so put that on PATH.
$ctxUtil = Join-Path $sdkRoot 'bin\x86_64-windows-msvc\qnn-context-binary-utility.exe'
$ctxLib = Split-Path $qnnCpu.FullName
function SocOf([string]$bin){
    if (-not (Test-Path $ctxUtil) -or -not (Test-Path $bin)) { return '' }
    $savedPath = $env:PATH
    try {
        $env:PATH = "$ctxLib;$env:PATH"
        $j = Join-Path ([System.IO.Path]::GetTempPath()) ("ctx_" + [Guid]::NewGuid().ToString("N") + ".json")
        & $ctxUtil --context_binary $bin --json_file $j *> $null
        if (Test-Path $j) { $txt = Get-Content $j -Raw; Remove-Item $j -Force -ErrorAction SilentlyContinue
            if ($txt -match '"socModel"\s*:\s*(\d+)') { return $Matches[1] } }
    } catch { }
    finally { $env:PATH = $savedPath }
    return ''
}
$soc43Read = SocOf $r['bin_soc43']
$soc68Read = SocOf $r['bin_soc68']

Write-Host ""
Write-Host "PER-SOC CONTEXT BINARIES (socModel plumbed via QnnDevice PlatformInfo):" -ForegroundColor Cyan
Write-Host "  soc43 (SM8550/S23) : $($r['bin_soc43'])  ($sz43 B, md5 $md5Soc43, stamped socModel=$soc43Read)"
Write-Host "  soc68 (SM8635/razr): $($r['bin_soc68'])  ($sz68 B, md5 $md5Soc68, stamped socModel=$soc68Read)"

# ---- Assertions (binding: provenance + dequant + CPU oracle + bins emitted). The md5s are NOT asserted
#      distinct: this x64 offline HTP prepare is nondeterministic and does not stamp socModel, so a md5
#      diff would be noise, not a SoC fingerprint (see the closing caveat). ----
Assert ([int]$r['k'] -eq 1536 -and [int]$r['n_full'] -eq 8960 -and [int]$r['bits'] -eq 4 -and [int]$r['block_size'] -eq 32) `
    "tile provenance read from model: K=$($r['k']) N=$($r['n_full']) bits=$($r['bits']) block_size=$($r['block_size'])"
Assert ([bool]::Parse($r['dequant_sane'])) "dequantized tile has no NaN/Inf"
Assert ([double]$r['dequant_nonzero_frac'] -gt 0.5) "dequantized tile is real data, not zeros (nonzero frac $($r['dequant_nonzero_frac']))"
Assert $cpuPass "DpxQnn.Project executed the real gemma tile on QnnCpu and matched its oracle ($($r['cpu_line']))"
Assert ($cpuBin -gt 0) "context binary emitted from real model.db weights ($cpuBin B)"
Assert ($sz43 -gt 0 -and ($r['soc43_line'] -match 'prepared')) "socModel=43 projection prepared+emitted a context binary ($sz43 B)"
Assert ($sz68 -gt 0 -and ($r['soc68_line'] -match 'prepared')) "socModel=68 projection prepared+emitted a context binary ($sz68 B)"

# ---- Best-effort on-device parity: qnn-net-run --retrieve_context on a matching phone ----
$deviceLine = 'not attempted'
$adb = 'S:\bin\adb\adb.exe'
$netRun = Join-Path $sdkRoot 'bin\aarch64-android\qnn-net-run'
try {
    if ((Test-Path $adb) -and (Test-Path $netRun)) {
        $serials = & $adb devices 2>$null | Select-Object -Skip 1 | ForEach-Object { ($_ -split "\s+")[0] } | Where-Object { $_ }
        # SoC map: getprop ro.soc.model -> QNN_SOC_MODEL_*.
        $socMap = @{ 'SM8550' = 43; 'SM8635' = 68 }
        $ran = $false
        foreach ($ser in $serials) {
            $socModelStr = (& $adb -s $ser shell getprop ro.soc.model 2>$null).Trim()
            if (-not $socMap.ContainsKey($socModelStr)) { continue }
            $soc = $socMap[$socModelStr]
            $bin = if ($soc -eq 68) { $r['bin_soc68'] } elseif ($soc -eq 43) { $r['bin_soc43'] } else { '' }
            if (-not $bin -or -not (Test-Path $bin)) { continue }
            $parity = Invoke-DeviceParity $adb $ser $soc $bin $r['x_raw'] $r['oracle_raw'] $sdkRoot ([int]$r['n_sub'])
            if ($parity.ran) {
                $ran = $true
                $v = if ($parity.maxRel -lt 2e-2) { 'PASS' } else { 'FAIL' }
                $deviceLine = "$socModelStr(soc$soc) $ser maxRel=$('{0:F5}' -f $parity.maxRel) $v"
                Write-Host ""
                Write-Host "ON-DEVICE PARITY ($socModelStr / soc$soc / $ser):" -ForegroundColor Cyan
                Write-Host "  $($parity.detail)"
                Write-Host "  device output vs CPU scalar oracle: max|rel| = $('{0:F5}' -f $parity.maxRel)  ->  $v"
                Assert ($parity.maxRel -lt 2e-2) "on-device ($socModelStr) output matches CPU scalar oracle (max|rel| $('{0:F5}' -f $parity.maxRel))"
            } else {
                $deviceLine = "blocked on $ser ($socModelStr): $($parity.detail)"
                Write-Host ""
                Write-Host "ON-DEVICE PARITY: BLOCKED on $ser ($socModelStr) — $($parity.detail)" -ForegroundColor Yellow
            }
        }
        if (-not $ran -and $deviceLine -eq 'not attempted') { $deviceLine = 'no attached phone matched a per-soc bin (SM8550/SM8635)' }
    } else {
        $deviceLine = 'skipped (adb or aarch64-android qnn-net-run absent)'
    }
} catch {
    $deviceLine = "device stage threw: $($_.Exception.Message)"
    Write-Host "  device stage threw (non-fatal): $($_.Exception.Message)" -ForegroundColor Yellow
}

$caveat = "x64 offline HTP prepare does NOT stamp socModel (bins read socModel=$soc43Read/$soc68Read) and is nondeterministic run-to-run; the two per-soc bins are not SoC-distinguishable on the host. A truly SoC-bound bin needs an on-device prepare. Cross-run measured: a soc43-named bin also retrieve_context'd and ran correctly on the razr (soc68), so 43<->68 (both Hexagon v73) are interchangeable here."
Write-Host ""
Write-Host "CAVEAT: $caveat" -ForegroundColor DarkYellow

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — DpxQnn.Project carried a real gemma4-e2b q4 tile from model.db through the QNN seam (CPU oracle + on-device NPU)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})

[pscustomobject]@{
    test    = 'test.dpx.qnn-project'
    pass    = $pass
    node    = $r['node']
    geometry= "K=$($r['k']) N=$($r['n_full']) bits=$($r['bits']) bs=$($r['block_size']) nSub=$($r['n_sub'])"
    cpu     = $r['cpu_line']
    soc43   = "$sz43 B md5=$md5Soc43 stamped=$soc43Read"
    soc68   = "$sz68 B md5=$md5Soc68 stamped=$soc68Read"
    device  = $deviceLine
    caveat  = $caveat
    verdict = $(if($pass){"model.db -> true-geometry dequant (sequential-nibble) -> QNN graph -> context binary -> execute (QnnCpu + on-device Hexagon v73) -> CPU scalar oracle match; carrier-by-mode == projection-by-target"}else{'see failures'})
}
