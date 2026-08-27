# Wolfstare — Design Specification

**Date:** 2026-08-27
**Status:** Approved for planning
**Target:** Windows 11 Home (build 26200), x64

---

## 1. Overview

Wolfstare is a local, bypass-resistant website and application blocker in the spirit of
Cold Turkey Blocker. A Windows service running as `LocalSystem` owns all enforcement and
all authority; a browser-based UI and a CLI are thin clients over a documented local HTTP API.

### 1.1 Goals

- Block websites by domain, including wildcard subdomains and whole-internet-except-allowlist.
- Block applications by identity, including applications **not yet installed** at the time
  the rule is written.
- Timed locks that cannot be ended early by any means the application itself exposes.
- Password-protected unlocking for non-timed blocks.
- A connectable HTTP API as the product boundary, so any client (SPA, CLI, script, future
  mobile app) is a first-class consumer.
- Survive reboot, process termination, and casual tampering.

### 1.2 Non-goals

Wolfstare raises the cost of a bypass above the cost of the impulse. It is not DRM and does
not attempt to defeat a determined administrator. Explicitly out of scope:

- Defeating safe-mode boots, offline registry edits, or booting from external media.
- Protected-process (PPL) abuse or any boot-path tampering.
- Blocking Task Manager or otherwise degrading the user's ability to administer their machine.
- Multi-user or networked/managed deployment. Single interactive user, single machine.

---

## 2. Threat model

The adversary is the **legitimate administrator of the machine** — that is, the user, later,
in a weaker moment. This is unusual and shapes everything:

- The adversary knows the design, because they chose it.
- The adversary has admin rights and cannot be denied them.
- Therefore no secret is truly secret and no boundary is truly hard.

What follows from that:

1. **Authority is policy, not transport.** It does not matter that the API is reachable by
   any local process. The stop operation on a locked session returns `423 Locked` regardless
   of caller identity. Securing the channel is about preventing *remote* abuse (DNS rebinding
   from a web page), not about preventing the user.
2. **Every failure path fails closed.** Corrupt state, integrity failure, unparseable config,
   crashed subsystem — blocks stay ON. A blocker that unblocks when confused teaches its user
   to confuse it deliberately.
3. **Integrity checks are speed bumps, honestly labelled.** The HMAC key is DPAPI-protected
   at machine scope; an administrator can run as `SYSTEM` and unprotect it. The check defeats
   a text editor, not a determined attacker. This is stated rather than papered over.

---

## 3. Architecture

### 3.1 Processes

```
+----------------------+
|  Web UI (React/TS)   |  browser tab; swappable
+----------+-----------+
           |  HTTP + WebSocket - 127.0.0.1 only, bearer token, Origin-checked
           v
+----------------------------------------------+
|  Wolfstare Service  (LocalSystem, autostart)  |
|  +----------------+  +---------------------+  |
|  | ASP.NET Core   |--| Enforcement         |  |
|  | Minimal API    |  | BackgroundServices  |  |
|  +----------------+  +---------------------+  |
+----------------------------------------------+
           |
   DNS sinkhole | proxy | IFEO | ETW watcher | firewall | tamper watch
```

The `Wolfstare.BlockStub` executable is a fourth, transient process: Windows launches it in
place of a blocked application via IFEO.

### 3.2 Projects

| Project | Responsibility | Windows deps |
|---|---|---|
| `Wolfstare.Core` | Domain: rules, matching, sessions, locks, clock. Pure. | **None** |
| `Wolfstare.Contracts` | API DTOs; source for generated TS types. | None |
| `Wolfstare.Enforcement` | Adapters implementing Core's enforcement interfaces. | Yes |
| `Wolfstare.Service` | Service host, API, composition root, persistence. | Yes |
| `Wolfstare.BlockStub` | Minimal exe IFEO redirects blocked apps to. | Yes (MessageBoxW) |
| `Wolfstare.Cli` | Command-line client over the HTTP API. | None |

**`Wolfstare.Core` must not reference any Windows-specific assembly.** This is enforced by
review and by the absence of such references in its `.csproj`. All enforcement is expressed
through interfaces Core defines:

