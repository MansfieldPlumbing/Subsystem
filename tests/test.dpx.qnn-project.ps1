#requires -Version 7
# test.dpx.qnn-project.ps1 — First caller of DpxQnn.Project: carries a REAL gemma4-e2b MatMulNBits
# tile (model_per_layer_projection, K=1536) from the decoder-q4 model.db through the QNN C API —
# dequantized with the pinned sequential-nibble layout (test.dpx.q4-packing-order.ps1), projected to
# a context binary, executed on the QnnCpu backend, verified against Project's in-method oracle.
# Closes the CRQ158/183 gap "DpxQnn.Project has no caller/harness". The HTP backend on this x64 host
# can only offline-prepare (no NPU to execute) — reported informationally, never asserted.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss -File tests/test.dpx.qnn-project.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

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

Write-Host "Compiling QnnTileHarness via Roslyn (winsqlite3 reader + q4 dequant + DpxQnn.Project)..."
$harnessCode = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using Subsystem.Dpx;

// Reads one real MatMulNBits weight (quant + scales + zp) straight out of the model.db via
// winsqlite3 (ss.exe's trimmed host cannot open Microsoft.Data.Sqlite — the dossier's workaround),
// dequantizes with the SEQUENTIAL-nibble layout pinned by test.dpx.q4-packing-order.ps1
// (byte = k>>1 within the 32-block, LOW nibble = even k; zp packed 2-per-byte low-first, w = s*(q-zp)),
// transposes [N,K] -> [K,N] for Project's x@W contract, and runs the QNN projection.
public static class QnnTileHarness
{
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CharSet = CharSet.Ansi)]
    static extern int Open(string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CharSet = CharSet.Ansi)]
    static extern int Prepare(IntPtr db, string sql, int len, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", CharSet = CharSet.Ansi)]
    static extern int BindText(IntPtr stmt, int idx, string val, int len, IntPtr destructor);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    static extern int Step(IntPtr stmt);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob")]
    static extern IntPtr ColumnBlob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")]
    static extern int ColumnBytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    static extern int Finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2")]
    static extern int Close(IntPtr db);

    static byte[] ReadTensor(IntPtr db, string name)
    {
        IntPtr stmt;
        if (Prepare(db, "SELECT data FROM tensor WHERE name=?", -1, out stmt, IntPtr.Zero) != 0)
            throw new Exception("prepare failed");
        try
        {
            BindText(stmt, 1, name, -1, (IntPtr)(-1));   // SQLITE_TRANSIENT
            if (Step(stmt) != 100) throw new Exception("tensor row not found: " + name);
            int len = ColumnBytes(stmt, 0);
            var bytes = new byte[len];
            Marshal.Copy(ColumnBlob(stmt, 0), bytes, 0, len);
            return bytes;
        }
        finally { Finalize(stmt); }
    }

    public static string Run(string dbPath, string qnnCpuPath, string qnnHtpPath, string binOutCpu, string binOutHtp)
    {
        const string tile = "model_per_layer_projection_MatMul";   // K=1536, N=8960, bits=4, bs=32
        const int K = 1536, Nfull = 8960, Nsub = 512, blocks = K / 32;   // sub-tile: first 512 output rows

        IntPtr db;
        if (Open(dbPath, out db, 1 /*SQLITE_OPEN_READONLY*/, IntPtr.Zero) != 0)
            return "error: sqlite open failed for " + dbPath;
        byte[] quant, zp; float[] scales;
        try
        {
            quant = ReadTensor(db, tile + "_weight_quant");    // uint8 [8960, 48, 16]
            var scaleBytes = ReadTensor(db, tile + "_weight_scales");  // f32 [8960, 48]
            zp = ReadTensor(db, tile + "_weight_zp");          // uint8 [8960, 24] (48 nibbles, low-first)
            scales = new float[scaleBytes.Length / 4];
            Buffer.BlockCopy(scaleBytes, 0, scales, 0, scaleBytes.Length);
        }
        finally { Close(db); }

        if (quant.Length != Nfull * blocks * 16) return "error: quant size " + quant.Length + " != expected " + (Nfull * blocks * 16);
        if (scales.Length != Nfull * blocks) return "error: scales count " + scales.Length;
        if (zp.Length != Nfull * (blocks / 2)) return "error: zp size " + zp.Length;

        // Dequant [Nsub, K] then transpose into Project's [K, N] row-major (y = x @ W).
        var W = new float[K * Nsub];
        double sumAbs = 0; int nonZero = 0;
        for (int n = 0; n < Nsub; n++)
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
                    int k0 = b * 32 + 2 * j, k1 = k0 + 1;
                    float w0 = s * ((q2 & 0xF) - z);
                    float w1 = s * ((q2 >> 4) - z);
                    W[k0 * Nsub + n] = w0;
                    W[k1 * Nsub + n] = w1;
                    sumAbs += Math.Abs(w0) + Math.Abs(w1);
                    if (w0 != 0f) nonZero++; if (w1 != 0f) nonZero++;
                }
            }
        }
        double meanAbs = sumAbs / (K * Nsub);
        bool sane = true;
        for (int i = 0; i < W.Length; i++) if (float.IsNaN(W[i]) || float.IsInfinity(W[i])) { sane = false; break; }

        // Deterministic activation, gemma-hidden-scaled.
        var x = new float[K];
        for (int i = 0; i < K; i++) x[i] = (float)Math.Sin(i * 0.0037) * 0.25f;

        var qnn = new DpxQnn();
        string cpuLine = qnn.Project(qnnCpuPath, binOutCpu, (uint)K, (uint)Nsub, W, x);

        string htpLine = "skipped (QnnHtp.dll absent)";
        if (!string.IsNullOrEmpty(qnnHtpPath))
        {
            // x64 host has no NPU: finalize/prepare may succeed (offline .bin) but execute cannot.
            // Informational only — whatever Project returns is the honest record.
            try { htpLine = qnn.Project(qnnHtpPath, binOutHtp, (uint)K, (uint)Nsub, W, x); }
            catch (Exception ex) { htpLine = "threw " + ex.GetType().Name + ": " + ex.Message; }
        }

        var sb = new StringBuilder();
        sb.AppendLine("tile:" + tile);
        sb.AppendLine("k:" + K);
        sb.AppendLine("n_sub:" + Nsub + "/" + Nfull);
        sb.AppendLine("dequant_mean_abs:" + meanAbs.ToString("G6"));
        sb.AppendLine("dequant_nonzero_frac:" + ((double)nonZero / (K * Nsub)).ToString("F4"));
        sb.AppendLine("dequant_sane:" + sane);
        sb.AppendLine("cpu_line:" + cpuLine);
        sb.AppendLine("htp_line:" + htpLine);
        return sb.ToString();
    }
}
"@

