# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Wolfstare is a bypass-resistant website and application blocker for Windows, modelled on Cold
Turkey Blocker. A Windows service running as `LocalSystem` owns all enforcement and all
authority; the browser UI and CLI are thin clients over a local HTTP API.

Read `docs/superpowers/specs/2026-08-27-wolfstare-design.md` before making architectural
decisions. It is the authority on scope, threat model, and the reasoning behind the
mechanisms. Phase plans live in `docs/superpowers/plans/`.

## Commands

```bash
dotnet build                                   # build the solution
dotnet test                                    # full suite (326 tests)
cd web && npm run build                        # build the UI into the service's wwwroot
dotnet test --filter StopPolicyTests           # one test class
dotnet test --filter "FullyQualifiedName~Time" # one namespace
dotnet test tests/Wolfstare.Enforcement.Tests  # one project

dotnet run --project src/Wolfstare.Service     # API on http://127.0.0.1:8437
```

Enforcement runs on privileged ports (53/80/443) by default. To exercise it without admin,
override the ports and keep `ModifySystem` off (the default):

```
dotnet run --project src/Wolfstare.Service -- \
  --Wolfstare:Enforcement:DnsPort=15353 --Wolfstare:Enforcement:ProxyPort=18080 \
  --Wolfstare:Enforcement:TransparentHttpPort=18081 --Wolfstare:Enforcement:TransparentTlsPort=14443
```

`ModifySystem=false` (default) means the sinkhole and proxy run but the machine's DNS, proxy,
firewall, and browser policy are never touched — a run can never strand your networking. Only
turn it on deliberately, elevated, following `docs/manual-e2e-phase3.md`.

Set `DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1` to keep output readable.

Pin new packages to the **9.x** line. `dotnet add package` defaults to 10.x, which does not
restore against `net9.0`.

To drive the running API, read the bearer token the service writes on each start. Note that
`curl` in the Bash tool is intercepted by a hook here — use PowerShell:

```powershell
$t = Get-Content "$env:ProgramData\Wolfstare\api.token" -Raw
Invoke-RestMethod http://127.0.0.1:8437/api/status -Headers @{Authorization="Bearer $t"}
```

`api.token` is ACL'd to SYSTEM + Administrators, so an elevated shell can read it but a
standard-user one cannot. The same token is injected into the served `index.html` as a
`<meta name="wolfstare-token">` — scrape that when you can't read the file.

Elevated one-off operations (reset, reinstall, registry surgery) go through
`Start-Process -Verb RunAs` with a `-File` script that writes a log file. Always confirm the
UAC prompt was actually approved by checking the log file exists — a dismissed prompt fails
silently, and the script simply never ran.

For a throwaway instance, override the data directory and port:
`-- --Wolfstare:DataDirectory=C:/temp/wolf --Wolfstare:Port=8437`

## The threat model drives everything

**The adversary is the machine's legitimate administrator** — the user, later, in a weaker
moment. They know the design because they chose it, and they have admin rights that cannot be
taken away. Three consequences shape nearly every decision:

1. **Authority is policy, not transport.** It does not matter that the local API is reachable
   by any process. `StopPolicy.CanStop` is the single gate, and every caller routes through
   it. Do not add a privileged path that skips it — there would then be something worth
   finding.
2. **Every failure path fails closed.** Corrupt state, integrity failure, unparseable config,
   crashed subsystem: blocks stay ON. `Pbkdf2PasswordHasher.Verify` returns false rather than
   throwing on a malformed record, `StopPolicy` returns `Locked` for an unrecognised lock
   type, and `DomainMatcher.Normalize` falls back to lowercase rather than throwing. Preserve
   this when adding code — a blocker that unblocks when confused teaches its user to confuse
   it on purpose.
3. **Limits are documented, not papered over.** The spec states plainly that an administrator
   with physical access always wins. Do not add mechanisms whose failure mode is an
   unbootable machine.

## Architecture

### Project layout and the `net9.0` / `net9.0-windows` split

- `Wolfstare.Core` (`net9.0`, **zero package references**) — all decision-making logic.
- `Wolfstare.Contracts` (`net9.0`) — API DTOs.
- `Wolfstare.Enforcement` (`net9.0-windows`) — DNS sinkhole, proxy, and the Windows
  `ISystemSetting` adapters. Machine-configuration adapters live under `Machine/` (not
  `System/` — a `System` namespace shadows the global one).
- `Wolfstare.Service` (`net9.0-windows`) — host, API, SQLite, the `WebsiteEnforcer`.

A `net9.0` project cannot reference a `net9.0-windows` one, which is *why* Core stays portable:
its logic runs in millisecond unit tests with no admin rights, no service, no hijacked
resolver. If you reach for a Windows API in Core, the logic is in the wrong project — define an
interface in Core and implement it in `Wolfstare.Enforcement`. `JournalledMutator` is the
pattern to follow: pure orchestration in Core, the `ISystemSetting` adapters it drives in
Enforcement. Pin new packages to the **9.x** line.