```csharp
interface IWebsiteBlocker { Task ApplyAsync(RuleSet rules, CancellationToken ct); }
interface IAppBlocker     { Task ApplyAsync(RuleSet rules, CancellationToken ct); }
interface ISystemMutator  { /* journalled, reversible system changes */ }
interface IClock          { DateTimeOffset UtcNow { get; } long MonotonicMs { get; } }
```

Rationale: running this application normally requires SYSTEM privileges, a registered
service, and a hijacked DNS resolver. Confining the interesting logic to a dependency-free
project makes the brain testable in millisecond unit tests with no privileges, and shrinks
the privileged surface to small, boring adapters.

---

## 4. Domain model

### 4.1 Rules

`BlockRule` is a closed hierarchy:

- **`DomainRule { string Pattern }`** — `reddit.com`, `*.reddit.com`, or `*`.
  Matching is case-insensitive on the normalised (punycode, trailing-dot-stripped) name.
  `*` matches every domain; combined with an allowlist this expresses
  whole-internet-except-these.
- **`AppRule { AppMatcher Matcher }`** where `AppMatcher` is one of:
  - `ImageName(string)` — e.g. `4kvideodownloaderplus.exe`. Filename only, never a path.
  - `Publisher(string)` — Authenticode certificate subject CN, substring match.
  - `FileDescription(string)` — Win32 version-info `FileDescription`, substring match.
- **`PathRule { string UrlPattern }`** — modelled now, **rejected at validation in v1**
  with a clear "requires HTTPS inspection" error. Present so the domain model does not
  change when MITM lands.

Matching on image name / publisher / description rather than install path is precisely what
makes the not-yet-installed case work: the rule describes an identity, not a location.

### 4.2 Lists and sessions

- **`BlockList { Id, Name, IReadOnlyList<BlockRule> Rules, IReadOnlyList<BlockRule> Allowlist }`**
  Allowlist entries take precedence over block rules. Evaluation order: allowlist first;
  if any allowlist rule matches, permit; otherwise if any block rule matches, deny; else permit.
- **`BlockSession { Id, BlockListId, Lock, StartedAt, ElapsedSeconds, DurationSeconds? }`**
  Zero or one active session per block list. A null duration means indefinite.

### 4.3 Locks

`Lock` is a closed hierarchy of three:

- **`NoLock`** — session can be stopped freely.
- **`PasswordLock { Hash, Salt, Iterations }`** — stopping requires the password.
  PBKDF2-HMAC-SHA256, 600,000 iterations, 128-bit random salt, 256-bit derived key.
  Verification is constant-time.
- **`TimedLock { DurationSeconds }`** — **there is no code path that ends it early.**
  Not "requires confirmation", not "requires the password". The API has no operation that
  terminates a timed session before expiry. This is the feature.

Password verification failures are rate-limited to one attempt per 2 seconds to keep the
API from becoming an offline-cracking oracle for a weak password.

---

## 5. Lock expiry and clock tampering

A timed lock defeated by `Set-Date` is worthless. Wall-clock comparison alone loses to a
clock change; monotonic counting alone loses across reboot.

### 5.1 Algorithm

State persisted per active session: `startedAtUtc`, `elapsedSeconds`, `checkpointWallUtc`,
`checkpointMonotonicMs`. A checkpoint is written every 30 seconds.

On each tick (1 Hz) and on each checkpoint:

```
wallDelta      = now.Utc       - checkpointWallUtc       (seconds, may be negative)
monotonicDelta = now.Monotonic - checkpointMonotonicMs   (seconds, never negative)
advance        = clamp(min(wallDelta, monotonicDelta), 0, monotonicDelta)
elapsedSeconds = elapsedSeconds + advance
```

- Clock moved **forward**: `wallDelta` is large, `monotonicDelta` is small, `min` picks
  monotonic. No credit gained.
- Clock moved **backward**: `wallDelta` is negative, `min` is negative, clamped to 0.
  No credit lost, and the next tick resumes from the new checkpoint normally.
- Normal operation: the two deltas agree within drift; either is correct.