$syntaxTrees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
# Compile DpxQnn.cs from WORKTREE SOURCE (decode-loop pattern): the live ss.exe bundle may predate
# ad5263f (real-weight overload); the source of truth for this receipt is the tree, not the old exe.
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

$tmp = [System.IO.Path]::GetTempPath()
$binCpu = Join-Path $tmp 'gemma_ple_tile_cpu.bin'
$binHtp = Join-Path $tmp 'gemma_ple_tile_htp.bin'
$htpPath = if ($qnnHtp) { $qnnHtp.FullName } else { '' }

$resultStr = $harness.GetMethod('Run').Invoke($null, [object[]]@([string]$decoderDb.FullName, [string]$qnnCpu.FullName, [string]$htpPath, [string]$binCpu, [string]$binHtp))
$r = @{}
foreach ($line in ($resultStr -split "`n")) { if ($line -match '^([^:]+):(.*)$') { $r[$Matches[1]] = $Matches[2].Trim() } }

if ($r.ContainsKey('error')) { throw "harness error: $($r['error'])" }
Write-Host "  tile $($r['tile'])  K=$($r['k'])  N=$($r['n_sub'])  meanAbs=$($r['dequant_mean_abs'])  nonzero=$($r['dequant_nonzero_frac'])"
Write-Host "  cpu: $($r['cpu_line'])"
Write-Host "  htp: $($r['htp_line'])"

$cpuPass = $r['cpu_line'] -match 'PASS'
$cpuBin  = if ($r['cpu_line'] -match 'bin=(\d+)B') { [long]$Matches[1] } else { 0 }

Assert ([bool]::Parse($r['dequant_sane'])) "dequantized tile has no NaN/Inf"
Assert ([double]$r['dequant_nonzero_frac'] -gt 0.5) "dequantized tile is real data, not zeros (nonzero frac $($r['dequant_nonzero_frac']))"
Assert $cpuPass "DpxQnn.Project executed the real gemma tile on QnnCpu and matched its oracle ($($r['cpu_line']))"
Assert ($cpuBin -gt 0) "context binary emitted from real model.db weights ($cpuBin B)"
if ($cpuBin -gt 0 -and (Test-Path $binCpu)) {
    $md5 = (Get-FileHash $binCpu -Algorithm MD5).Hash.ToLower()
    Write-Host "  context binary: $binCpu ($cpuBin B, md5 $md5)"
}

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — DpxQnn.Project carried a real gemma4-e2b q4 tile from model.db through the QNN seam."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})

[pscustomobject]@{
    test = 'test.dpx.qnn-project'
    pass = $pass
    tile = $r['tile']
    cpu = $r['cpu_line']
    htp = $r['htp_line']
    verdict = $(if($pass){"model.db -> dequant (sequential-nibble) -> QNN graph -> context binary -> execute -> oracle match; the carrier-by-mode seam is exercised with her real weights"}else{'see failures'})
}
