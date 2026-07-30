#requires -Version 7
# test.dpx.q4-packing-order.ps1 - Pins the q4 nibble byte-packing order the block-q4 kernels actually decode
# (CRQ190 first receipt: the dequant formula (q - zp)*scale is shared with ggml q4_0, but the same formula over a
# transposed nibble layout is silent garbage - so the layout itself needs a fixture, not an assumption).
# Drives the REAL kernels in-proc (Dpx.MatMulNBits / Dpx.GatherBlockQuantized via reflection) with A = I, so the
# output reads back the kernel's dequant of every packed element. Two decodes of the SAME bytes are diffed:
#   sequential (ORT MatMulNBits: byte k>>1, low nibble = even k)  - the kernel must match this EXACTLY
#   ggml q4_0 split (byte j of a 16-byte block = elements j and j+16) - must DIFFER (the port may take ggml's
#                                                                       dot recipe, never its unpack order)
# Also pinned: the zero-point nibble order (same Nib4 path), zp default = 8 when absent, and scale indexing
# sc[n*nBlk + b]. K=64 (2 blocks) so per-block indexing is exercised, N=2 rows, all values exact in fp32.
# NOTE: geometry vars are $Kd/$Nd, never $K/$N - PowerShell variables are case-insensitive and a `foreach ($k ...)`
# loop would silently clobber $K (found the hard way; the first run of this fixture failed exactly that way).
#   Dogfood:  ss test dpx.q4-packing-order
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dpType = 'Subsystem.Dpx.Dp' -as [type]
if (-not $dpType) { $dpType = [AppDomain]::CurrentDomain.GetAssemblies().ForEach({ $_.GetType('Subsystem.Dpx.Dp') }).Where({ $_ }) | Select-Object -First 1 }
if (-not $dpType) { Write-Host "SKIP - Subsystem.Dpx not loaded (run in-proc: ss test dpx.q4-packing-order)." -ForegroundColor Yellow; return }
$asm      = $dpType.Assembly
$tensorT  = $asm.GetType('Subsystem.Dpx.Tensor')
$arenaT   = $asm.GetType('Subsystem.Dpx.TensorArena')
$nodeT    = $asm.GetType('Onnx.NodeProto')
$attrT    = $asm.GetType('Onnx.AttributeProto')
$bf       = [System.Reflection.BindingFlags]'NonPublic,Static'
$mmnb     = $dpType.GetMethod('MatMulNBits', $bf)
$gbq      = $dpType.GetMethod('GatherBlockQuantized', $bf)
Assert ($null -ne $mmnb) 'Dpx.MatMulNBits reachable in-proc'
Assert ($null -ne $gbq)  'Dpx.GatherBlockQuantized reachable in-proc'

# fixture geometry: K=64 (nBlk=2, bs=32), N=2 output rows. 32 packed bytes per row, known and asymmetric.
$Kd = 64; $Nd = 2; $bs = 32; $nBlk = 2
$row0 = [byte[]]((0x10,0x32,0x54,0x76,0x98,0xBA,0xDC,0xFE) * 4)                    # sequential decode: 0..15 repeating
$row1 = [byte[]]((0xF0,0xE1,0xD2,0xC3,0xB4,0xA5,0x96,0x87,0x78,0x69,0x5A,0x4B,0x3C,0x2D,0x1E,0x0F) * 2)
$packed = [byte[]]($row0 + $row1)
$scales = [float[]](1.0, 0.5, 2.0, 1.0)                                            # sc[n*nBlk+b]: pins per-block indexing

# the two candidate decodes of the same bytes
function DecodeSeq([byte[]]$b,[int]$k)  { ($b[$k -shr 1] -shr (($k -band 1) * 4)) -band 0xF }
function DecodeGgml([byte[]]$b,[int]$k) { $blk = [int][math]::Floor($k / 32); $i = $k % 32
    if ($i -lt 16) { $b[$blk*16 + $i] -band 0xF } else { ($b[$blk*16 + ($i-16)] -shr 4) -band 0xF } }

$rows = @($row0, $row1)
$orderDiff = 0; $total = $Kd * $Nd
foreach ($nn in 0..($Nd-1)) { foreach ($k in 0..($Kd-1)) { if ((DecodeSeq $rows[$nn] $k) -ne (DecodeGgml $rows[$nn] $k)) { $orderDiff++ } } }
Write-Host "  layout disagreement on these bytes: $orderDiff/$total elements decode differently (sequential vs ggml-split)"
Assert ($orderDiff -gt ($total / 2)) 'the two packing orders are materially different on the fixture bytes (a transposed port could not pass by luck)'

function NewTensor { [Activator]::CreateInstance($tensorT) }
function NewNode([hashtable]$attrs) {
    $n = [Activator]::CreateInstance($nodeT); $n.OpType = 'MatMulNBits'
    foreach ($kv in $attrs.GetEnumerator()) { $a = [Activator]::CreateInstance($attrT); $a.Name = $kv.Key; $a.I = [long]$kv.Value; $n.Attribute.Add($a) }
    return $n
}