### 5.2 Across reboot

Monotonic resets to zero at boot, so a resumed session cannot use it. On service start,
if a session is active:

```
gap            = now.Utc - checkpointWallUtc
elapsedSeconds = elapsedSeconds + max(gap, 0)
```

A genuine 8-hour shutdown should count against the block, so crediting wall time here is the
honest behaviour, not a concession.

### 5.3 Accepted residual hole

Reboot, set clock forward, boot. The fabricated gap is credited. Closing this requires an
external time source, i.e. a network dependency inside an application whose job is severing
network access, with a fail-closed policy that would then have to block on an unreachable
time server. The tradeoff is not worth it. **Documented, accepted, not fixed.**

---

## 6. Persistence and integrity

- Single SQLite database at `%ProgramData%\Wolfstare\wolfstare.db`
  (`Microsoft.Data.Sqlite`, WAL mode).
- Directory ACL: full control to `SYSTEM` and `Administrators`; no access for `Users`.
  The API token file is the sole exception (§10.2).
- Active lock state is additionally covered by HMAC-SHA256 under a 256-bit random key stored
  DPAPI-protected at `LocalMachine` scope.
- Logs to `%ProgramData%\Wolfstare\logs\` via a Serilog rolling file sink.

**Fail-closed rules.** On HMAC mismatch, schema corruption, or unreadable state: retain the
last known enforcement configuration, mark service health `Degraded`, surface the condition
on `GET /api/status`, and refuse all unlock operations. Never unblock as a recovery action.

---

## 7. Website enforcement

Three cooperating subsystems, with a degraded fallback.

### 7.1 DNS sinkhole (primary)

- UDP and TCP listener on `127.0.0.1:53` and `[::1]:53`.
- On query: evaluate the active rule set. Blocked answers `A 127.0.0.1` / `AAAA ::1`
  (deliberately **not** `NXDOMAIN`, so the proxy can serve a block page). Permitted queries
  are forwarded to captured upstream resolvers and the response relayed.
- Upstream resolvers are captured from the active interfaces **before** takeover and stored.
  If none can be determined, fall back to `1.1.1.1` and `8.8.8.8`.
- Interface DNS is set to `127.0.0.1` via WMI (`Win32_NetworkAdapterConfiguration`), through
  `ISystemMutator` so prior values are journalled and restorable.
- Wildcard subdomain matching is native here. This is the main reason DNS beats a hosts file.

### 7.2 DoH sealing

Without this the DNS tier is theatre — Chrome and Firefox route around it by default.
Three layers:

1. **Policy registry keys**: Chrome and Edge `DnsOverHttpsMode = "off"`, Firefox
   `network.trr.mode = 5` via the enterprise policy mechanism.
2. **Known DoH endpoint blocklist**: a maintained list of DoH hostnames sinkholed
   unconditionally while any session is active.
3. **Windows Firewall outbound rules**: deny outbound UDP/TCP 53 and TCP 853 for every
   process except the Wolfstare service.

Layer 3 uses the **Windows Firewall COM API (`INetFwPolicy2`)**, not raw WFP. This is a
deliberate simplification from the original sketch: port-based egress denial is expressible
as ordinary firewall rules with a fraction of the P/Invoke surface, and the resulting rules
are inspectable with `netsh` when debugging. Raw WFP remains available if a future rule needs
per-flow granularity the firewall cannot express.

### 7.3 Proxy

- Listeners on `127.0.0.1:8080` (registered as the system proxy) plus `127.0.0.1:80` and
  `127.0.0.1:443` to catch connections the sinkhole redirected.
- The decision is made from the `CONNECT` target hostname or the plain-HTTP `Host` header.
  **No decryption in v1.**
- A permitted `CONNECT` becomes an opaque TCP tunnel. A blocked one yields a block page on
  HTTP; on port 443 the browser receives a TLS error, which is the acknowledged v1 cost of
  not installing a CA.
- System proxy registration writes to the interactive user's hive
  (`HKU\<sid>\Software\Microsoft\Windows\CurrentVersion\Internet Settings`) since the service
  runs as `LocalSystem`, followed by an `InternetSetOption` refresh broadcast.

**MITM seam.** TLS handling is written against `ICertificateProvider`, shipping as
`NullCertificateProvider`. Enabling HTTPS inspection later is a registration change plus a
CA generator and trust-store installer — not a rewrite. `PathRule` (§4.1) is already modelled.

### 7.4 Degraded fallback

If `:53` cannot be bound (Internet Connection Sharing, Docker, another resolver), the service
does not fail open. It logs, marks health `Degraded`, and applies the active domain rules via
the **hosts file** instead — weaker (no wildcards, DoH bypasses it) but non-zero.

### 7.5 Reversibility

Every system change — interface DNS, proxy registration, policy keys, firewall rules, hosts
file — goes through `ISystemMutator`, which journals the prior value to the database before
writing. On service start, any incomplete mutation is rolled back. Without this, a crash
mid-change eventually leaves the machine with a dead DNS configuration and no obvious cause.

---

## 8. Application enforcement

### 8.1 IFEO redirection (primary)

For each `ImageName` matcher, write:

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<name>
    Debugger = "C:\Program Files\Wolfstare\Wolfstare.BlockStub.exe"
```

