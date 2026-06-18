# Security Findings — Subsystem

**Scan date:** 2026-06-18
**Scope:** Full repository at time of scan (working tree + tracked files).
**Type:** First-pass static review — (1) private-information leak scan, (2) security/quality gap analysis.
**Method:** Read-only static analysis. No code was executed; no dynamic/pen testing was performed.

> **Read this first.** This is a *first attempt* at an outside-in review. Subsystem is already
> an unusually security-conscious codebase: a loopback-only `BindGuard` invariant, a per-boot
> capability token, a default-deny zone firewall, an air-gapped renderer, and 22 build-time
> Roslyn analyzers enforcing structural discipline. Most items below are **residual hardening**,
> not open holes. Each finding notes the mitigations already in place so nothing reads as more
> alarming than it is.

---

## Part 1 — Private Information Leak Scan

### Result: CLEAN on secrets. One informational identity note.

| Category | Result |
|---|---|
| API keys / tokens / bearer secrets | **None found** |
| Cloud credentials (AWS `AKIA*`, GCP, Azure) | **None found** |
| Private keys / certs (`.pem`, `.key`, `.p12`, `.pfx`, `id_rsa`) | **None found** |
| `.env` files with secrets | **None found** (and `.env` is git-ignored) |
| Connection strings / DB URLs with passwords | **None found** |
| OAuth / client secrets | **None found** |
| High-entropy secret-shaped strings | **None found** |

**On the "password" strings that exist:** `src/runspace/Adb/AdbPairingClient.cs` and `Spake2.cs`
contain `password`/pair-code handling — these are **ephemeral in-memory values for the SPAKE2 adb
pairing protocol**, not stored credentials. Legitimate, not a leak.

**Git author identity:** commits use the GitHub noreply form
(`109449446+MansfieldPlumbing@users.noreply.github.com`) — correctly anonymized. The only network
URL in `.git/config` is the local dev proxy (`127.0.0.1`). Clean.

### INFO-1 — `mansfieldplumbing` identity in package name (intentional — no action)

The identifier `mansfieldplumbing` appears throughout as the Android application id and namespace:

- `src/runspace/Subsystem.csproj:14` — `<ApplicationId>dev.mansfieldplumbing.subsystem</ApplicationId>`
- `src/runspace/AndroidManifest.xml` — `package="dev.mansfieldplumbing.subsystem"`
- numerous C# attributes/service names, and the GitHub org `MansfieldPlumbing/Subsystem`.

This is the project owner's own brand/org, deliberately public. **Recorded as informational, not a
leak. No change recommended** (renaming an Android `ApplicationId` is a breaking change to app
identity and signing). Listed here only so it is a conscious decision rather than an oversight.

---

## Part 2 — Security & Quality Gaps

Severity reflects *residual* risk after accounting for existing mitigations.
Ordered by the focus areas requested: local attack surface → info disclosure → dependencies → crypto/native.

### A. Local attack surface (the loopback command plane)

#### GAP-1 — JS→PowerShell bridge `apiCommand` is **not** capability-gated *(Severity: Medium; design-review)*

`src/runspace/MainActivity.cs:846-853`

```csharp
[Export("apiCommand")] [JavascriptInterface]
public string ApiCommand(string command) {
    return SubsystemApi.ExecuteCommandAsJson(command).GetAwaiter().GetResult();
}
```

The HTTP command surfaces (`/api/exec`, `/clixml`, `/psrp`, command WebSockets) are all gated by the
per-boot capability token (`Authorized()` in `ProjectionServer.cs:52`). This in-process
`@JavascriptInterface` bridge is **not** — any JavaScript running in the WebView can call
`apiCommand(...)` and reach full PowerShell execution directly, bypassing the token entirely.

**What protects it today:** the renderer is air-gapped — `ShouldOverrideUrlLoading`
(`MainActivity.cs:824-834`) hard-blocks every off-origin navigation, and content is served from
embedded resources, so there is no normal path for foreign JS to load. The bridge is RCE *by design*
for trusted local UI.

**Residual risk:** the bridge is the single point where "air-gap holds" becomes "no further defense."
A future feature that renders untrusted HTML (an LLM-authored card, a fetched applet, a `data:`/`blob:`
document, a stored-XSS sink in the shell) would inherit unauthenticated RCE. Defense-in-depth would
gate this bridge the same way the wire surface is gated, or restrict it to a vetted verb allowlist.
*(Secondary: `.GetAwaiter().GetResult()` blocks the UI thread — same anti-pattern analyzer SS018 flags.)*

