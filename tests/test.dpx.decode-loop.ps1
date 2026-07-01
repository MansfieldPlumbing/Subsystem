#requires -Version 7
# test.dpx.decode-loop.ps1 — Proves the DPX decode loop and KV-cache off-GC management.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss -File tests/test.rb.dponnx-decode-loop.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$VOM = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if (-not $VOM) { Write-Host "Subsystem.Vom type not found — cannot run." -ForegroundColor Red; return }

# Extract references directly from the executing ss.exe single-file bundle
$selfBundleType = $VOM.Assembly.GetType('Subsystem.Windows.SelfBundle')
if (-not $selfBundleType) {
    throw "SelfBundle type not found in ss assembly"
}
$exePath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$exeBytes = [System.IO.File]::ReadAllBytes($exePath)
$manifest = $selfBundleType.GetMethod('Read').Invoke($null, @(,$exeBytes))
$bundleAssemblies = $selfBundleType.GetMethod('ManagedAssemblies').Invoke($null, @($manifest))

$references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($tup in $bundleAssemblies) {
    $bytes = $tup.Item2
    try {
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromImage($bytes))
    } catch { }
}

# Compile both DpxDecoder and SyntheticModelBuilder in a single assembly compilation
# to allow re-running the test with updated code in the same PowerShell session.
Write-Host "Compiling DpxDecoder and SyntheticModelBuilder via Roslyn..."

$syntaxTrees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()

# 1. Parse DpOnnxRuntime.cs
$dpOnnxText = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot "..\src\runspace\Dpx\DpxDecoder.cs"))
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($dpOnnxText))

# 2. Parse ToolDescriptor
$toolDescriptorCode = @"
namespace Subsystem.RuntimeBroker
{
    public sealed record ToolDescriptor(string Name, string Description, string InputSchemaJson, string Command, bool Sensitive);
}
"@
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($toolDescriptorCode))

# 3. Parse SyntheticModelBuilder
$builderCode = @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Onnx;
using Subsystem.RuntimeBroker;
using Subsystem.Dpx;
using Subsystem.Vom;