Windows then launches the stub **instead of** the target, at launch time, matched purely by
filename. The key may exist long before the application does — which is exactly the
not-yet-installed requirement, and why Windows Home's lack of AppLocker costs us nothing.

- Written to both the native view and `Wow6432Node` so 32-bit targets are covered.
- The stub receives the original command line as arguments; it displays a block notice via
  `MessageBoxW` (P/Invoke, no UI framework, no dependencies) and exits non-zero.
- Keys are removed when the rule is no longer active.

### 8.2 ETW process watcher (backstop)

IFEO matches on filename, so renaming the executable defeats it. A watcher subscribes to
`Microsoft-Windows-Kernel-Process` start events via the `TraceEvent` library; on each
process start it resolves the image path, reads the Authenticode publisher and version-info
`FileDescription`, evaluates the rule set, and calls `TerminateProcess` on a match. Renaming
a file does not change who signed it.

Fallback if ETW cannot be initialised: WMI `Win32_ProcessStartTrace`.

A short race window exists in which a blocked process runs before termination. This is
inherent to a user-mode design and is accepted; IFEO covers the common case pre-launch.

### 8.3 Critical-process guard

A hard-coded denylist of image names that **may never** be IFEO-redirected or terminated:

```
explorer.exe, svchost.exe, winlogon.exe, csrss.exe, services.exe, lsass.exe,
smss.exe, wininit.exe, dwm.exe, systemsettings.exe, taskmgr.exe, regedit.exe,
cmd.exe, powershell.exe, pwsh.exe, conhost.exe, dllhost.exe, runtimebroker.exe,
plus every Wolfstare executable.
```

IFEO on `explorer.exe` leaves no desktop shell, and since the uninstaller refuses to run
during an active lock (§9), recovery would require safe mode. **This guard is implemented as
a validation rule in `Wolfstare.Core`, not in the registry adapter**, so it is unit-tested and
rejects the rule at creation time with a clear error rather than at apply time.

---

## 9. Tamper resistance

- Service configured `SERVICE_AUTO_START` with recovery actions set to restart on first,
  second, and subsequent failures.
- While a lock is active, `SERVICE_STOP` is denied to the interactive user by rewriting the
  service SDDL; the original descriptor is restored on unlock.
- Startup integrity check over lock state (§6); failure is fail-closed.
- The uninstaller refuses to run while any lock is active.

Per §2, an administrator with physical access always wins. Wolfstare does not pretend
otherwise, and does not pursue mechanisms whose failure modes include an unbootable machine.

---

## 10. HTTP API

### 10.1 Surface

