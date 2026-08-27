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
dotnet test                                    # full suite (152 tests)
dotnet test --filter StopPolicyTests           # one test class
dotnet test --filter "FullyQualifiedName~Time" # one namespace
dotnet test tests/Wolfstare.Service.Tests      # one project

dotnet run --project src/Wolfstare.Service     # API on http://127.0.0.1:8437
```

Set `DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1` to keep output readable.

Pin new packages to the **9.x** line. `dotnet add package` defaults to 10.x, which does not
restore against `net9.0`.

To drive the running API, read the bearer token the service writes on each start. Note that
`curl` in the Bash tool is intercepted by a hook here — use PowerShell:

```powershell
$t = Get-Content "$env:ProgramData\Wolfstare\api.token" -Raw
Invoke-RestMethod http://127.0.0.1:8437/api/status -Headers @{Authorization="Bearer $t"}
```

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

### `Wolfstare.Core` must stay dependency-free

Core targets `net9.0`, **not** `net9.0-windows`, and has zero `PackageReference` entries.
This is load-bearing, not stylistic: the application otherwise needs SYSTEM privileges, a
registered service, and a hijacked DNS resolver before you can observe any behaviour at all.
Keeping the decision-making logic in a pure project means it runs in millisecond unit tests.

If you need a Windows API in Core, you have put the logic in the wrong project. Define an
interface in Core and implement it in `Wolfstare.Enforcement`.

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