#### GAP-2 — `/vom/<handle>` and `vom://` reads are unauthenticated by name *(Severity: Low; by-design, document it)*

`src/runspace/Host/ProjectionServer.cs:273-293` and `MainActivity.cs:802-817`

Texture-region reads resolve purely on the handle **name** ("possession of the name = authority"),
with no capability token. This is a deliberate, weaker authority model than the command routes. Risk
is low (regions are projection data, names are not trivially enumerable), but it is an asymmetry worth
documenting: an attacker who already has WebView/loopback reach can read any region whose name they
can guess or observe. Consider a max-size guard on returned bytes to bound a malicious large-region read.

#### GAP-3 — No per-connection input cap or rate limiting on command WebSockets *(Severity: Low; local-only)*

`src/runspace/Host/SubsystemApi.cs:66-82` (`ProcessApiWebSocket`) and `ProjectionServer.cs` handlers.

Authorized clients can flood the command pool; there is no sliding-window/token-bucket throttle and no
per-message size ceiling beyond fixed read buffers. Attack surface is loopback + token-gated, so impact
is a local self-DoS, but a bounded queue / rate limit would harden the multi-client case. *(Note: the
`/api/config` POST route at `ProjectionServer.cs:836` does cap body size at 256 KB and validates the key
with a regex — a good pattern to mirror on the command routes.)*

#### GAP-4 — Wildcard CORS on the `vom://` resource response *(Severity: Low)*

`src/runspace/MainActivity.cs:811-813` sets `Access-Control-Allow-Origin: *` on intercepted `vom://`
responses. Only reachable in the air-gapped WebView, so practical exposure is minimal, but `*` is
broader than needed — scope it to the actual app origin. Cheap cleanup.

### B. Information disclosure / logging

#### GAP-5 — Full command text + results logged to logcat in **all** builds *(Severity: Medium)*

`src/runspace/Host/SubsystemApi.cs:317,319`

```csharp
Android.Util.Log.Info("SubsystemApi", $"Executing: {command}");
Android.Util.Log.Info("SubsystemApi", $"Finished Executing: {command}, Results: {results.Count}");
```

Every command body is written to logcat at `Info` (not behind `#if DEBUG`). Any command carrying a
secret (a credential argument, a token, PII) lands in the device log, which is readable via `adb
logcat` and by other tooling on a rooted/compromised device. **Fix:** drop command bodies from logs
(log a verb/identifier or a hash), or wrap in `#if DEBUG`, and add redaction for known-sensitive args.
Audit the broader codebase for other `Log.Info/Debug` calls that echo user/command data in release.

#### GAP-6 — Capability token travels as a `?cap=` query-string on WebSocket upgrade *(Severity: Low; acknowledged trade-off)*

`src/runspace/Host/ProjectionServer.cs:49-56`

HTTP routes pass the token in the `X-Subsystem-Cap` header (good); WebSockets fall back to `?cap=`
because browser JS cannot set WS headers (acknowledged in the code comment). Query-string secrets are
an OWASP-flagged pattern — they leak into access logs, proxy logs, and crash dumps more readily than
headers. On pure loopback the exposure is small. If a remote/tunneled path is ever enabled (the code
anticipates Tailscale/SPAKE2), revisit this: prefer a `Sec-WebSocket-Protocol` token or a short-lived
one-time ticket over a query param.

### C. Dependencies / supply chain — *the items your offline agents most likely cannot see*

#### GAP-7 — Pinned to preview / non-LTS packages *(Severity: Medium)*

`src/runspace/Subsystem.csproj:52,55`

- `Microsoft.PowerShell.SDK` **7.7.0-preview.2**
- `Microsoft.Extensions.AI` **9.0.0-preview.9.24556.5**

Preview builds receive fewer security backports, can ship breaking changes, and their CVEs are often
not yet in standard advisory databases — exactly the blind spot an internet-less agent cannot assess.
Track a path to a stable/LTS line (e.g. PowerShell 7.4.x LTS) once feature requirements allow, or at
minimum pin-and-review these on a schedule. *(`net11.0-android` / `.NET 11` is itself pre-release; same
caveat applies to the platform.)*

#### GAP-8 — No automated dependency / CVE scanning *(Severity: Medium; hygiene)*

There is **no `.github/workflows/`**, no Dependabot config, no SBOM, no `dotnet list package
--vulnerable` step. Nothing mechanically catches a newly-disclosed CVE in a (possibly transitive)
dependency. This is the single highest-leverage gap an offline agent will never surface, because it
*requires* the live advisory feed.

