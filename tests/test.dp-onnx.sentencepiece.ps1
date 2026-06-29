#requires -Version 7
# test.dp-onnx.sentencepiece.ps1 — Receipt for the home-rolled SentencePiece reader + Unigram tokenizer
# (Onnx.SentencePieceTokenizer / Onnx.SpModelProto): parse the gemma-4 .spm ModelProto with NO sentencepiece
# lib and NO protobuf lib, then prove (1) the piece table is the full gemma vocab, (2) the .spm control tokens
# resolve, and (3) encode -> detokenize round-trips. (gemma's <start_of_turn>/<end_of_turn> are added_tokens
# layered ABOVE the .spm by the HF tokenizer, not pieces in the ModelProto — correctly absent.) SKIPs without the .spm.
# Authority = the binary; this comment is not — the receipt the run prints is.
#   Dogfood:  ss run tests/test.dp-onnx.sentencepiece.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# Resolve the types-under-test from LOADED assemblies (reflection) — the tokenizer is resident in ss.exe.
$asm = [AppDomain]::CurrentDomain.GetAssemblies()
$Tk = $asm | ForEach-Object { $_.GetType('Onnx.SentencePieceTokenizer') } | Where-Object {$_} | Select-Object -First 1
$Mp = $asm | ForEach-Object { $_.GetType('Onnx.SpModelProto') }          | Where-Object {$_} | Select-Object -First 1
if(-not $Tk -or -not $Mp){ Write-Host "Onnx.SentencePieceTokenizer not loaded — cannot run." -ForegroundColor Red; return }

$spm = @($env:SS_GEMMA_SPM,'S:\modeldb\gemma-4-e2b.spm') | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if(-not $spm){ Write-Host "SKIP - gemma-4 .spm not present (set SS_GEMMA_SPM)." -ForegroundColor Yellow; return }

$bytes = [System.IO.File]::ReadAllBytes($spm)
$model = $Mp::Parse($bytes)
$tok   = [Activator]::CreateInstance($Tk, @([object]$model))
$vocab = $tok.VocabSize
$bos = $tok.FindPieceId('<bos>'); $eos = $tok.FindPieceId('<eos>'); $pad = $tok.FindPieceId('<pad>'); $unk = $tok.FindPieceId('<unk>')
Write-Host ("spm: {0} ({1:n0} B); vocab={2}; <bos>={3} <eos>={4} <pad>={5} <unk>={6}" -f $spm,$bytes.Length,$vocab,$bos,$eos,$pad,$unk)

# Round-trip a handful of plain ASCII strings (encode -> detokenize == input).
$samples = @('Hello, world!','The quick brown fox jumps over the lazy dog.','Subsystem is NT, and it is a fractal.')
$rt = $true
foreach($s in $samples){
    $ids  = $tok.Encode($s)
    $back = $tok.Detokenize($ids)
    if($back -ne $s){ $rt = $false; Write-Host "  miss: '$s' -> '$back'" -ForegroundColor Red }
    else { Write-Host "  ok roundtrip ($($ids.Count) tok): $s" }
}

Assert ($vocab -eq 262144)              "vocab == 262144 pieces parsed off the .spm ModelProto (no sentencepiece/protobuf lib)"
Assert ($bos -eq 2)                     "<bos> resolves to id 2"
Assert ($eos -ge 0 -and $pad -ge 0 -and $unk -ge 0) "control tokens present in the .spm: <eos>=$eos <pad>=$pad <unk>=$unk"
Assert $rt                              "encode -> detokenize round-trips on all samples"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - sovereign SentencePiece: gemma-4 .spm parsed + Unigram encode/detokenize bit-exact, no libs."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.dp-onnx.sentencepiece'; pass=$pass; vocab=$vocab; bos=$bos; eos=$eos; pad=$pad; verdict=$(if($pass){'home-rolled SentencePiece reader + Unigram tokenizer; gemma-4 vocab + control tokens + roundtrip proven'}else{'see failures'}) }
