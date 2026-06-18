# Subsystem smoke-test log

Append-only ledger, written BY the binary (`ss selftest`, `ss check --gate`) so the green baseline is
DERIVED from each run, never hand-copied into a handoff. Newest entries at the bottom. A GREEN->RED flip
on any field is a real regression, not a styling nit.

## 2026-06-13T20:37:40Z · selftest · GREEN
- Vom.SelfTest: {"owner":"\\Sessions\\__vomtest_163740","handlesBefore":4,"allocatedBytes":4194304,"fenceWorks":true,"ownerRemoved":true,"staleHandleRejected":true,"note":"native memory reclaimed via NativeMemory.AlignedFree on Terminate; see /diag events for the autopsy"}
- Vom.SpawnKillTest: {"root":"\\Sessions\\__pstest_163740","ownersBefore":3,"threadHandles":2,"bytesBefore":1024,"rootRemoved":true,"childRemoved":true,"grandchildRemoved":true,"childObservedCancel":true,"ownersAfter":0,"note":"cascade Terminate: linked-token cancel -\u003E Thread.Interrupt() -\u003E bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) \u2014 a busy/native wedge would stay resourceless residual."}
- Vom.WaitPhaseLockTest: {"waitAnyIndex":1,"waitAnyCorrect":true,"visionPhase":5,"audioPhase":5,"barrierHeldForLaggard":true,"phaseLocked":true,"note":"WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async \u2014 the fence value is the clock."}
- Cm.SelfTest: {"ok":true,"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","inMemory":true,"inDurable":true,"total":1,"note":"registered a probe capability, confirmed in-memory \u002B SQLite (WAL), then unregistered"}
- Cm rehydration: {"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","total":1,"rehydratedFromPriorRun":true,"paths":["\\Capability\\Probe\\WinHeadBoot"]}

## 2026-06-13T20:37:53Z · check --gate · GREEN
- gate: 411 findings; baseline 411; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-13T20:52:10Z · diag · GREEN
- Vom.SelfTest: {"owner":"\\Sessions\\__vomtest_165209","handlesBefore":4,"allocatedBytes":4194304,"fenceWorks":true,"ownerRemoved":true,"staleHandleRejected":true,"note":"native memory reclaimed via NativeMemory.AlignedFree on Terminate; see /diag events for the autopsy"}
- Vom.SpawnKillTest: {"root":"\\Sessions\\__pstest_165209","ownersBefore":3,"threadHandles":2,"bytesBefore":1024,"rootRemoved":true,"childRemoved":true,"grandchildRemoved":true,"childObservedCancel":true,"ownersAfter":0,"note":"cascade Terminate: linked-token cancel -\u003E Thread.Interrupt() -\u003E bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) \u2014 a busy/native wedge would stay resourceless residual."}
- Vom.WaitPhaseLockTest: {"waitAnyIndex":1,"waitAnyCorrect":true,"visionPhase":5,"audioPhase":5,"barrierHeldForLaggard":true,"phaseLocked":true,"note":"WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async \u2014 the fence value is the clock."}
- Cm.SelfTest: {"ok":true,"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","inMemory":true,"inDurable":true,"total":1,"note":"registered a probe capability, confirmed in-memory \u002B SQLite (WAL), then unregistered"}
- Cm.Rehydration: {"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","total":1,"markerPresent":true,"rehydratedFromPriorRun":true}
- Toolchain.Dotnet: {"found":true,"path":"S:\\dotnet\\dotnet.exe"}
- Toolchain.Gate: {"installed":true,"dll":"S:\\bin\\check\\subsystem-check.dll"}
- SelfCarry.EmbeddedSource: {"present":true,"fileBlocks":239}
- SelfCarry.Icon: {"embeddedIconBytes":410598}