```
GET    /api/status                    active sessions, lock state, remaining, health
GET    /api/blocklists                list
POST   /api/blocklists                create
GET    /api/blocklists/{id}           read
PUT    /api/blocklists/{id}           update      -> 423 if locked and active
DELETE /api/blocklists/{id}           delete      -> 423 if locked and active
POST   /api/blocklists/{id}/start     { durationMinutes?, lock: {...} }
POST   /api/blocklists/{id}/stop      -> 200 | 423
POST   /api/unlock                    { blockListId, password } -> 200 | 401 | 423
GET    /api/health                    subsystem health detail
WS     /api/events                    live status changes and block hits
```

`423 Locked` is returned identically whether the caller is the SPA, the CLI, curl, or a
script. A `TimedLock` never yields to `/api/unlock`; it returns `423` with the remaining
duration.

OpenAPI is emitted by `Microsoft.AspNetCore.OpenApi`; TypeScript client types are generated
from that document, so `Wolfstare.Contracts` remains the single source of truth.

### 10.2 Access control

- Bound to `127.0.0.1` only.
- 256-bit random bearer token, regenerated on every service start, written to
  `%ProgramData%\Wolfstare\api.token` with an ACL granting read to the interactive user.
- `Origin` and `Host` headers validated on every request to defeat DNS-rebinding attacks
  from a malicious web page.

The token is an anti-CSRF measure, not an anti-user one. See §2.

---

## 11. Frontend

- Vite + React + TypeScript, served in production from the service's `wwwroot`.
- Development: Vite dev server with `/api` and `/api/events` proxied to the service, giving
  hot reload against a live backend.
- Screens: block list editor (rules and allowlist), start-session dialog (duration and lock
  type), active-status view with countdown, unlock prompt, health and diagnostics.
- Visual design is deliberately minimal; this is a personal tool.

---

## 12. Development workflow

The service supports a `--console` switch that runs the entire stack as an ordinary elevated
console application rather than a registered Windows service. Full debugger attach, no
install/uninstall cycle per iteration. This is the primary development mode.

```
dotnet run --project src/Wolfstare.Service -- --console     # elevated
cd web && npm run dev                                        # Vite, proxies to service
dotnet test                                                  # all tests
```

Service installation for realistic testing is a separate, scripted step.

---

## 13. Testing strategy

| Layer | Approach | Privileges |
|---|---|---|
| `Core` | Unit tests. Rule matching (wildcards, allowlist precedence, punycode), lock semantics, the full clock-tampering matrix from §5, critical-process guard. | None |
| DNS sinkhole | In-process tests on high loopback ports with a fake upstream. | None |
| Proxy | In-process tests on high loopback ports; CONNECT allow and deny, block page. | None |
| `ISystemMutator` | Journal and rollback tests against a fake backing store. | None |
| IFEO, ETW, firewall, DNS takeover | Documented manual E2E checklist. | Admin |

The genuinely privileged adapters get a manual checklist rather than mocked tests, because a
mocked test of a registry write proves only that we called the mock.

---

## 14. Scope

### 14.1 In scope for v1

Website blocking (DNS and proxy, domain-level), application blocking (IFEO and ETW, including
not-yet-installed applications), timed locks, password unlocks, the HTTP API, the React UI,
the CLI, tamper resistance per §9, and reversible system mutation.

### 14.2 Deferred to v2

Schedules (recurring weekly blocks), statistics and time tracking, allowance and
pomodoro-style breaks, "Frozen Turkey" whole-computer lockout, path-level URL rules and the
HTTPS inspection they require, random-text unlock, and multiple simultaneous named sessions
beyond one per list.

---

## 15. Known risks

| Risk | Mitigation |
|---|---|
| Port 53 already bound | Detect at startup, degrade to hosts file, report health (§7.4). |
| Crash mid-mutation leaves broken DNS or proxy | Journalled mutations with startup rollback (§7.5). |
| IFEO on a critical process bricks the desktop | Core-level validation denylist (§8.3). |
| ETW race lets a blocked app run briefly | Accepted; IFEO covers the pre-launch common case. |
| Clock forward across reboot credits time | Accepted and documented (§5.3). |
| HMAC key recoverable by an administrator | Acknowledged as a speed bump, not a boundary (§2). |
| Proxy conflicts with an existing VPN or proxy | Capture and chain to prior proxy settings; document the limitation. |