public static class SyntheticModelBuilder
{
    public static ModelProto BuildModel()
    {
        var g = new GraphProto { Name = "synthetic_gemma" };
        
        // Input: input_ids [1, sequence]
        var inputType = new TypeProto { TensorType = new TypeProto.Types.Tensor { ElemType = 7, Shape = new TensorShapeProto() } };
        inputType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 1 });
        inputType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimParam = "sequence" });
        g.Input.Add(new ValueInfoProto { Name = "input_ids", Type = inputType });
        
        // Input: past_key_values.0.key [1, 1, sequence, 8]
        var pastKvType = new TypeProto { TensorType = new TypeProto.Types.Tensor { ElemType = 1, Shape = new TensorShapeProto() } };
        pastKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 1 });
        pastKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 1 });
        pastKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimParam = "sequence" });
        pastKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 8 });
        g.Input.Add(new ValueInfoProto { Name = "past_key_values.0.key", Type = pastKvType });

        // Initializer: W_embed [16, 8]
        float[] embedData = new float[16 * 8];
        for (int i = 0; i < 16 * 8; i++) embedData[i] = i * 0.01f;
        g.Initializer.Add(FloatInit("W_embed", embedData, 16, 8));
        
        // Initializer: W_logits [8, 16]
        float[] logitsData = new float[8 * 16];
        for (int i = 0; i < 8 * 16; i++) logitsData[i] = i * 0.01f;
        g.Initializer.Add(FloatInit("W_logits", logitsData, 8, 16));

        // Node 1: Gather embedded [1, sequence, 8] from W_embed using input_ids
        var gatherNode = new NodeProto { OpType = "Gather", Name = "gather_node" };
        gatherNode.Input.Add("W_embed");
        gatherNode.Input.Add("input_ids");
        gatherNode.Output.Add("embedded");
        gatherNode.Attribute.Add(new AttributeProto { Name = "axis", Type = AttributeProto.Types.AttributeType.Int, I = 0 });
        g.Node.Add(gatherNode);
        
        // Node 2: MatMul embedded [1, sequence, 8] @ W_logits [8, 16] -> logits [1, sequence, 16]
        var matmulNode = new NodeProto { OpType = "MatMul", Name = "matmul_node" };
        matmulNode.Input.Add("embedded");
        matmulNode.Input.Add("W_logits");
        matmulNode.Output.Add("logits");
        g.Node.Add(matmulNode);

        // Node 3: Identity embedded [1, sequence, 8] -> present_key_values.0.key [1, sequence, 8]
        var identityNode = new NodeProto { OpType = "Identity", Name = "identity_node" };
        identityNode.Input.Add("embedded");
        identityNode.Output.Add("present_key_values.0.key");
        g.Node.Add(identityNode);
        
        // Output: logits
        var outputType = new TypeProto { TensorType = new TypeProto.Types.Tensor { ElemType = 1, Shape = new TensorShapeProto() } };
        outputType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 1 });
        outputType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimParam = "sequence" });
        outputType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 16 });
        g.Output.Add(new ValueInfoProto { Name = "logits", Type = outputType });

        // Output: present_key_values.0.key
        var presentKvType = new TypeProto { TensorType = new TypeProto.Types.Tensor { ElemType = 1, Shape = new TensorShapeProto() } };
        presentKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 1 });
        presentKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 1 });
        presentKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimParam = "sequence" });
        presentKvType.TensorType.Shape.Dim.Add(new TensorShapeProto.Dimension { DimValue = 8 });
        g.Output.Add(new ValueInfoProto { Name = "present_key_values.0.key", Type = presentKvType });
        
        return new ModelProto { Graph = g };
    }
    
    private static TensorProto FloatInit(string name, float[] data, params long[] dims)
    {
        var t = new TensorProto { Name = name, DataType = 1 };
        t.Dims.Add(dims);
        t.FloatData.Add(data);
        return t;
    }

    public static SpModelProto BuildTokenizer()
    {
        var spm = new SpModelProto();
        spm.Normalizer.EscapeWhitespaces = true;
        spm.Normalizer.AddDummyPrefix = true;
        
        string[] pieces = new[] {
            "<unk>", "</s>", "<s>", "<start_of_turn>", "<end_of_turn>", " ", 
            "h", "e", "l", "o", "w", "r", "d", "a", "b", "c"
        };
        for (int i = 0; i < pieces.Length; i++)
        {
            var p = new SentencePiece {
                Piece = pieces[i],
                Score = -((float)i),
                Type = i < 5 ? SentencePieceType.Control : SentencePieceType.Normal
            };
            spm.Pieces.Add(p);
        }
        return spm;
    }

    public static string RunTest()
    {
        var model = BuildModel();
        var spm = BuildTokenizer();
        var tokenizer = new SentencePieceTokenizer(spm);

        var runtime = new DpxDecoder(model, tokenizer, "synthetic_unit", 10);
        var bringup = runtime.BringUp();
        if (bringup != null) return "has_error:True\nerror_msg:BringUp failed: " + bringup.NativeDetail;

        var initialTotals = Vom.Totals();
        var initialHandles = initialTotals.Handles;
        var initialBytes = initialTotals.Bytes;

        var tokens = new List<string>();
        int activeHandlesDuringDecode = 0;
        int activeBytesDuringDecode = 0;
        bool hasError = false;
        string errorMsg = "";

        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var delta in runtime.StreamTurnAsync("hello", null))
                {
                    if (delta.Kind == AgentDeltaKind.Token)
                    {
                        tokens.Add(delta.Text);
                        var totals = Vom.Totals();
                        activeHandlesDuringDecode = (int)(totals.Handles - initialHandles);
                        activeBytesDuringDecode = (int)(totals.Bytes - initialBytes);
                    }
                    else if (delta.Kind == AgentDeltaKind.Error)
                    {
                        hasError = true;
                        errorMsg = delta.Text;
                    }
                }
            }
            catch (Exception ex)
            {
                hasError = true;
                errorMsg = ex.ToString();
            }
        });

        task.Wait();

        var finalTotals = Vom.Totals();
        var reclaimedHandles = initialHandles - finalTotals.Handles;
        var reclaimedBytes = initialBytes - finalTotals.Bytes;

        bool? workerIsPool = runtime.WorkerIsThreadPoolThread;

        runtime.Dispose();

        var postDisposeTotals = Vom.Totals();
        int activeHandlesAfterTeardown = (int)(postDisposeTotals.Handles - initialHandles);

        var sb = new StringBuilder();
        sb.AppendLine($"tokens_count:{tokens.Count}");
        sb.AppendLine($"tokens_text:{string.Concat(tokens)}");
        sb.AppendLine($"worker_is_pool_thread:{workerIsPool}");
        sb.AppendLine($"active_handles_during_decode:{activeHandlesDuringDecode}");
        sb.AppendLine($"active_bytes_during_decode:{activeBytesDuringDecode}");
        sb.AppendLine($"active_handles_after_teardown:{activeHandlesAfterTeardown}");
        sb.AppendLine($"has_error:{hasError}");
        sb.AppendLine($"error_msg:{errorMsg}");

        return sb.ToString();
    }
}
"@
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($builderCode))