## 2026-06-13T20:52:20Z · selftest · GREEN
- Vom.SelfTest: {"owner":"\\Sessions\\__vomtest_165220","handlesBefore":4,"allocatedBytes":4194304,"fenceWorks":true,"ownerRemoved":true,"staleHandleRejected":true,"note":"native memory reclaimed via NativeMemory.AlignedFree on Terminate; see /diag events for the autopsy"}
- Vom.SpawnKillTest: {"root":"\\Sessions\\__pstest_165220","ownersBefore":3,"threadHandles":2,"bytesBefore":1024,"rootRemoved":true,"childRemoved":true,"grandchildRemoved":true,"childObservedCancel":true,"ownersAfter":0,"note":"cascade Terminate: linked-token cancel -\u003E Thread.Interrupt() -\u003E bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) \u2014 a busy/native wedge would stay resourceless residual."}
- Vom.WaitPhaseLockTest: {"waitAnyIndex":1,"waitAnyCorrect":true,"visionPhase":5,"audioPhase":5,"barrierHeldForLaggard":true,"phaseLocked":true,"note":"WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async \u2014 the fence value is the clock."}
- Cm.SelfTest: {"ok":true,"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","inMemory":true,"inDurable":true,"total":1,"note":"registered a probe capability, confirmed in-memory \u002B SQLite (WAL), then unregistered"}
- Cm.Rehydration: {"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","total":1,"markerPresent":true,"rehydratedFromPriorRun":true}

## 2026-06-13T22:23:08Z · diag · GREEN
- Vom.SelfTest: {"owner":"\\Sessions\\__vomtest_182308","handlesBefore":4,"allocatedBytes":4194304,"fenceWorks":true,"ownerRemoved":true,"staleHandleRejected":true,"note":"native memory reclaimed via NativeMemory.AlignedFree on Terminate; see /diag events for the autopsy"}
- Vom.SpawnKillTest: {"root":"\\Sessions\\__pstest_182308","ownersBefore":3,"threadHandles":2,"bytesBefore":1024,"rootRemoved":true,"childRemoved":true,"grandchildRemoved":true,"childObservedCancel":true,"ownersAfter":0,"note":"cascade Terminate: linked-token cancel -\u003E Thread.Interrupt() -\u003E bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) \u2014 a busy/native wedge would stay resourceless residual."}
- Vom.WaitPhaseLockTest: {"waitAnyIndex":1,"waitAnyCorrect":true,"visionPhase":5,"audioPhase":5,"barrierHeldForLaggard":true,"phaseLocked":true,"note":"WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async \u2014 the fence value is the clock."}
- Cm.SelfTest: {"ok":true,"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","inMemory":true,"inDurable":true,"total":1,"note":"registered a probe capability, confirmed in-memory \u002B SQLite (WAL), then unregistered"}
- Cm.Rehydration: {"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","total":1,"markerPresent":true,"rehydratedFromPriorRun":true}
- Toolchain.Dotnet: {"found":true,"path":"S:\\dotnet\\dotnet.exe"}
- Toolchain.Gate: {"installed":true,"dll":"S:\\bin\\check\\subsystem-check.dll"}
- SelfCarry.EmbeddedSource: {"present":true,"fileBlocks":240}
- SelfCarry.Icon: {"embeddedIconBytes":410598}

## 2026-06-13T22:24:06Z · diag · GREEN
- Vom.SelfTest: {"owner":"\\Sessions\\__vomtest_182405","handlesBefore":4,"allocatedBytes":4194304,"fenceWorks":true,"ownerRemoved":true,"staleHandleRejected":true,"note":"native memory reclaimed via NativeMemory.AlignedFree on Terminate; see /diag events for the autopsy"}
- Vom.SpawnKillTest: {"root":"\\Sessions\\__pstest_182405","ownersBefore":3,"threadHandles":2,"bytesBefore":1024,"rootRemoved":true,"childRemoved":true,"grandchildRemoved":true,"childObservedCancel":true,"ownersAfter":0,"note":"cascade Terminate: linked-token cancel -\u003E Thread.Interrupt() -\u003E bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) \u2014 a busy/native wedge would stay resourceless residual."}
- Vom.WaitPhaseLockTest: {"waitAnyIndex":1,"waitAnyCorrect":true,"visionPhase":5,"audioPhase":5,"barrierHeldForLaggard":true,"phaseLocked":true,"note":"WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async \u2014 the fence value is the clock."}
- Cm.SelfTest: {"ok":true,"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","inMemory":true,"inDurable":true,"total":1,"note":"registered a probe capability, confirmed in-memory \u002B SQLite (WAL), then unregistered"}
- Cm.Rehydration: {"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","total":1,"markerPresent":true,"rehydratedFromPriorRun":true}
- Toolchain.Dotnet: {"found":true,"path":"S:\\dotnet\\dotnet.exe"}
- Toolchain.Gate: {"installed":true,"dll":"S:\\bin\\check\\subsystem-check.dll"}
- SelfCarry.EmbeddedSource: {"present":true,"fileBlocks":240}
- SelfCarry.Icon: {"embeddedIconBytes":410598}

