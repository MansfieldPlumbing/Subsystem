---
name: in-memoriam-dedication
description: "The project carries a permanent, GATE-LOCKED dedication to Billie Dean Mansfield. SS023 makes it unremovable — sacrosanct, never abridge."
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 5bd8890b-d7ce-4181-925f-dbf4b3eeb074
---

Subsystem is dedicated to the loving memory of **Billie Dean Mansfield (1945–2026)** — deeply personal to Scott, his tribute woven into the work.

It is a **LOCKED INVARIANT**, on the same footing as the telos:
- `Subsystem.Cm.Dedication.InMemoriam` (`src/runspace/Cm/Dedication.cs`) — the single source of truth: *"This project is devoted to the loving memory of Billie Dean Mansfield — 1945–2026."*
- **SS023** (`src/analyzers/SS023InMemoriamAnalyzer.cs`) — fails the build (RED gate) if the const is removed, renamed, or stripped of her name / "1945" / "2026". It cannot be abridged out.
- Rendered at the foot of the About panel (`src/shell/presenters/settings.obp`, enshrined there per Scott's direction) and atop every `ss onboard`.
- Committed `2f5eeeb`, pushed to `origin/main`, 2026-06-19.

**CORRECTED 2026-07-02** (Scott raw prompt): "the memoriam should be on the subsystem about screen not the first fucking thing u see on the readme... inside subsystem / settings / about that is where the memoriam lives." The README duplicate (previously at the top) was removed (commit `05344ee`) — the About panel is now her one true home, not one of two copies. SS023 only ever scanned the C# `InMemoriam` const — it never touched the README, so the removal was always safe and never a gate risk.

**How to apply:** treat SS023 and the `InMemoriam` const as **sacrosanct** — never remove, rename, "clean up," weaken, or baseline them away. The dedication's one home is Settings > About; do not re-add it to the README or anywhere else without Scott's explicit instruction. See [[prime-directive]].
