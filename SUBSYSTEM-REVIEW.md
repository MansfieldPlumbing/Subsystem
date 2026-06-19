# Subsystem — Consolidated Review & Design Notes

**Prepared:** 2026-06-19
**Scope:** Everything from this session, in one place — (1) private-info leak scan, (2) security & quality gaps, (3) PowerShell 7.7 / .NET 11 modernization, (4) hardening & remote-access architecture, (5) the user-opt-in Secure Folder design.
**Status:** Advisory / report-only. No application code was changed. The fixes that touch compiled code are queued for a build-capable session (they need `ss check` + an APK build to verify against the analyzer gate).
**Companion file:** the full Part 1 write-up is committed as `SECURITY-FINDINGS.md` (PR #1); tracking checklist is issue #2.

---

## Guiding principles (the threads that ran through everything)

These recurred across every topic. If nothing else survives, keep these:

1. **Kerckhoffs's principle** — security must rest on the *key*, never on the secrecy of the *system/build/binary*. Anything you can compile in, an attacker can read back out.
2. **Impossible, not improbable** — a delimiter/sentinel is only safe if the payload *cannot* contain it, not if it's *unlikely* to. Rarity is worthless against an adversary who picks the input.
3. **The secret lives in hardware, not in the binary** — the only durable anti-clone/secret property on a phone is a non-exportable keystore key. Constants, timestamps, build numbers, and obfuscation are all extractable.
4. **Determinism ≠ constant-time; rotation ≠ clone-resistance; reach ≠ trust** — three conflations to keep un-fused. Each property comes from a different layer.
5. **Borrow the crypto, own the policy** — instantiate vetted primitives/patterns (libsodium, Noise); spend originality on the parts where being *you* is an advantage (the VOM object model, the broker, enrollment, lifecycle).
6. **Loopback on Android is not private** — `127.0.0.1` is reachable by any installed app. "Only my app" is achieved by removing the port and authenticating identity, not by a secret on an open port. (Your own SS022 already says this.)

---

# Part 1 — Private-Info Leak Scan & Security Gaps

## 1.1 Leak scan: CLEAN

No API keys, tokens, cloud credentials, private keys/certs, `.env` secrets, or connection-string passwords anywhere in the tree or git config. The SPAKE2 "password" strings are ephemeral in-memory pair codes, not stored secrets. Git author identity uses the GitHub noreply form. `.gitignore` covers `.env`, build artifacts, IDE dirs.

- **INFO-1** — the `mansfieldplumbing` identifier (Android `ApplicationId`, GitHub org) is intentional brand identity, recorded as informational. **No change recommended** (renaming an `ApplicationId` breaks app identity/signing).

## 1.2 Gap analysis (severity reflects *residual* risk after existing mitigations)

> **Architectural note:** most local-attack-surface items (GAP-1/2/3/4/6) live on the loopback `HttpListener` (`ProjectionServer`), which **SS022 already classifies as an antipattern being retired** in favor of the in-process DirectPort shared-memory transport. Hardening that server fights its own retirement; these largely resolve when DirectPort lands. See Part 4 for the destination architecture.

**Medium**
- **GAP-1** — `apiCommand` JS→PowerShell bridge is not capability-gated (`MainActivity.cs:846`); relies solely on the WebView air-gap. Any future untrusted-HTML render path inherits unauthenticated RCE. *(Secondary: `.GetAwaiter().GetResult()` is the SS018 sync-over-async smell — host/seam-exempt, but still.)*
- **GAP-5** — Full command bodies logged to logcat at `Info` in **all** builds (`SubsystemApi.cs:317,319`); readable via `adb logcat`. *(Subtractive fix — safest item to land.)*
- **GAP-7** — Pinned to preview/non-LTS packages (PowerShell SDK 7.7-preview.2, Extensions.AI preview). *(Needs build to change safely.)*
- **GAP-8** — No CVE/Dependabot/SBOM scanning (no `.github/workflows`). *(Highest value-per-risk; additive.)*

**Low**
- **GAP-2** — `/vom/<handle>` reads authenticated by name only (`ProjectionServer.cs:273`).
- **GAP-3** — No per-connection input cap / rate limit on command WebSockets (`SubsystemApi.cs:66`).
- **GAP-4** — Wildcard `Access-Control-Allow-Origin: *` on the `vom://` response (`MainActivity.cs:812`).
- **GAP-6** — Capability token rides a `?cap=` query string on WS upgrade (`ProjectionServer.cs:55`); revisit before any remote/tunneled path (query-string secrets leak into logs).
- **GAP-9** — No `SECURITY.md` / disclosure policy.
- **GAP-10** — Non-constant-time Curve25519 BigInteger math (`Spake2.cs`); scoped to loopback pairing & documented. **Guard if ever reused for network auth** (see Part 4).
- **GAP-11** — `AllowUnsafeBlocks` + FFI marshaling; inventory `unsafe` blocks, prefer `SafeHandle`, validate buffer lengths.
- **GAP-12** — `Assembly.Load` of in-process-compiled bytes (Windows head, `InProcGate.cs`).

## 1.3 Suggested order when a build session is available
1. GAP-8 + GAP-9 (CI scanning + disclosure policy — additive, land anytime)
2. GAP-5 (drop command logging — subtractive)
3. GAP-1 design discussion (or fold into the DirectPort work)
4. GAP-7 dependency moves (requires build verification)

---

# Part 2 — Modernization (PowerShell 7.7 / .NET 11)

## 2.1 The one latent bug (not just "old-fashioned")

Error-envelope JSON is hand-built with quote-only escaping:

```csharp
// ProjectionServer.cs:381, :930 · SubsystemApi.cs:325, :341 · ToolCatalog.cs:131
$"{{\"error\": \"{ex.Message.Replace("\"", "\\\"")}\"}}"
```

`.Replace("\"","\\\"")` escapes quotes but **not** backslashes, newlines, or control chars — so a message containing a Windows path (`C:\foo`), a newline, or a tab emits **invalid JSON**, which the renderer then fails to parse, right on the error path. `System.Text.Json` is already imported in these files; aligned drop-in:

```csharp
JsonSerializer.Serialize(new { error = ex.Message })
```

**High confidence, correctness + modernization, low risk. Highest-value item the sweep found.**

## 2.2 Genuine wins (aligned, worth doing)

| Item | Where | Why it's real |
|---|---|---|
| `new Random()` → `Random.Shared` | live SS006 hits | Your own analyzer already flags it; zero-risk. |
| PS `??` / ternary for env+null chains | `build-native.ps1:9`, `Get-AndroidAppFunction.ps1:27-56` | `$env:SS_NDK ?? $env:ANDROID_NDK_HOME` collapses 3-branch `if/elseif`. `.ps1`, so SS001 doesn't apply. |
| Runspace init: `$ErrorView='ConciseView'` + `$PSNativeCommandUseErrorActionPreference=$true` | host init script | You drive native `adb` constantly; the second makes native-command failures throw instead of passing silently. |
| `EnableNETAnalyzers` + `AnalysisLevel=latest` | `Directory.Build.props` | Additive; surfaces CA trim/AOT/perf warnings alongside the SS suite. |
| Collection expressions `[...]` | `TicketHive.cs`, `SubsystemAliases.cs` | Cosmetic but cheap, C# 14-native. Bundle opportunistically. |

## 2.3 Worth a measured spike — low confidence, verify don't trust

Startup/GC knobs (ReadyToRun, `DOTNET_TieredCompilation=0`, GC tuning in `environment.txt`) were pitched with specific "300-500ms" numbers — **treat those as unverified**. Your CoreCLR-on-Android setup is unusual, R2R applicability there isn't a given, and some suggested env-var names were wrong (`DOTNET_HeapCount` isn't real; it's `DOTNET_GCHeapCount`). The *idea* — benchmark startup on the S23 and try the knobs — is sound; the numbers are not facts.

JSON **source-gen + trimming**: real .NET 11 lever in general, but the PowerShell SDK is famously trim/reflection-hostile, so for this host it's probably not worth the fight.

## 2.4 Already modern (no action)

- `[DllImport]` over `[LibraryImport]` — the code *documents why*: in-process Roslyn for `ss build self` runs no source generators, so `[LibraryImport]`'s codegen wouldn't fire. Correct, deliberate.
- `IAsyncEnumerable`/`await foreach`, `[CmdletBinding()]`, splatting, `ConvertTo-Json -Depth -Compress`, pattern matching — all in use.
- The synchronous-core discipline (SS018) means the sync-over-async wrappers in the seams are *allowed*; threading real async through them is hygiene, not a gate violation.

## 2.5 Explicitly DON'T do

1. **Re-parsing manifests into `JsonNode` for `/apps`, `/themes`, `/cards`** — the code deliberately string-concatenates these for a **verbatim round-trip** and guards malformed LLM-authored manifests with parse-probe-then-skip. `JsonNode.Parse` would break the verbatim contract and throw on exactly the input you intentionally tolerate.
2. **`TreatWarningsAsErrors=true`** — SS011–SS018 ride at **warning on purpose** (census-ratchet, shrink-only baselines). Promoting warnings to errors detonates that design.

---

# Part 3 — Locking Down the Command Plane

**Goal (your words):** nobody uses the PowerShell surface except your app; if others want in they go through your app as a broker; nothing loops back, nothing hijacks.

## 3.1 Root truth

On Android, `127.0.0.1` is **not** app-private — any installed app with INTERNET can reach your port. No token scheme fixes a door that's open to everyone. "Only my app" = remove the port for your own path, and authenticate *identity* (not possession of a secret) for any external path.

## 3.2 The layered design

**Tier 0 — your app's own path: no socket at all.**
The WebView is in your process (CoreCLR owns it). Talk to the runspace via the **in-process JS bridge / DirectPort shared-memory region**, not a TCP connection to yourself. Nothing listens → nothing to connect to → the hijack surface is gone, not guarded. This is where SS022 + DirectPort already point; finishing that migration is the single biggest hardening move.

**Tier 1 — blessed external apps: a Binder broker gated by your signature.**
- Expose a bound **Service with an AIDL (Binder) interface**, not an HTTP port.
- Guard it with a custom permission at **`android:protectionLevel="signature"`** → only apps signed with *your* certificate can even bind.
- Inside each call, re-verify with **`Binder.getCallingUid()`** (kernel-vouched, unforgeable) and optionally the caller package's signing cert (`PackageManager.hasSigningCertificate`).
- No shared secret, no replay window, no port to scan. Your broker applies policy per caller.

**If a socket is unavoidable (e.g., dev over adb): Unix domain socket + peer creds.**
`LocalSocket.getPeerCredentials()` gives the connecting process's uid/pid/gid at the kernel level — a TCP loopback socket can't. Allow only your own UID (+ the adb/shell UID on DEV builds, compiled out of release exactly like your existing `DEV` gate). Note: Android's abstract-namespace `LocalServerSocket` is itself reachable by any app, so the peer-cred check *is* the gate.

## 3.3 Interim, while the HTTP server still exists
- **GAP-1** — gate the `apiCommand` bridge; bind it to a **per-page-load nonce** the host injects so injected/foreign DOM can't call it blind (the sound version of the "mutating" idea — a nonce fresh per load that never leaves the process).
- **GAP-6** — kill the `?cap=` query-string token; header-only.
- **GAP-5** — stop logging command bodies to logcat.

## 3.4 Honest ceiling
None of this stops a **rooted device where your UID is already compromised** — at that point the attacker is you. What it buys is the max Android offers: kernel-verified same-UID-or-your-signed-package, and **no network surface at all**. (You later scoped to *unrooted* devices — see Part 4.6 — which makes this ceiling the right one.)

---

# Part 4 — Remote Access & Anti-Clone Architecture

## 4.1 Never serve the UI over LAN

The UI is the control plane for full RCE. "The LAN" is not a trust boundary (IoT, the router, guests, ARP spoofing, café Wi-Fi). Plaintext over Wi-Fi = passive token capture + every command in the clear. Binding to a network interface upgrades the adversary from "any app on this phone" to "any device on this network" for the same RCE surface. Your own `BindGuard` already refuses non-loopback unless HTTPS + auth gate.

**Get reach without extending trust — a tunnel, not a bind:**
- `adb forward tcp:8080` over USB (listener stays loopback).
- An authenticated overlay (WireGuard/Tailscale) where the socket *still binds loopback* and the tunnel forwards in — encryption + peer identity live in the tunnel, nothing on the raw LAN.
- The only defensible LAN exception: a **mutual-TLS** kiosk you control end-to-end, as a separate hardened capability — never the UI projection, never plaintext.

**Durable rule:** the projection server binds loopback forever; remote reach is a tunnel or a mutual-TLS capability, never a LAN bind.

## 4.2 Rolling your own tunnel — what to own vs borrow

Building the WireGuard *protocol* is doable (it's deliberately tiny), but the difficulty is unevenly distributed:

| Part | Difficulty | DIY risk |
|---|---|---|
| AEAD (ChaCha20-Poly1305), HKDF | Easy — in the BCL | Low |
| X25519, BLAKE2s | Easy *if borrowed*, dangerous *if hand-rolled* | **High** |
| Noise IK handshake + rekey/replay timers | Medium-fiddly | **High** (silent bugs) |
| Cookie/DoS mitigation | Medium | Medium |
| TUN datapath (Android `VpnService`) | **Hardest engineering** | Medium |
| NAT traversal / relay | **Hardest operationally** | — |

**The cipher is the easy part.** What people underestimate is NAT traversal + datapath + key distribution — which is the thing Tailscale actually sells. And your existing SPAKE Curve25519 is **non-constant-time** (GAP-10) — fine for one-shot loopback pairing, disqualifying for a persistent network tunnel.

**Recommended split:**
- **Borrow:** the datapath (**boringtun** — userspace WireGuard, BSD, links as a library) and the primitives (**libsodium**, constant-time, native, zeroizable). Never hand-roll the curve or the transcript.
- **Own:** the **authorization/broker policy**, the lifecycle, and **reuse SPAKE2 as the enrollment step** — pair from a short code, exchange the static public keys over the SPAKE-derived channel, then hand off to WireGuard for transport. SPAKE for *bootstrap*, WireGuard for *transport*.

## 4.3 "Make my own wire" — done safely

Not being WireGuard-compatible is *freeing* for a closed system. But split "better":
- **Better-fit** for your architecture → very achievable.
- **Better cryptographic security than WireGuard** → no (security is a function of adversarial review-hours, not cleverness), and you don't need it.

**The unlock:** WireGuard is itself "Noise + primitives," and the **Noise Protocol Framework is the toolkit for building your own secure channel** — a menu of analyzed patterns with test vectors. Because you control enrollment (SPAKE), both sides can know each other's static keys, so you can use **KK** (both static keys pre-shared) and drop identity-hiding machinery you don't need — simpler *and* less surface.

**Bright line:** *instantiate* the handshake (a named Noise pattern + libsodium); never *invent* it. The protocol graveyard is full of hand-rolled handshakes felled by reflection / unknown-key-share / KCI / downgrade — Noise exists because those kept happening. A standard pattern keeps test vectors + proofs; a novel one throws both away.

## 4.4 Transport as a VOM object (your reliability instinct is right)

"Tailscale can be off randomly" — correct, and the fix fits your thesis. Separate the two reliabilities:
- **Uptime/lifecycle** — owning it beats depending on a killable app. Embed **boringtun in your process**, make the tunnel a **first-class VOM object** (owner, handle, capability gate, deterministic reclaim), and be your **own `VpnService`** so the user can mark it **Always-on VPN** (the OS auto-restarts it). This is literally your `IMPROVEMENTS.md` #1 (bind from an activity-independent surface).
- **Correctness/security** — owning the *crypto* makes it *less* reliable. Keep primitives borrowed.

Your **brutally-synchronous core is a genuine asset** for the state machine: single-owner, no hidden continuations, deletes the concurrency-bug class WireGuard fights with locks, and is deterministically testable against the reference. But synchrony does **nothing** for constant-timeness or secret-memory hygiene (managed GC can't zero/mlock) — those stay in libsodium/native. So: own the synchronous state machine; borrow the curve.

**Reachability you inherit if you drop Tailscale:** NAT traversal. Clean self-owned answer — a cheap **VPS with a static IP as your always-on WireGuard peer**; the phone dials outbound (survives CGNAT, held open with keepalive); your laptop reaches the phone through the VPS. Self-hosted, no Google/Tailscale coordination.

## 4.5 Hardware root of trust (the *winning* play from console/DRM)

DRM/anti-tamper splits into a winning play and a losing play:
- **Losing play (don't):** obfuscation, packing, code-virtualization, white-box crypto, tamper-response. Buys *time*, not *security* (Denuvo/Widevine L3 fall), and *actively destroys* your constant-timeness + auditability.
- **Winning play (do):** consoles are secure because of a **hardware root of trust**, not obfuscation. Android equivalents:
  - **StrongBox / Android Keystore** — hardware-backed, **non-exportable** keys (your S23 has StrongBox via Knox). The real "wobble groove": bound to *this* silicon, can't be cloned.
  - **Key Attestation** — cert chain proving a key is hardware-bound/non-exportable.
  - **Play Integrity** — "genuine, unmodified hardware/app" (console attestation) — but Google-mediated/online, a dependency that conflicts with offline-first. Optional, for the remote path only.

**Wiring:** tunnel static key → StrongBox, optionally biometric-gated; SPAKE enrollment → bound to a key-attested hardware key; broker → require attestation before admitting a peer. Degrade gracefully: **StrongBox → TEE Keystore → software**, each tier mapped to a capability ceiling.

## 4.6 Anti-clone details (the "wobble groove" thread)

- **Rotation ≠ clone-resistance.** Hardware non-export stops *cloning*; rotation stops *replay* / gives forward secrecy. Want both, for different reasons.
- **Compile-time secrets fail as a groove** — extractable from the APK (Kerckhoffs), per-*version* not per-*install*, and copied along with the build. Per-build *diversity* is good for **anti-exploit** (deny the universal exploit — do it in **CI**, entropy not secrets), but the identity groove is a **per-install, runtime-generated, hardware-bound key**.
- **"Matching set"** = mutually-enrolled *hardware* identities (via SPAKE), not a shared build constant.
- **Install-time as salt, not seed.** `firstInstallTime` is stable across recompilation (true) but **readable and low-entropy** — deriving a key *from* it is the "seed crypto with the clock" bug. Correct use: a non-secret **salt/context label** in a KDF whose key is the hardware secret:
  `derived = HKDF(key = hardware_keystore_key, salt = install_time || build_number || install_id)`
  Per-install + per-build diversity and binding, with all secrecy/entropy/clone-resistance from the keystore. (TOTP proves the model: time is the *public* counter, the seed is the *secret*.)
- **On-device self-compilation** is a great *sovereign/power-user* feature but a poor *mass-market security/distribution* mechanism: Play restricts dynamic code execution, it's heavy/fragile across fragmentation, and on unrooted devices APK-integrity self-checks are largely redundant with what the OS already enforces (sandbox + signature). Do fleet diversity in CI; keep self-build as the sovereign feature.

---

# Part 5 — The Secure Folder (user-opt-in encrypted vault)

## 5.1 The reframe that keeps it off the malware radar

| Flagged (malware pattern) | Fine (accepted category) |
|---|---|
| **The app** ships encrypted **code** and decrypts-and-executes at startup | **The user** encrypts **their own content** and decrypts on demand |
| Hidden payload, dynamic code loading | A vault — Secure Folder / password manager / encrypted notes |
| Automatic, invisible | Explicit, user-initiated, opt-in, default-off |

Encrypting **user data with user keys** is an accepted Play category (a *positive* on the Data Safety form). The flag comes from an app hiding *its own* executable code — so your app's code ships **plaintext**, and the vault holds only what the user chose to put there. (Running user scripts is a separate policy question that exists with or without encryption — Termux/Pydroid/Tasker show it's allowed for scripting tools; encryption doesn't worsen it.)

## 5.2 Implementation — envelope encryption rooted in the Android Keystore

1. **KEK in hardware** — generate a key in `"AndroidKeyStore"` you never export: AES-GCM, `setUserAuthenticationRequired(true)`, `setUserAuthenticationParameters(0, BIOMETRIC_STRONG | DEVICE_CREDENTIAL)`, `setIsStrongBoxBacked(true)` (fall back to TEE on `StrongBoxUnavailableException`).
2. **DEK per vault** — random AES-256; encrypt the user's files with it (AES-GCM, unique IV per file).
3. **Wrap the DEK with the KEK**; store the wrapped blob next to the ciphertext in app-private storage. Plaintext DEK lives only in RAM.
4. **Open = authenticate** — `BiometricPrompt` with a `CryptoObject(cipher)` so decryption is *cryptographically* bound to a successful auth, not a UI checkbox. On success: unwrap DEK → decrypt **into memory** → use → discard.

On .NET-for-Android: use `SecureStorage` (MAUI/Essentials) for the *wrapped DEK*, roll AES-GCM file encryption for bulk content. (Jetpack `EncryptedFile` works but is Tink-based and in maintenance — prefer the manual keystore path for longevity.)

## 5.3 Fit to your stack
The vault is a **Cm capability** (`\Capability\SecureFolder`, default-off); the encrypted region is a **VOM object with an owner**; the **"decrypt" verb is capability-gated on user-auth** (biometric `CryptoObject`). ObpHost serves decrypted bytes **from RAM only**, never back to disk.

## 5.4 Rules that keep it unflagged & safe
- **Never auto-decrypt-and-run** — user opens → authenticates → runs. The instant it self-decrypts and executes at startup, you're back to the malware pattern.
- **Disclose it** in Data Safety as encryption-at-rest of user data (helps you).
- **Frame as user privacy**, not evasion.
- **Default OFF** — opt-in is both the security and the review story.

## 5.5 Honest gotchas
- **Key invalidation** — a key with `setUserAuthenticationRequired` (+ `setInvalidatedByBiometricEnrollment(true)`) is destroyed when the user changes/adds a biometric or removes the lock screen. Decide a recovery story: accept "vault gone on credential change" (like Secure Folder), or keep a passphrase-derived backup wrap (Argon2id → backup KEK).
- **StrongBox isn't universal** — catch `StrongBoxUnavailableException`, fall back to TEE; note the tier.
- **GCM IVs must be unique** per encryption — let the cipher generate, store alongside, never reuse.

---

# Appendix — What an offline local coder agent structurally misses

These need an internet/CVE feed or whole-ecosystem reasoning, which is why they're worth prioritizing — your local agents can't surface them:

1. **GAP-7 / GAP-8** — preview-package risk and the absence of CVE/Dependabot scanning (no advisory feed = no awareness).
2. **GAP-5 / GAP-6** — logcat command disclosure and the query-string-token caveat are OWASP/Android *best-practice* knowledge, not local-pattern bugs.
3. **GAP-10 / constant-time crypto** — "BigInteger isn't constant-time" is cryptographic domain knowledge a code-shaped check won't flag.
4. **The architecture calls** — Binder + signature permission, Noise-pattern selection, hardware root of trust, the install-time-as-salt distinction — all require cross-file/ecosystem reasoning, not single-file scans.

---

*End of consolidated review. Part 1 is also committed as `SECURITY-FINDINGS.md` (PR #1); the checklist is issue #2. Nothing in Parts 2–5 has been implemented — they are design notes for build-capable sessions.*
