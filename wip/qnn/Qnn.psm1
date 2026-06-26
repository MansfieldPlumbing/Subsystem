# Qnn.psm1 — object-oriented QNN/Hexagon pipeline for the Subsystem way.
# No python: build/tile via onnx-surgeon (protobuf), orchestrate here in PowerShell,
# everything returns OBJECTS (pipe them, don't scrape). Dogfood through ss.exe:
#   ss -Command "ipmo S:\qnn\Qnn.psm1; Invoke-QnnFinalize -Dlc <dlc> -Graph <g>"
# The closed Qualcomm tools (qairt-converter=python, *-generator.exe) are wrapped here
# as a thin seam until we emit DLC/.bin natively (the sovereign maker).

$script:QairtRoot = (Get-ChildItem 'S:\qairt' -Directory -EA SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1).FullName
$script:QairtBin  = Join-Path $script:QairtRoot 'bin\x86_64-windows-msvc'
$script:QairtLib  = Join-Path $script:QairtRoot 'lib\x86_64-windows-msvc'
$script:EnvDone   = $false

function Initialize-QnnEnv {
    [CmdletBinding()] param()
    if ($script:EnvDone) { return }
    & (Join-Path $script:QairtRoot 'bin\envsetup.ps1') | Out-Null
    $script:EnvDone = $true
}

function New-QnnHtpConfig {
    # Writes the HTP V73 ext+wrapper config (via ConvertTo-Json, backslash-safe). Returns the wrapper path.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Graph,
        [Parameter(Mandatory)][string]$OutDir,
        [int]$VtcmMb = 8, [int]$O = 2, [int]$SocId = 43, [string]$DspArch = 'v73',
        [bool]$Fp16 = $true, [switch]$EnableSlc
    )
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $g = [ordered]@{ graph_names = @($Graph); fp16_relaxed_precision = [int]$Fp16; vtcm_mb = $VtcmMb; O = $O }
    if ($EnableSlc) { $g['O_config'] = @{ enable_slc_allocator = 1 } }  # advisory; HTP reads O_config map
    $ext = Join-Path $OutDir "ext_$Graph.json"
    $cfg = Join-Path $OutDir "cfg_$Graph.json"
    @{ graphs = @($g); devices = @(@{ soc_id = $SocId; dsp_arch = $DspArch;
        cores = @(@{ core_id = 0; perf_profile = 'burst'; rpc_control_latency = 100 }) }) } |
        ConvertTo-Json -Depth 9 | Set-Content -Encoding ascii $ext
    @{ backend_extensions = @{ shared_library_path = 'QnnHtpNetRunExtensions.dll'; config_file_path = $ext } } |
        ConvertTo-Json -Depth 5 | Set-Content -Encoding ascii $cfg
    $cfg
}

function Invoke-QnnFinalize {
    # Finalize a DLC into a V73 context binary; return a structured RESULT OBJECT (never scraped text).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Dlc,
        [Parameter(Mandatory)][string]$Graph,
        [int]$VtcmMb = 8, [int]$O = 2, [switch]$EnableSlc,
        [int]$SocId = 43, [string]$DspArch = 'v73',
        [string]$OutDir = (Split-Path $Dlc -Parent),
        [string]$Tag = $Graph
    )
    Initialize-QnnEnv
    $binDir = Join-Path $OutDir 'bin'
    New-Item -ItemType Directory -Force $binDir | Out-Null
    $cfg = New-QnnHtpConfig -Graph $Graph -OutDir $OutDir -VtcmMb $VtcmMb -O $O -EnableSlc:$EnableSlc -SocId $SocId -DspArch $DspArch
    $logPath = Join-Path $OutDir "finalize_$Tag.log"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $log = & (Join-Path $script:QairtBin 'qnn-context-binary-generator.exe') `
        --backend QnnHtp.dll --model QnnModelDlc.dll --dlc_path $Dlc `
        --config_file $cfg --binary_file "${Tag}_v73" --output_dir $binDir 2>&1
    $sw.Stop()
    $log | Set-Content $logPath
    $binPath = Join-Path $binDir "${Tag}_v73.bin"
    $finalized = Test-Path $binPath

    # parse the busting op (object out, not a string blob)
    $text = $log -join "`n"
    $bustOp = $bustId = $preOp = $stage = $null; $tcm = $vtcmBytes = 0L
    if ($text -match 'A single op, "([^"]+)" \(Op ID: ([0-9a-fA-F]+)\), requires (0x[0-9a-fA-F]+) bytes of TCM, which is greater than the TCM size of (0x[0-9a-fA-F]+)') {
        $bustOp = $Matches[1]; $bustId = $Matches[2]
        $tcm = [Convert]::ToInt64($Matches[3].Substring(2), 16)
        $vtcmBytes = [Convert]::ToInt64($Matches[4].Substring(2), 16)
    }
    if ($text -match 'The name of the failing op before optimization is: "([^"]+)"') { $preOp = $Matches[1] }
    $stageHit = [regex]::Matches($text, 'Starting stage: ([^\r\n]+)')
    if ($stageHit.Count) { $stage = $stageHit[$stageHit.Count - 1].Groups[1].Value.Trim() }

    [pscustomobject]@{
        Graph         = $Graph
        Finalized     = $finalized
        BinPath       = $(if ($finalized) { $binPath })
        BinMB         = $(if ($finalized) { [math]::Round((Get-Item $binPath).Length / 1MB, 2) })
        BustOp        = $bustOp
        BustOpId      = $bustId
        PreOptOp      = $preOp
        TcmRequiredMB = $(if ($tcm) { [math]::Round($tcm / 1MB, 2) })
        TcmRequiredB  = $tcm
        VtcmMB        = $VtcmMb
        FailedStage   = $(if (-not $finalized) { $stage })
        Seconds       = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        LogPath       = $logPath
    }
}