**Recommended (report-only — implement when you choose):**
- Add a CI job running `dotnet list package --vulnerable --include-transitive` (fail on High/Critical).
- Enable GitHub Dependabot for NuGet.
- Generate an SBOM (e.g. CycloneDX) on release.

#### GAP-9 — No `SECURITY.md` / disclosure policy *(Severity: Low; hygiene)*

No responsible-disclosure contact or policy exists. For a public repo, add a `SECURITY.md` with a
contact and expected response window.

### D. Cryptography / native safety

#### GAP-10 — Non-constant-time Curve25519 (BigInteger) *(Severity: Low; acknowledged, scoped)*

`src/runspace/Adb/Spake2.cs`

Scalar multiplication uses managed `BigInteger`, which is **not constant-time**; the code comment
already says so and scopes it to one-shot loopback adb pairing. Fine for that use. **Guard rail:** if
this code is ever reused for a networked/remote auth path, it must move to a constant-time
implementation (e.g. a libsodium binding) — leave the warning comment loud so the constraint travels
with the code.

#### GAP-11 — `AllowUnsafeBlocks=true` + FFI marshaling *(Severity: Low–Medium; inherent to the design)*

`src/runspace/Subsystem.csproj:12` (and the Windows head). Required for the libpsl / LiteRT-LM /
DirectPort P/Invoke surfaces. Unsafe + `Marshal` means buffer-length and lifetime mistakes at the FFI
boundary are possible and won't be caught by the managed runtime. **Recommendation:** keep an explicit
inventory of `unsafe` blocks, ensure native handles use `SafeHandle`, and validate every buffer length
crossing the boundary. Worth a dedicated focused pass given the amount of native interop.

#### GAP-12 — `Assembly.Load` of in-process-compiled bytes *(Severity: Low; Windows head only)*

`src/runspace/windows/InProcGate.cs` loads freshly-compiled analyzer bytes via `Assembly.Load(...)` and
instantiates types by reflection. Not shipped in the APK and the bytes are locally produced, so risk is
low today — but it is a code-execution sink if an external code path ever feeds it. Constrain to a
sealed analyzer base type / known assembly if that ever changes.

---

## What an offline local coder agent most likely MISSES

These need internet/CVE knowledge or a whole-repo/ecosystem view — prioritize them precisely because
your local agents structurally cannot:

1. **GAP-7 / GAP-8** — preview-package risk and the absence of CVE/Dependabot scanning. No advisory feed = no awareness.
2. **GAP-5 / GAP-6** — logcat command disclosure and the query-string token caveat are *best-practice* knowledge (OWASP/Android logging guidance), not local-pattern bugs.
3. **GAP-10** — "BigInteger isn't constant-time" is cryptographic domain knowledge a code-shaped check won't flag.
4. **GAP-1** — the bridge-vs-wire authorization *asymmetry* requires reasoning across two files (`MainActivity.cs` bridge vs `ProjectionServer.cs` gate), not a single-file scan.

## Priority summary

| ID | Finding | Severity | Effort |
|----|---------|----------|--------|
| GAP-1 | `apiCommand` JS bridge not capability-gated | Medium | Medium |
| GAP-5 | Command bodies logged to logcat in release | Medium | Low |
| GAP-7 | Preview / non-LTS dependencies | Medium | Low–Med |
| GAP-8 | No CVE / Dependabot / SBOM scanning | Medium | Low |
| GAP-2 | `/vom/` reads authenticated by name only | Low | Low |
| GAP-3 | No WS input cap / rate limit | Low | Low |
| GAP-4 | Wildcard CORS on `vom://` | Low | Low |
| GAP-6 | Cap token in `?cap=` query string | Low | Med |
| GAP-9 | No `SECURITY.md` | Low | Very low |
| GAP-10 | Non-constant-time Curve25519 (scoped) | Low | High |
| GAP-11 | `AllowUnsafeBlocks` + FFI bounds | Low–Med | Med |
| GAP-12 | `Assembly.Load` of compiled bytes (Win head) | Low | Low |
| INFO-1 | `mansfieldplumbing` brand identity | Info | — (intentional) |

*Report only — no code changes were made. Recommended next pass (if desired): GAP-5 (drop command
logging), GAP-8/GAP-9 (add CVE scanning + `SECURITY.md`), then a design discussion on GAP-1.*