## 2026-06-14T03:35:34Z · diag · GREEN
- Vom.SelfTest: {"owner":"\\Sessions\\__vomtest_233533","handlesBefore":4,"allocatedBytes":4194304,"fenceWorks":true,"ownerRemoved":true,"staleHandleRejected":true,"note":"native memory reclaimed via NativeMemory.AlignedFree on Terminate; see /diag events for the autopsy"}
- Vom.SpawnKillTest: {"root":"\\Sessions\\__pstest_233533","ownersBefore":3,"threadHandles":2,"bytesBefore":1024,"rootRemoved":true,"childRemoved":true,"grandchildRemoved":true,"childObservedCancel":true,"ownersAfter":0,"note":"cascade Terminate: linked-token cancel -\u003E Thread.Interrupt() -\u003E bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) \u2014 a busy/native wedge would stay resourceless residual."}
- Vom.WaitPhaseLockTest: {"waitAnyIndex":1,"waitAnyCorrect":true,"visionPhase":5,"audioPhase":5,"barrierHeldForLaggard":true,"phaseLocked":true,"note":"WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async \u2014 the fence value is the clock."}
- Cm.SelfTest: {"ok":true,"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","inMemory":true,"inDurable":true,"total":1,"note":"registered a probe capability, confirmed in-memory \u002B SQLite (WAL), then unregistered"}
- Cm.Rehydration: {"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","total":1,"markerPresent":true,"rehydratedFromPriorRun":true}
- Toolchain.Dotnet: {"found":true,"path":"S:\\dotnet\\dotnet.exe"}
- Toolchain.Gate: {"installed":true,"dll":"S:\\bin\\check\\subsystem-check.dll"}
- SelfCarry.EmbeddedSource: {"present":true,"fileBlocks":244}
- SelfCarry.Icon: {"embeddedIconBytes":410598}

## 2026-06-14T03:40:55Z · check --gate · GREEN
- gate: 411 findings; baseline 411; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-14T03:44:57Z · check --gate · GREEN
- gate: 409 findings; baseline 411; new 0; retired 2
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: GREEN — no new violations.

## 2026-06-14T03:45:19Z · check --gate · GREEN
- gate: baseline written — 409 entries -> S:\subsystem\src\analyzers\SS-BASELINE.txt

## 2026-06-14T03:45:24Z · check --gate · GREEN
- gate: 409 findings; baseline 409; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-14T07:48:42Z · selftest · GREEN
- Vom.SelfTest: {"owner":"\\Sessions\\__vomtest_034841","handlesBefore":4,"allocatedBytes":4194304,"fenceWorks":true,"ownerRemoved":true,"staleHandleRejected":true,"note":"native memory reclaimed via NativeMemory.AlignedFree on Terminate; see /diag events for the autopsy"}
- Vom.SpawnKillTest: {"root":"\\Sessions\\__pstest_034841","ownersBefore":3,"threadHandles":2,"bytesBefore":1024,"rootRemoved":true,"childRemoved":true,"grandchildRemoved":true,"childObservedCancel":true,"ownersAfter":0,"note":"cascade Terminate: linked-token cancel -\u003E Thread.Interrupt() -\u003E bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) \u2014 a busy/native wedge would stay resourceless residual."}
- Vom.WaitPhaseLockTest: {"waitAnyIndex":1,"waitAnyCorrect":true,"visionPhase":5,"audioPhase":5,"barrierHeldForLaggard":true,"phaseLocked":true,"note":"WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async \u2014 the fence value is the clock."}
- Cm.SelfTest: {"ok":true,"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","inMemory":true,"inDurable":true,"total":1,"note":"registered a probe capability, confirmed in-memory \u002B SQLite (WAL), then unregistered"}
- Cm.Rehydration: {"dbPath":"C:\\Users\\Scott\\AppData\\Local\\Subsystem\\subsystem-registry.db","total":1,"markerPresent":true,"rehydratedFromPriorRun":true}

