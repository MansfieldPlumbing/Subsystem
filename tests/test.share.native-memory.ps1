#requires -Version 7
# test.share.native-memory.ps1 — THE REFERENCE family-tree smoke experiment ("share" suite). A
# native-memory region is the canonical VOM family member; every other member (managed object, Thread,
# Runtime, code Slot, Sub-VOM child, a foreign DirectPort surface) is the SAME shape with one organ
# swapped. The shape, run end to end against the real Vom kernel:
#
#   CREATE   — CreateOwner + Alloc a 256-aligned native region WITH a Fence (the doorbell).
#   USE      — write a known byte pattern straight into the region's native pointer (Handle.Resource).
#   FENCE    — Signal the producer doorbell, Wait the consumer side (monotonic u64 timeline).
#   VERIFY   — read the pattern back through the same pointer; the bytes survived + the fence phased.
#   RECLAIM  — Terminate the owner (cooperative cancel -> bulk AlignedFree -> owner dropped).
#   ASSERT-RECLAIMED — the handle no longer resolves (by path AND by generational id), and the owner
#                      is gone. Free-at-zero proven on real native memory.
#
# Authority = the binary; this comment is not. Dot-source-safe; throwaway owner only.
#   Dogfood:  ss -File tests/test.share.native-memory.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# Reflect into the loaded Subsystem.Vom (same head as bench.vom.throughput.ps1 / test.vom.*.ps1).
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$M = [System.Runtime.InteropServices.Marshal]
$p = "\Sessions\__share_$(Get-Date -f HHmmssfff)"
$o = $V::CreateOwner($p)
try {
    # --- CREATE: a native region with a doorbell fence ---
    $req = 4096
    $h   = $V::Alloc($o, $req, [Subsystem.Vom.VomFormat]::Bytes, "ShareRegion", $true, "Objects", "byte-lane")
    $id  = $h.Id
    $ptr = $h.Resource                       # nint: the 256-aligned NativeMemory pointer
    $f   = $V::GetFence($o, $id)             # the CpuFence doorbell (withFence:$true)
    Write-Host "create: path=$($h.Path) byteCount=$($h.ByteCount) ptr%256=$($ptr.ToInt64() % 256) hasFence=$($null -ne $f)"
    Assert ($ptr.ToInt64() -ne 0)            "Alloc handed back a non-null native region pointer"
    Assert (($ptr.ToInt64() % 256) -eq 0)    "the region is 256-byte aligned (DMA / row-pitch ready)"
    Assert ($h.ByteCount -ge $req)           "the padded region covers the requested size"
    Assert ($null -ne $f)                    "a doorbell fence is attached to the region"

    # --- USE: write a known byte pattern straight into the shared region ---
    $pattern = [byte[]](0xDE,0xAD,0xBE,0xEF,0x10,0x20,0x30,0x40)
    for ($i = 0; $i -lt $pattern.Length; $i++) { $M::WriteByte($ptr, $i, $pattern[$i]) }
    # write a sentinel at the far end too, to prove the whole padded stride is real backing memory
    $M::WriteByte($ptr, ($h.ByteCount - 1), 0x99)

    # --- FENCE/PHASE: producer signals the doorbell, consumer waits the timeline ---
    $v0 = $f.CompletedValue
    $f.Signal(7)                             # producer advances the timeline to phase 7
    $sw = [Diagnostics.Stopwatch]::StartNew(); $f.Wait(7); $waitMs = $sw.ElapsedMilliseconds   # consumer: already met -> instant
    $vPhase = $f.CompletedValue

    # --- VERIFY: read the pattern back through the same pointer ---
    $readBack = for ($i = 0; $i -lt $pattern.Length; $i++) { $M::ReadByte($ptr, $i) }
    $sentinel = $M::ReadByte($ptr, ($h.ByteCount - 1))
    $match = -not (Compare-Object $pattern $readBack)
    Write-Host ("use/verify: wrote {0} read {1} sentinel=0x{2:X2} ; fence {3}->{4} wait={5}ms" -f `
        (($pattern  | ForEach-Object { '0x{0:X2}' -f $_ }) -join ' '), `
        (($readBack | ForEach-Object { '0x{0:X2}' -f $_ }) -join ' '), $sentinel, $v0, $vPhase, $waitMs)
    Assert $match                            "the byte pattern read back byte-for-byte from the native region"
    Assert ($sentinel -eq 0x99)              "the last byte of the padded stride is real backing memory"
    Assert ($v0 -eq 0)                       "the fresh doorbell started at phase 0"
    Assert ($vPhase -eq 7)                   "Signal advanced the doorbell and Wait observed phase 7"
    Assert ($waitMs -lt 50)                  "Wait returned at once once the phase was met (monotonic timeline)"

    # --- pre-teardown sanity: the handle resolves by path and its id is live ---
    $resolvedBefore = $V::TryGetByPath($o, $h.Path, [ref]([Subsystem.Vom.Handle]::new()))
    $idLiveBefore   = $o.Handles.IsValid($id)
    Assert $resolvedBefore "the region resolves by its namespace path while live"
    Assert $idLiveBefore   "the region's generational id is valid while live"

    # --- RECLAIM: Terminate the owner (the ONLY teardown call) ---
    $bytesBefore = [System.Threading.Interlocked]::Read([ref]$o.CurrentBytes)
    $V::Terminate($o)

    # --- ASSERT-RECLAIMED: handle gone by path + by id, owner dropped (free-at-zero) ---
    $resolvedAfter = $V::TryGetByPath($o, $h.Path, [ref]([Subsystem.Vom.Handle]::new()))
    $idStale       = -not $o.Handles.IsValid($id)
    $ownerGone     = $null -eq $V::GetOwner($p)
    Write-Host "reclaim: bytesBefore=$bytesBefore resolvedAfter=$resolvedAfter idStale=$idStale ownerGone=$ownerGone"
    Assert ($bytesBefore -ge $h.ByteCount) "the owner accounted the region's native bytes before teardown"
    Assert (-not $resolvedAfter)           "the region no longer resolves by path after Terminate (reclaimed)"
    Assert $idStale                        "the region's generational id is rejected O(1) after Terminate"
    Assert $ownerGone                      "the owner tree was dropped deterministically (free-at-zero)"
}
finally { if ($null -ne $V::GetOwner($p)) { try { $V::Terminate($o) } catch {} } }   # leak-proof on early throw

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — native-memory share: CREATE->USE->FENCE->VERIFY->RECLAIM->ASSERT-RECLAIMED, all on the real VOM."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.share.native-memory'; pass=$pass; verdict=$(if($pass){'native region: aligned alloc, byte-pattern round-trip, doorbell phase, deterministic reclaim'}else{'see failures'}) }
