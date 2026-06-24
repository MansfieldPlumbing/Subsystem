# tests/ — the proof surface (auditable standard)

The green baseline. Every test is **self-describing and runnable cold** by any session or agent
(Claude, Antigravity, Qwen, AI Studio) with no tribal knowledge. Authority is the binary + the
receipt the run prints — never this doc, never a comment.

## Naming — `test.<component>.<what>.ps1`

Dotted, lowercase, **self-explanatory**, mirroring the namespace/DAG (so the filename alone says what
it proves). One concept per file.

- `<component>` = a contract component: `vom`, `cm`, `pwsh`, `rs`, `rb`, `pp`, `device`, `dg`, …
  (run `ss contextualize` for the live list), or a surface like `gate` (the analyzer/check suite).
- `<what>` = kebab-case of the mechanism: `no-gc`, `slot-swap`, `slot-rollback`, `fence-waitn`,
  `runspace-reclaim`, `get-process`.

No other shape. (`*.tests.ps1`, camelCase, vague names → rename to conform.)

## Shape — the Cutlerian receipt template

Every test follows this so output is **auditable at a glance** (pass + measured numbers + verdict):

```powershell
#requires -Version 7
# test.<component>.<what>.ps1 — one line: what this proves.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
# Probes (A, B, …) named; dot-source-safe (no `exit`); mutates only throwaway owners it Terminates.
#   Dogfood:  ss run tests/test.<component>.<what>.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# Resolve the type-under-test from LOADED assemblies (reflection) — a test depends on no build:
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded — cannot run." -ForegroundColor Red; return }

# … probes: print a measured line, then Assert the truth …

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — <one-line verdict>"}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.<component>.<what>'; pass=$pass; <measured fields>; verdict=$(if($pass){'…'}else{'see failures'}) }
```

## Rules (auditable, enforceable by the next session)

1. **Self-describing name** — the filename says what it proves; no decoder ring.
2. **Receipt or it didn't happen** — return a `[pscustomobject]` with `test` (= the dotted name),
   `pass` (bool), the measured numbers, and a one-line `verdict`. A false field is a *tracked
   regression*, not a styling issue.
3. **Cold-runnable** — resolve types by reflection from loaded assemblies; never require a build.
4. **Dot-source-safe** — no `exit`; `return` only. So `. tests/x.ps1` and `ss run tests/x.ps1` both work.
5. **No collateral** — mutate only throwaway `\Sessions\__*` / `\Slots` owners and `Terminate` them
   (leak-proof in a `finally`). Never touch the durable registry or another owner.
6. **Sober, mechanism-named** — no marketing words, no triumph. The number is the brag.

## Running

- One test:  `ss run tests/test.vom.slot-swap.ps1`
- Kernel suite (the C# verdicts, also gated): `ss selftest` — VOM + Cm Layers 1-2, writes
  `smoketest-log.md` (the green baseline). PS receipts that exercise an in-binary `*Test()` mirror a
  `Check(...)` line in `src/runspace/windows/SelfTest.cs`.

## Inventory (keep current)

| File | Proves |
|---|---|
| `test.vom.no-gc.ps1` | data plane off the collector; runspace dies on handle revoke (invariant 7) |
| `test.vom.runspace-reclaim.ps1` | the VOM owns a runspace's lifetime; GC only sweeps allocation |
| `test.vom.slot-swap.ps1` | code slot = refcounted handle; swap + free-on-zero on code |
| `test.vom.slot-rollback.ps1` | a bad slot swap rolls back to the good slot; loser freed-on-zero |
| `test.vom.fence-waitn.ps1` | `Fence.WaitN` — the general N-of-M quorum (WaitAny=1, WaitAll=M) |
| `test.pwsh.get-process.ps1` | in-process pwsh 7.7 on CoreCLR runs a real `Get-Process` |
| `test.gate.ss026-raw-pointer.ps1` | SS026 fires on a raw pointer crossing a boundary; exempts kernel/FFI/handle |
| `test.staging.ai-transport-assets.ps1` | staged DirectPort/VOM/AI assets present; onnx-surgeon runs, Onnx.dll loads, rife_v73.bin reads |
| `test.vom.refcount.ps1` | Open/Close refcount; free-on-zero reclaims native bytes |
| `test.vom.generational-handle.ps1` | stale handle id rejected O(1); slot reuse bumps the generation (ABA-proof) |
| `test.vom.dropprefix.ps1` | DropPrefix bulk-reclaims every handle under a prefix in one pass |
| `test.vom.alignment.ps1` | every Alloc is 256-padded with a 256-aligned pointer |
| `test.vom.quota.ps1` | per-owner quota is accounted, and ADVISORY in Phase 1 (tracked, not enforced) |
| `test.vom.register-managed.ps1` | a managed object is a refcounted handle; its Reclaim fires on free-at-zero |
| `test.vom.fence-timeline.ps1` | CpuFence is a monotonic u64 timeline; no regress; instant wait-when-met |
| `test.cm.register-rehydrate.ps1` | Cm register projects to memory + durable list; clean unregister |
| `test.pwsh.project-cmdlets.ps1` | ss is a pwsh superset — built-ins + the project cmdlets in one runspace |
| `bench.vom.throughput.ps1` | **benchmark** — VOM data-plane GB/s (alloc / free-on-zero / bulk-reclaim) + handle/fence op rates |
| `bench.rb.graphruntime-kokoro-parity.ps1` | **benchmark** — the ORT-free .NET ONNX interpreter (future Rb GraphRuntime leaf) runs Kokoro-82M end-to-end: 49/49 op-types, all 2463 nodes; measures waveform rmse vs the ORT oracle (parity is the open frontier; divergence localized to `/decoder/decoder/generator/*`). Result: `*.result.md` |
| `test.vom.c-kernel.ps1` | the C VOM kernel (`src/native/vom`) compiles with `cl` and matches the C# contract — the dual-language parity seam |

**Benchmarks** use the sibling prefix `bench.<component>.<what>.ps1` — same receipt shape, but the *numbers* are the point (sanity floors only stop a silent regression to zero). Note: per-op rates driven from PowerShell are interop-bound; the kernel's true rate needs an in-binary C# bench (`ss benchmark`, CRQ114).
