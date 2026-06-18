# Code of Conduct

## Purpose

Subsystem is a small project with a sharp architecture. This document covers two things contributors are held to: how people treat each other, and how code is held to the project's discipline. Both matter; a project this opinionated needs both stated plainly.

## Conduct

This project follows the spirit of the [Contributor Covenant](https://www.contributor-covenant.org/). In short:

- Be respectful and direct. Technical disagreement is welcome; personal attacks, harassment, and demeaning behavior are not.
- Assume good faith. Most friction is a misunderstanding, not malice.
- Criticize the code, not the person. "This holds truth outside the registry" is feedback; "you're careless" is not.
- No harassment, discrimination, or unwelcome conduct of any kind, in issues, pull requests, commits, or any project space.

Unacceptable behavior can be reported to the maintainer via the contact on the GitHub profile. Reports are handled privately. The maintainer may remove comments, commits, and contributions, and may block accounts, for conduct that violates this document.

## Engineering discipline

Contributions are held to the project's one rule: **everything is an object in one namespace, and nothing holds its own truth.** This is not a style preference — it is the property the whole system depends on, and it is enforced mechanically. Before opening a pull request:

- **Read `CLAUDE.md` and `docs/`.** They are the governing law. A change that contradicts the doctrine should say so explicitly and argue for the change — not quietly diverge.
- **The build gates on the analyzers.** The `Subsystem.Analyzers` suite (`SS001`–`SS010`) flags truth held outside the namespace: PowerShell baked into C# strings, static dictionaries as parallel stores, fabricated namespace-path literals, raw memory crossing a cmdlet boundary, ambient threads outside the kernel, empty `catch {}`. A pull request that does not pass the gate does not merge. Run `ss-check` before submitting.
- **The registry is canonical.** New capabilities are registry records, not hardcoded lists. The UI renders objects and holds nothing. Behaviors are verbs on objects, not inline functions.
- **Degrade, never crash silently.** A failure returns a typed degraded result and is recorded to the diagnostic surface. An empty result and a failed result must never look the same.
- **Respect device ownership.** Any capability that reaches into hardware, capture, or off-device exposure must be gated behind explicit, informed, revocable consent. Nothing fires by default; nothing fires without the owner's intent. A contribution that weakens a consent gate will not be accepted.
- **Match the surrounding code.** Naming is mechanism-descriptive and NT-shaped — terse, impersonal, two-letter component prefixes — never anthropomorphic or marketing-flavored. Comments state constraints, not narration.
- **Capabilities are not removed without sign-off.** The substrate grows; leaves are retired deliberately, never swept away as a side effect. A pull request that removes a capability must say so in its description and is the maintainer's decision.

## Scope

This applies to all project spaces: the repository, issues, pull requests, commit messages, and any associated discussion. It applies equally to human and AI-assisted contributions — AI is a welcome force multiplier here, but every contribution is reviewed and owned by the human who submits it, and is held to the same standard regardless of how it was produced.

## Attribution

Conduct section adapted from the Contributor Covenant, version 2.1. The engineering discipline is specific to this project and reflects its architecture.