### Enforcement reads a lock-free cache, never the database

The DNS sinkhole evaluates rules on every lookup, so it reads `RuleSetCache` — a single
`volatile` `RuleSet` reference the `WebsiteEnforcer` swaps in. Never make the servers query
SQLite or take a lock on the hot path. `WebsiteEnforcer.RefreshOnceAsync` is the one place that
copies session state into the cache and engages or disengages machine settings; it is the
testable seam the tests drive directly instead of waiting on the timer.

Machine changes are journalled and reversible (`ISystemMutator` / `JournalledMutator`): the
original is recorded **before** the write, so a crash between the two restores harmlessly.
`SystemSettingResolver` reconstructs any setting from its journal key, so restore works after a
crash with no memory of having applied it — when you add a new `ISystemSetting` type, teach the
resolver its key prefix or its keys will never restore.

### Website and app enforcement share one journal but engage independently

Website takeover (DNS/proxy/firewall/DoH) engages only when a session has a **domain** rule;
IFEO redirection engages only on an **image-name** rule. So the coordinator tracks two
engagement bits, and restore is **key-scoped** (`RestoreMatchingAsync`): website restore touches
everything but `ifeo:`, app restore touches only `ifeo:`. A blunt `RestoreAllAsync` when one
half disengages would rip out the other's live settings — don't reach for it outside startup
crash-recovery.

### App enforcement: IFEO catches by name, ETW catches renames

`ImageNameMatcher` rules become IFEO `Debugger` redirections to the block stub — keyed on the
filename, so the key can exist before the app is installed (the not-yet-installed requirement).
Renaming the exe defeats IFEO, so the `ProcessWatcher` (ETW, `ProcessStartDecision`) is the
backstop: it matches on publisher/description, which a rename doesn't change. The
critical-process guard is enforced in **three** places on purpose — `RuleValidator` at creation,
the IFEO setting's constructor, and `ProcessStartDecision` at the kill boundary — because an
IFEO key or a kill on `explorer.exe`/`lsass.exe` can make the machine unbootable. Never remove
one of those checks on the grounds that another covers it.

Authenticode reading (`ProcessInspector`) sees only **embedded** signatures, not catalog ones.
That's correct for the target: third-party apps embed their signatures; catalog-signed OS
binaries are Microsoft-published and already barred from blocking.

**App enforcement reconciles every tick, not on an engage edge.** `WindowsAppEnforcement.ApplyAsync`
makes the IFEO redirects match the active rules exactly: it writes missing ones (self-heals a
deleted key) and removes any redirect Wolfstare owns that no active block calls for. It finds its
own redirects by their Debugger value pointing at the stub (`RedirectedTo`), not a journal or
flag, so orphans self-clear within a refresh interval across restarts and crashes. Edge-triggering
this — the old model — is what stranded an app as blocked with no session to unlock; don't
reintroduce it. `WebsiteEnforcer.RefreshOnceAsync` therefore calls `_app.ApplyAsync` on every
tick unconditionally, while the website half stays edge-triggered via `_websiteEngaged`.

### Service, integrity, and the UI

The service hosts under the SCM via `UseWindowsService()` and runs as a console app otherwise,
so dev needs no install. `install`/`uninstall` are one-shot verbs handled before the host is
built (`ServiceInstaller`); install sets always-restart failure actions. Tamper logic that is
pure is tested — `ServiceSddl` (deny/allow the stop right) and `UninstallGuard` (refuse
uninstall while locked); nothing tries to block an admin's `sc delete` or safe mode, which is
an accepted limit, not a bug.

`SessionIntegrity` (Core, tested) HMACs the fields of an active session that must not be forged
— including elapsed, since forging it could force a timed lock to look expired. The key is
DPAPI machine-scoped (`IntegrityKeyProvider`, service layer). On a mismatch the session is
**kept** (dropping it would unblock) and health degraded; the stronger "refuse unlock on
tamper" is deliberately not built, because a regenerated key would false-positive and strand
the user.

The UI is a Vite + React + TS SPA in `web/`, built into the service's `wwwroot` (gitignored;
`install.ps1` builds it before publish). The service injects the API token into the served
`index.html` — safe because the token is anti-CSRF, not anti-user (a cross-origin page can't
read the DOM and fails the Origin check). Keep the `RuleDto`/`StatusDto` shapes in
`web/src/api/types.ts` in step with `Wolfstare.Contracts` by hand.

### Two invariants worth understanding before editing