# ============================================================================
#  ONNX AS .NET OBJECTS  — ONNX is protobuf; edit ModelProto as live objects in
#  PowerShell (Onnx.dll generated from onnx.proto). No python, ever.
# ============================================================================
$script:OnnxLibDir = 'S:\qnn\onnxnet\bin\Release\netstandard2.0'
$script:OnnxLoaded = $false

function Initialize-OnnxAssemblies {
    [CmdletBinding()] param()
    if ($script:OnnxLoaded) { return }
    [void][Reflection.Assembly]::LoadFrom((Join-Path $script:OnnxLibDir 'Google.Protobuf.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $script:OnnxLibDir 'Onnx.dll'))
    $script:OnnxLoaded = $true
}

function Import-Onnx {
    # ONNX file -> live [Onnx.ModelProto] object
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    Initialize-OnnxAssemblies
    [Onnx.ModelProto]::Parser.ParseFrom([IO.File]::ReadAllBytes($Path))
}

function Export-Onnx {
    # live ModelProto -> ONNX file (returns the FileInfo object)
    [CmdletBinding()] param([Parameter(Mandatory)]$Model, [Parameter(Mandatory)][string]$Path)
    [IO.File]::WriteAllBytes($Path, [Google.Protobuf.MessageExtensions]::ToByteArray($Model))
    Get-Item $Path
}

function New-OnnxNode {
    # build a NodeProto with int / ints attributes
    [CmdletBinding()] param(
        [Parameter(Mandatory)][string]$OpType, [string]$Name,
        [string[]]$In = @(), [string[]]$Out = @(),
        [hashtable]$IntAttr, [hashtable]$IntsAttr)
    Initialize-OnnxAssemblies
    $n = [Onnx.NodeProto]::new(); $n.OpType = $OpType; if ($Name) { $n.Name = $Name }
    foreach ($i in $In)  { [void]$n.Input.Add($i) }
    foreach ($o in $Out) { [void]$n.Output.Add($o) }
    if ($IntAttr)  { foreach ($k in $IntAttr.Keys) {
        $a = [Onnx.AttributeProto]::new(); $a.Name = $k
        $a.Type = [Onnx.AttributeProto+Types+AttributeType]::Int; $a.I = [long]$IntAttr[$k]
        [void]$n.Attribute.Add($a) } }
    if ($IntsAttr) { foreach ($k in $IntsAttr.Keys) {
        $a = [Onnx.AttributeProto]::new(); $a.Name = $k
        $a.Type = [Onnx.AttributeProto+Types+AttributeType]::Ints
        foreach ($v in $IntsAttr[$k]) { [void]$a.Ints.Add([long]$v) }
        [void]$n.Attribute.Add($a) } }
    $n
}

function New-OnnxInt64Init {
    # build a 1-D int64 initializer (Split sizes, Slice starts/ends, axes, ...)
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][long[]]$Values)
    Initialize-OnnxAssemblies
    $t = [Onnx.TensorProto]::new(); $t.Name = $Name
    $t.DataType = [int][Onnx.TensorProto+Types+DataType]::Int64
    [void]$t.Dims.Add([long]$Values.Count)
    foreach ($v in $Values) { [void]$t.Int64Data.Add([long]$v) }
    $t
}

function Get-OnnxAttrInts {
    param($Node, [string]$Name)
    foreach ($a in $Node.Attribute) { if ($a.Name -eq $Name) { return @($a.Ints) } }
    $null
}

