# Wolfstare Phase 5: Service Hosting, Tamper Resistance, and UI

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship the pieces that make Wolfstare a real installed product: run as a Windows Service, resist tampering, close the last enforcement-latency gap, and give it a browser UI.

**Architecture:** The existing `WebApplication` gains Windows Service hosting and a `--console` dev switch. Tamper resistance is partly install-time configuration (auto-restart, stop-denial ACL) and partly runtime logic (integrity check, uninstall-during-lock guard) — the logic lives in Core/Enforcement and is tested; the privileged install steps are scripted and checklisted. The UI is a Vite + React + TypeScript SPA served from the service's `wwwroot`.

**Spec:** `docs/superpowers/specs/2026-08-27-wolfstare-design.md`

## Global Constraints

- `Wolfstare.Core` stays `net9.0`, zero package references.
- Tamper mechanisms must never be able to lock the user out of repairing their machine (spec §2 non-goals); the uninstall guard blocks the uninstaller during a lock but nothing blocks safe mode or `sc delete` by an admin.
- Everything still fails closed.

---

### Task 1: Immediate enforcement refresh on session change

Close the deferred latency gap: blocking should take effect the instant a session starts, not on the next 2 s tick.

**Files:** `src/Wolfstare.Service/Enforcement/IEnforcementRefresh.cs`, modify `WebsiteEnforcer` to implement it and `ApiEndpoints` to call it after start/stop/unlock; test `Enforcement/RefreshOnChangeTests.cs`.

- [ ] `interface IEnforcementRefresh { Task RefreshNowAsync(CancellationToken ct); }` implemented by `WebsiteEnforcer` (delegates to `RefreshOnceAsync`); a `NullEnforcementRefresh` for when enforcement is absent in a test host.
- [ ] Endpoints take `IEnforcementRefresh` and call it after a successful start, stop, or unlock.
- [ ] Test: starting a session then reading the cache shows the rule without waiting; a fake refresh records it was called. Red, implement, green, commit.

### Task 2: Session integrity (HMAC, fail-closed)

Spec §6: active lock state is HMAC-covered so a hand-edit of the database is detected. Detection fails closed — a bad HMAC keeps blocks on.

**Files:** `src/Wolfstare.Core/Sessions/SessionIntegrity.cs`, test `Sessions/SessionIntegrityTests.cs`.

- [ ] `static class SessionIntegrity` with `string Sign(BlockSession, ReadOnlySpan<byte> key)` and `bool Verify(BlockSession, string mac, ReadOnlySpan<byte> key)`, HMAC-SHA256 over the fields that must not be forged (id, block list id, lock kind, duration, started-at), constant-time verify.
- [ ] Test: a signature verifies; tampering with duration/lock/id fails; a different key fails; a garbage mac fails closed (false, no throw). Red, implement, green, commit.
- [ ] Wire into the session repository: store the mac column, verify on load, and on mismatch keep the session active but mark health degraded (a later task can surface it). The DPAPI machine-key provider lives in the service layer and is not unit-tested.

### Task 3: Windows Service hosting and lifecycle CLI

**Files:** modify `Program.cs`; `src/Wolfstare.Service/ServiceControl/` for the SDDL transform and install/uninstall commands; `scripts/install.ps1`, `scripts/uninstall.ps1`; test `ServiceControl/ServiceSddlTests.cs`, `ServiceControl/UninstallGuardTests.cs`.

- [ ] `Program.cs`: `AddWindowsService()`, and a `--console` switch (or absence of the service context) that runs interactively for dev. Args `install` / `uninstall` / `run` handled before building the web host.
- [ ] Pure + tested: `ServiceSddl.DenyStopForInteractiveUser(existing)` transforms a service SDDL to remove `WP`/stop rights from the interactive user, and `AllowStop(existing)` restores it — string transform, unit-tested; applying it via `sc sdset` is scripted.
- [ ] Pure + tested: `UninstallGuard.CanUninstall(activeSessions)` → false when any session is locked, with the reason. The uninstaller calls it.
- [ ] `scripts/install.ps1`: install the service (`sc create` / `New-Service`), set failure actions to always-restart (`sc failure`), set `ModifySystem=true`, copy the stub next to the service exe. `uninstall.ps1`: refuse if a lock is active (query the API), else `sc delete`.
- [ ] Red, implement, green, commit.

### Task 4: React UI

**Files:** `web/` (Vite + React + TS); serve from the service via static files with SPA fallback; `web/src/api/` typed client.

- [ ] Scaffold Vite React-TS in `web/`. Dev server proxies `/api` to `http://127.0.0.1:8437`.
- [ ] Screens: block-list list + editor (rules/allowlist), start-session dialog (duration, lock kind + password), active-status view with live countdown, unlock prompt. Reads the bearer token flow: in dev, a configured token; in production, the page is served same-origin by the service which injects it.
- [ ] The service serves `web/dist` from `wwwroot` with a fallback to `index.html`; `/api` is unaffected.
- [ ] `npm run build` succeeds; the SPA loads against the running service and can create a list, start a timed session, and see it refuse to stop. Commit.

## Phase 5 Definition of Done

- `dotnet test` green; `npm run build` green.
- Starting a session blocks immediately (no 2 s wait) — a test asserts the refresh path.
- `SessionIntegrity` detects a forged duration and fails closed on a garbage mac.
- `ServiceSddl` and `UninstallGuard` are unit-tested; install/uninstall scripts exist and are checklisted.
- The UI builds and drives the API end to end.