**Clock tampering** (`Time/ElapsedCalculator.cs`). A timed lock defeated by `Set-Date` is
worthless. Credit per tick is capped at the *monotonic* delta, which the user cannot set — so
the most a session can accrue is time the machine has genuinely been running. A forward clock
jump inflates only the wall delta, and the minimum discards it. A backward jump is handled by
trusting monotonic outright rather than clamping to zero, because clamping forfeited honest
time and penalised ordinary NTP corrections.

The invariant to preserve: **`advance <= monotonicDelta`, always.** `ResumeAfterRestart` is
the deliberate exception — monotonic resets at boot, so wall time is the only evidence left,
and crediting a genuine overnight shutdown is correct. That is the accepted residual hole in
spec §5.3.

**Timed locks have no early exit** (`Sessions/StopPolicy.cs`). `TimedLock` is an empty record
and `StopPolicy` has no password branch for it. The absence is the feature: adding an escape
hatch would have to show up in a diff rather than hiding in a config flag.

**The domain supports four lock types; the API only lets you create one.** `StopPolicy` and
`SessionLock` still cover `NoLock`, `TimedLock`, `PasswordLock`, and `RandomTextLock` (which is
why the clock-tamper and integrity machinery above still matters). But the create endpoint
refuses anything but `"randomtext"` with a 400 — the user chose random-text as the *only* lock.
The challenge is a run of whole words (`RandomText.Generate`, grade-12 vocabulary), 100–5000
chars (`RandomTextLockMin/MaxLength`, packed up to the target so 3888 requested yields ~3885),
and it is served **only** as a PNG (`/blocklists/{id}/unlock-image`) — never as text. `/api/status`
returns `UnlockTextLength`, not the string. The PNG-only path is deliberate: text in the API
response would be trivially scriptable, and even the image is word-wrapped at word boundaries so
a screenshot-to-OCR round-trip is the least-friction bypass, not a one-liner. Unlocking is a
case-sensitive exact retype (`StringComparison.Ordinal`), no time gate.

### `SessionManager` is the only mutator

Nothing else changes session state, and nothing else decides whether a session may stop — it
delegates to `StopPolicy` rather than re-deriving lock semantics. Time accrues in exactly two
methods (`TickAsync`, `ResumeAsync`), so no caller can observe a different elapsed value by
computing its own.

The API extends this: **anything that would weaken an active block answers 423**, not just
`stop`. Editing or deleting a block list under a lock is refused for the same reason stopping
it is — otherwise you could empty a list of its rules and leave the lock technically intact
but meaningless. Password-locked sessions are included; the caller can `stop` with the
password first, then edit.

When adding an endpoint, ask what it lets a caller do to an active locked session. If the
answer is anything at all, it needs the same guard.

### Rules match identity, never location

`AppRule` matches on image name, Authenticode publisher, or file description — never on
install path. That is what makes a rule for a **not-yet-installed** application work: the
IFEO registry key can exist years before the executable does. `ImageNameMatcher` therefore
strips any path it is handed and normalises to a lowercase `.exe` name.

`PublisherMatcher` is the backstop for a renamed executable, since renaming a file does not
change who signed it.

### The critical-process guard is in Core on purpose

`Rules/CriticalProcesses.cs` refuses to block `explorer.exe`, `lsass.exe`, Task Manager, and
friends. It lives in the pure domain rather than the registry adapter so it is unit-tested and
rejects a rule at *creation* time. An IFEO key on `explorer.exe` leaves no desktop shell, and
because the uninstaller refuses to run during an active lock, recovery would mean safe mode.

Administration tools are protected for the same reason: the user must retain the ability to
repair their own machine.

### Domain matching semantics

- `*` matches every host.
- `reddit.com` matches the apex **and** every subdomain — what a user expects from a bare domain.
- `*.reddit.com` matches subdomains only, not the apex.
- Label-boundary checks stop `reddit.com` matching `notreddit.com`.
- Allowlist is evaluated first, so "block everything except these" is a single `DomainRule("*")`
  plus an allowlist rather than a special case.

## Conventions

- Domain types are `record` / `sealed record`. Closed hierarchies (`BlockRule`, `AppMatcher`,
  `SessionLock`) are matched with switch expressions that end in a fail-closed default.
- `SessionLock`, not `Lock` — .NET 9 introduced `System.Threading.Lock` and the collision is
  otherwise unavoidable.
- `TreatWarningsAsErrors` is on solution-wide via `Directory.Build.props`.
- Comments explain *why*, particularly where a naive reading would suggest a simpler
  implementation that has a security consequence.
- Validation errors are written to be shown to the user verbatim.

## Working practice

This project is being built spec-first with TDD: red, green, commit, one behaviour at a time.
Tests for the adversarial cases (clock tampering, corrupt records, renamed executables) are
the point of the exercise, not overhead — the backward-jump flaw in `ElapsedCalculator` was
found by a test, not by review.