function Edit-OnnxConvToTiled {
    # In-place: replace a single stride-1 Conv with an EXACT blocked equivalent
    #   Pad(X) -> [Slice -> valid Conv] x K -> Concat -> Y
    # so each tile's im2col fits VTCM. Output tensor name Y is preserved (consumers untouched).
    # Bit-exact (blocked convolution). Returns the count of nodes added.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Model,
        [Parameter(Mandatory)][string]$ConvName,
        [Parameter(Mandatory)][int]$Length,   # size of the time axis being tiled
        [int]$Tiles = 7,
        [int]$Axis = 2)
    Initialize-OnnxAssemblies
    $g = $Model.Graph
    $idx = -1
    for ($i = 0; $i -lt $g.Node.Count; $i++) { if ($g.Node[$i].Name -eq $ConvName) { $idx = $i; break } }
    if ($idx -lt 0) { throw "conv not found: $ConvName" }
    $n = $g.Node[$idx]
    if ($n.OpType -ne 'Conv') { throw "$ConvName is $($n.OpType), not Conv" }
    $sArr = Get-OnnxAttrInts $n 'strides'; $s = if ($sArr) { [int]$sArr[0] } else { 1 }
    if ($s -ne 1) { throw "stride=$s unsupported here (strided conv needs the born-at-input tiler)" }

    $X = $n.Input[0]; $W = $n.Input[1]; $B = if ($n.Input.Count -ge 3) { $n.Input[2] } else { $null }
    $Y = $n.Output[0]
    $k  = [int](Get-OnnxAttrInts $n 'kernel_shape')[0]
    $dA = Get-OnnxAttrInts $n 'dilations'; $d = if ($dA) { [int]$dA[0] } else { 1 }
    $pA = Get-OnnxAttrInts $n 'pads'; $pl = [int]$pA[0]; $pr = [int]$pA[1]
    $gA = Get-OnnxAttrInts $n 'group'; $grp = if ($gA) { [int]$gA[0] } else { 1 }
    $halo = ($k - 1) * $d
    $p = "$ConvName/t"                       # tensor/node name prefix

    # even split with remainder on the last tile
    $T = [math]::Floor($Length / $Tiles)
    $sizes = for ($j = 0; $j -lt $Tiles; $j++) { if ($j -eq $Tiles - 1) { $Length - $T * ($Tiles - 1) } else { $T } }

    # initializers: the global pad, and per-tile slice starts/ends (axes shared)
    $padVec = New-Object 'long[]' 6
    $padVec[$Axis] = $pl; $padVec[$Axis + 3] = $pr           # [b0,b1,b2, e0,e1,e2]
    $g.Initializer.Add((New-OnnxInt64Init -Name "$p/pads" -Values $padVec))
    $g.Initializer.Add((New-OnnxInt64Init -Name "$p/axes" -Values @([long]$Axis)))

    $newNodes = New-Object System.Collections.Generic.List[object]
    $newNodes.Add((New-OnnxNode -OpType 'Pad' -Name "$p/Pad" -In @($X, "$p/pads") -Out @("$p/Xp")))
    $off = 0; $yNames = @()
    for ($j = 0; $j -lt $Tiles; $j++) {
        $st = $off; $en = $off + $sizes[$j] + $halo          # window covers tile + halo
        $g.Initializer.Add((New-OnnxInt64Init -Name "$p/st$j" -Values @([long]$st)))
        $g.Initializer.Add((New-OnnxInt64Init -Name "$p/en$j" -Values @([long]$en)))
        $newNodes.Add((New-OnnxNode -OpType 'Slice' -Name "$p/Slice$j" `
                    -In @("$p/Xp", "$p/st$j", "$p/en$j", "$p/axes") -Out @("$p/win$j")))
        $convIn = if ($B) { @("$p/win$j", $W, $B) } else { @("$p/win$j", $W) }
        $newNodes.Add((New-OnnxNode -OpType 'Conv' -Name "$p/Conv$j" -In $convIn -Out @("$p/y$j") `
                    -IntsAttr @{ kernel_shape = @($k); dilations = @($d); pads = @(0, 0); strides = @(1) } `
                    -IntAttr @{ group = $grp }))
        $yNames += "$p/y$j"; $off += $sizes[$j]
    }
    $newNodes.Add((New-OnnxNode -OpType 'Concat' -Name "$p/Concat" -In $yNames -Out @($Y) -IntAttr @{ axis = $Axis }))

    $g.Node.RemoveAt($idx)
    for ($o = 0; $o -lt $newNodes.Count; $o++) { $g.Node.Insert($idx + $o, $newNodes[$o]) }
    $newNodes.Count
}

function Set-OnnxConvToChannelGroups {
    # "Atomize each side of the fence": replace a Conv [Cin->Cout] with G partial convs over
    # input-channel GROUPS (Cin/G each) summed by Adds — EXACT (sum over channels = the conv).
    # Each partial's im2col is 1/G the size; a channel-group+Add shape HTP may NOT re-fuse into
    # one conv (unlike a spatial tile, which it does). Returns nodes added.
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Model, [Parameter(Mandatory)][string]$ConvName, [int]$Groups = 6)
    Initialize-OnnxAssemblies
    $g = $Model.Graph
    $idx = -1; for ($i = 0; $i -lt $g.Node.Count; $i++) { if ($g.Node[$i].Name -eq $ConvName) { $idx = $i; break } }
    if ($idx -lt 0) { throw "conv not found: $ConvName" }
    $n = $g.Node[$idx]
    $X = $n.Input[0]; $W = $n.Input[1]; $B = if ($n.Input.Count -ge 3) { $n.Input[2] } else { $null }; $Y = $n.Output[0]
    $k = [int](Get-OnnxAttrInts $n 'kernel_shape')[0]
    $dA = Get-OnnxAttrInts $n 'dilations'; $d = if ($dA) { [int]$dA[0] } else { 1 }
    $pA = Get-OnnxAttrInts $n 'pads'; $pl = [int]$pA[0]; $pr = [int]$pA[1]
    $sA = Get-OnnxAttrInts $n 'strides'; $s = if ($sA) { [int]$sA[0] } else { 1 }
    $wt = $null; foreach ($t in $g.Initializer) { if ($t.Name -eq $W) { $wt = $t; break } }
    if (-not $wt) { throw "weight initializer not found: $W" }
    $inCh = [int]$wt.Dims[1]
    if ($inCh % $Groups -ne 0) { throw "in-channels $inCh not divisible by Groups $Groups" }
    $gs = $inCh / $Groups
    $p = "$ConvName/cg"
    $g.Initializer.Add((New-OnnxInt64Init -Name "$p/split" -Values (@($gs) * $Groups | ForEach-Object { [long]$_ })))
    $g.Initializer.Add((New-OnnxInt64Init -Name "$p/wax" -Values @([long]1)))
    $newNodes = New-Object System.Collections.Generic.List[object]
    $splitOuts = for ($j = 0; $j -lt $Groups; $j++) { "$p/x$j" }
    $newNodes.Add((New-OnnxNode -OpType 'Split' -Name "$p/Split" -In @($X, "$p/split") -Out $splitOuts -IntAttr @{ axis = 1 }))
    $partials = @()
    for ($j = 0; $j -lt $Groups; $j++) {
        $g.Initializer.Add((New-OnnxInt64Init -Name "$p/ws$j" -Values @([long]($j * $gs))))
        $g.Initializer.Add((New-OnnxInt64Init -Name "$p/we$j" -Values @([long](($j + 1) * $gs))))
        $newNodes.Add((New-OnnxNode -OpType 'Slice' -Name "$p/Wslice$j" -In @($W, "$p/ws$j", "$p/we$j", "$p/wax") -Out @("$p/w$j")))
        $convIn = if ($j -eq 0 -and $B) { @("$p/x$j", "$p/w$j", $B) } else { @("$p/x$j", "$p/w$j") }
        $newNodes.Add((New-OnnxNode -OpType 'Conv' -Name "$p/Conv$j" -In $convIn -Out @("$p/y$j") `
                    -IntsAttr @{ kernel_shape = @($k); dilations = @($d); pads = @($pl, $pr); strides = @($s) } -IntAttr @{ group = 1 }))
        $partials += "$p/y$j"
    }
    $acc = $partials[0]
    for ($j = 1; $j -lt $Groups; $j++) {
        $out = if ($j -eq $Groups - 1) { $Y } else { "$p/acc$j" }
        $newNodes.Add((New-OnnxNode -OpType 'Add' -Name "$p/Add$j" -In @($acc, $partials[$j]) -Out @($out)))
        $acc = $out
    }
    $g.Node.RemoveAt($idx)
    for ($o = 0; $o -lt $newNodes.Count; $o++) { $g.Node.Insert($idx + $o, $newNodes[$o]) }
    $newNodes.Count
}

Export-ModuleMember -Function Initialize-QnnEnv, New-QnnHtpConfig, Invoke-QnnFinalize,
    Initialize-OnnxAssemblies, Import-Onnx, Export-Onnx, New-OnnxNode, New-OnnxInt64Init,
    Get-OnnxAttrInts, Edit-OnnxConvToTiled, Set-OnnxConvToChannelGroups