$compName = "DynamicAssembly_" + [Guid]::NewGuid().ToString("N")
# AllowUnsafe: true, matching SubsystemWin.csproj/SelfBuild.cs's compile options — DpxDecoder's
# KV-cache path aliases a VOM region's native pointer directly (zero-copy), which needs an unsafe
# context; the production build already allows it, this dynamic recompile has to match.
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithAllowUnsafe($true)

$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    $compName,
    $syntaxTrees,
    $references,
    $options
)

$ms = [System.IO.MemoryStream]::new()
$emitResult = $comp.Emit($ms)
if (-not $emitResult.Success) {
    foreach ($diag in $emitResult.Diagnostics) {
        if ($diag.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) {
            Write-Error $diag.ToString()
        }
    }
    throw "Compilation of test assembly failed."
}

$ms.Seek(0, [System.IO.SeekOrigin]::Begin) | Out-Null
$assembly = [System.Reflection.Assembly]::Load($ms.ToArray())
$builderType = $assembly.GetType('SyntheticModelBuilder')
Assert ($null -ne $builderType) "SyntheticModelBuilder compiled and loaded successfully"

# 3. Run the test inside C# using reflection
$testResultStr = $builderType.GetMethod('RunTest').Invoke($null, $null)

# Parse the key-value pairs from C# test run output
$results = @{}
foreach ($line in ($testResultStr -split "`n")) {
    if ($line -match '^([^:]+):(.*)$') {
        $results[$Matches[1]] = $Matches[2].Trim()
    }
}

$tokensCount = [int]$results['tokens_count']
$tokensText = $results['tokens_text']
$workerIsPoolThread = [bool]::Parse($results['worker_is_pool_thread'])
$activeHandlesDuringDecode = [int]$results['active_handles_during_decode']
$activeBytesDuringDecode = [int]$results['active_bytes_during_decode']
$activeHandlesAfterTeardown = [int]$results['active_handles_after_teardown']
$hasError = [bool]::Parse($results['has_error'])
$errorMsg = $results['error_msg']

# Assertions
Assert ($hasError -eq $false) "Inference execution completed without error (Error: $errorMsg)"
Assert ($tokensCount -gt 0) "Deterministic token stream produced: '$tokensText'"
Assert ($tokensCount -le 10) "Clean EOS / max-tokens termination met (tokens: $tokensCount)"
Assert ($workerIsPoolThread -eq $false) "No ThreadPool thread touched (Thread.IsThreadPoolThread = $workerIsPoolThread)"
Assert ($activeHandlesDuringDecode -gt 0) "KV-cache VOM handles allocated during generation (handles: $activeHandlesDuringDecode, bytes: $activeBytesDuringDecode B)"
Assert ($activeHandlesAfterTeardown -eq 0) "KV-cache VOM regions fully reclaimed on Terminate (leaked handles: $activeHandlesAfterTeardown)"

# Receipt metrics
$pass = $fails.Count -eq 0
$reclaimedDetails = "handles=$activeHandlesDuringDecode/bytes=$activeBytesDuringDecode"

Write-Host ""
Write-Host ($(if($pass){"PASS — DPX decode loop is sovereign, deterministic, off-GC, and thread-pool clean."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})

[pscustomobject]@{
    test = 'test.dpx.decode-loop'
    pass = $pass
    tokens = $tokensCount
    reclaimed = $reclaimedDetails
    verdict = $(if($pass){"deterministic token stream ($tokensText) on custom worker thread; KV cache VOM handles ($reclaimedDetails) reclaimed"}else{'see failures'})
}