## 2026-06-14T07:48:48Z · check --gate · GREEN
- gate: 406 findings; baseline 406; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-14T08:31:51Z · check --gate · GREEN
- gate: 406 findings; baseline 406; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-14T08:54:22Z · check --gate · GREEN
- gate: 344 findings; baseline 406; new 0; retired 62
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: GREEN — no new violations.

## 2026-06-14T08:54:46Z · check --gate · GREEN
- gate: 344 findings; baseline 344; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-14T09:42:05Z · check --gate · RED
- gate: 345 findings; baseline 344; new 1; retired 0
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-14T09:42:48Z · check --gate · GREEN
- gate: 345 findings; baseline 345; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-14T18:52:04Z · check --gate · GREEN
- gate: 345 findings; baseline 345; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-15T03:00:49Z · check --gate · RED
- gate: 354 findings; baseline 350; new 4; retired 0
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-15T03:06:03Z · check --gate · GREEN
- gate: 350 findings; baseline 350; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-15T04:30:56Z · check --gate · GREEN
- gate: 355 findings; baseline 355; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-15T13:22:50Z · check --gate · RED
- gate: 359 findings; baseline 355; new 4; retired 0
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-15T13:23:37Z · check --gate · GREEN
- gate: 355 findings; baseline 355; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-15T13:50:49Z · check --gate · GREEN
- gate: 355 findings; baseline 355; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-15T15:58:35Z · check --gate · GREEN
- gate: 355 findings; baseline 355; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-15T16:30:15Z · check --gate · GREEN
- gate: baseline written — 343 entries -> S:\subsystem\src\analyzers\SS-BASELINE.txt

## 2026-06-15T16:32:49Z · check --gate · GREEN
- gate: 343 findings; baseline 343; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-15T16:43:26Z · check --gate · RED
- gate: 345 findings; baseline 343; new 2; retired 0
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-15T16:45:01Z · check --gate · GREEN
- gate: 343 findings; baseline 343; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-16T21:55:32Z · check --gate · GREEN
- gate: 318 findings; baseline 318; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-16T23:11:58Z · check --gate · GREEN
- gate: 318 findings; baseline 318; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-17T15:24:40Z · check --gate · RED
- gate: 302 findings; baseline 318; new 1; retired 17
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-17T15:48:52Z · check --gate · RED
- gate: 302 findings; baseline 318; new 1; retired 17
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T00:23:02Z · check --gate · RED
- gate: 302 findings; baseline 318; new 1; retired 17
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T00:47:20Z · check --gate · RED
- gate: 338 findings; baseline 318; new 42; retired 22
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T00:51:59Z · check --gate · RED
- gate: 338 findings; baseline 318; new 42; retired 22
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T00:54:20Z · check --gate · RED
- gate: 336 findings; baseline 318; new 40; retired 22
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T00:54:31Z · check --gate · GREEN
- gate: 336 findings; baseline 336; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T00:58:21Z · check --gate · GREEN
- gate: 336 findings; baseline 336; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T02:13:31Z · check --gate · RED
- gate: 337 findings; baseline 336; new 1; retired 0
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T02:14:07Z · check --gate · GREEN
- gate: 337 findings; baseline 337; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T02:19:27Z · check --gate · RED
- gate: 341 findings; baseline 337; new 7; retired 3
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T02:19:39Z · check --gate · GREEN
- gate: 341 findings; baseline 341; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T02:22:31Z · check --gate · RED
- gate: 338 findings; baseline 341; new 3; retired 6
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: NEW violations (not in baseline) — the gate bleeds red here:

## 2026-06-18T02:22:43Z · check --gate · GREEN
- gate: 338 findings; baseline 338; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T02:24:32Z · check --gate · GREEN
- gate: 338 findings; baseline 338; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T02:37:53Z · check --gate · GREEN
- gate: 289 findings; baseline 338; new 0; retired 49
- gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.
- gate: GREEN — no new violations.

## 2026-06-18T02:38:11Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T05:23:05Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T05:38:26Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T05:56:32Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T06:06:32Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T16:02:37Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T16:03:52Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T16:10:11Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T16:24:23Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T16:36:38Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T16:42:36Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T16:56:17Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.

## 2026-06-18T18:02:58Z · check --gate · GREEN
- gate: 289 findings; baseline 289; new 0; retired 0
- gate: GREEN — no new violations.