$wasActive = $arenaT.GetProperty('Active').GetValue($null)
$arenaT.GetProperty('Active').SetValue($null, $false)   # output tensors land in .Fp (readable managed arrays)
try {
    # A = I(64), so Y[m,n] = dequant(B[n, m]) element-for-element - the kernel's own decode, read back verbatim.
    $ident = New-Object 'float[]' ($Kd * $Kd); for ($i = 0; $i -lt $Kd; $i++) { $ident[$i * $Kd + $i] = 1.0 }
    $tA = NewTensor; $tA.Fp = $ident;  $tA.Shape = [int[]]($Kd, $Kd)
    $tB = NewTensor; $tB.Rawb = $packed; $tB.Shape = [int[]]($Nd, $nBlk, 16)
    $tS = NewTensor; $tS.Fp = $scales; $tS.Shape = [int[]]($Nd, $nBlk)
    $node = NewNode @{ K = $Kd; N = $Nd; bits = 4; block_size = $bs }

    # --- case 1: zp absent -> default 8 ---
    $x3 = [Array]::CreateInstance($tensorT, 3); $x3[0] = $tA; $x3[1] = $tB; $x3[2] = $tS
    $y = $mmnb.Invoke($null, @($x3, $node))
    $seqMax = 0.0; $ggmlSame = $true
    foreach ($nn in 0..($Nd-1)) { foreach ($k in 0..($Kd-1)) {
        $got  = $y.Fp[$k * $Nd + $nn]
        $s    = $scales[$nn * $nBlk + [int][math]::Floor($k / $bs)]
        $seq  = ([float](DecodeSeq  $rows[$nn] $k) - 8.0) * $s
        $ggml = ([float](DecodeGgml $rows[$nn] $k) - 8.0) * $s
        $d = [math]::Abs($got - $seq); if ($d -gt $seqMax) { $seqMax = $d }
        if ($got -ne $ggml) { $ggmlSame = $false }
    } }
    Assert ($seqMax -eq 0.0) "MatMulNBits (zp absent) decodes SEQUENTIAL nibbles exactly (max |diff| = $seqMax, zp default 8, per-block scales)"
    Assert (-not $ggmlSame)  'MatMulNBits does NOT decode ggml-q4_0 split order (a literal ggml unpack transcription would be garbage here)'

    # --- case 2: explicit packed zp -> pins the zp nibble order through the same Nib4 path ---
    $zps = @(@(5, 9), @(8, 8))                                    # per (row, block); row0 byte = 0x95
    $tZ = NewTensor; $tZ.Rawb = [byte[]](0x95, 0x88); $tZ.Shape = [int[]]($Nd, 1)
    $x4 = [Array]::CreateInstance($tensorT, 4); $x4[0] = $tA; $x4[1] = $tB; $x4[2] = $tS; $x4[3] = $tZ
    $y2 = $mmnb.Invoke($null, @($x4, $node))
    $zpMax = 0.0
    foreach ($nn in 0..($Nd-1)) { foreach ($k in 0..($Kd-1)) {
        $b = [int][math]::Floor($k / $bs)
        $exp = ([float](DecodeSeq $rows[$nn] $k) - [float]$zps[$nn][$b]) * $scales[$nn * $nBlk + $b]
        $d = [math]::Abs($y2.Fp[$k * $Nd + $nn] - $exp); if ($d -gt $zpMax) { $zpMax = $d }
    } }
    Assert ($zpMax -eq 0.0) "MatMulNBits (zp packed 0x95/0x88) reads zp nibbles low-first per row (max |diff| = $zpMax)"

    # --- GatherBlockQuantized: the other block-q4 consumer decodes the SAME order ---
    $tD = NewTensor; $tD.Rawb = $packed; $tD.Shape = [int[]]($Nd, 32)   # [V=2, 32 bytes] -> H=64
    $tI = NewTensor; $tI.Ip = [long[]](0, 1); $tI.Shape = [int[]](, 2)
    $gS = NewTensor; $gS.Fp = $scales; $gS.Shape = [int[]]($Nd, $nBlk)
    $gNode = NewNode @{ bits = 4; block_size = $bs; gather_axis = 0; quantize_axis = 1 }
    $x3g = [Array]::CreateInstance($tensorT, 3); $x3g[0] = $tD; $x3g[1] = $tI; $x3g[2] = $gS
    $g = $gbq.Invoke($null, @($x3g, $gNode))
    $gMax = 0.0
    foreach ($t in 0..1) { foreach ($k in 0..($Kd-1)) {
        $exp = ([float](DecodeSeq $rows[$t] $k) - 8.0) * $scales[$t * $nBlk + [int][math]::Floor($k / $bs)]
        $d = [math]::Abs($g.Fp[$t * $Kd + $k] - $exp); if ($d -gt $gMax) { $gMax = $d }
    } }
    Assert ($gMax -eq 0.0) "GatherBlockQuantized decodes the same sequential order (max |diff| = $gMax)"
}
finally { $arenaT.GetProperty('Active').SetValue($null, $wasActive) }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - q4 layout pinned: SEQUENTIAL nibbles (byte k>>1, low = even k), zp nibbles low-first, zp default 8, scales sc[n*nBlk+b]. ggml q4_0's split order decodes $orderDiff/$total elements differently on the same bytes - port the dot recipe, keep OUR unpack."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='dpx.q4-packing-order'; pass=$pass; layout='sequential-low-even'; ggmlOrderDiffers="$orderDiff/$total"; verdict=$(if($pass){'packing order proven by fixture, not assumed - CRQ190 proof gap closed'}else{'see failures'}) }
