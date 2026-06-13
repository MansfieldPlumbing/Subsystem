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
