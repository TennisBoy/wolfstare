---
paths:
  - "src/Wolfstare.Core/Rules/**"
  - "src/Wolfstare.Enforcement/Apps/**"
  - "src/Wolfstare.Enforcement/Machine/ImageFileExecutionOptionsSetting.cs"
  - "src/Wolfstare.Service/Enforcement/**"
  - "src/Wolfstare.BlockStub/**"
  - "tests/**/Rules/**"
  - "tests/**/Apps/**"
  - "tests/**/Machine/**"
  - "tests/**/Enforcement/**"
---

# Rules and app enforcement

Moved out of `CLAUDE.md` so it loads only when these files are touched. Read it before any
design decision about rule matching, IFEO, or the process watcher.

## App enforcement: IFEO catches by name, ETW catches renames

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

## Rules match identity, never location

`AppRule` matches on image name, Authenticode publisher, or file description — never on
install path. That is what makes a rule for a **not-yet-installed** application work: the
IFEO registry key can exist years before the executable does. `ImageNameMatcher` therefore
strips any path it is handed and normalises to a lowercase `.exe` name.

`PublisherMatcher` is the backstop for a renamed executable, since renaming a file does not
change who signed it.

## The critical-process guard is in Core on purpose

`Rules/CriticalProcesses.cs` refuses to block `explorer.exe`, `lsass.exe`, Task Manager, and
friends. It lives in the pure domain rather than the registry adapter so it is unit-tested and
rejects a rule at *creation* time. An IFEO key on `explorer.exe` leaves no desktop shell, and
because the uninstaller refuses to run during an active lock, recovery would mean safe mode.

Administration tools are protected for the same reason: the user must retain the ability to
repair their own machine.

## Domain matching semantics

- `*` matches every host.
- `reddit.com` matches the apex **and** every subdomain — what a user expects from a bare domain.
- `*.reddit.com` matches subdomains only, not the apex.
- Label-boundary checks stop `reddit.com` matching `notreddit.com`.
- Allowlist is evaluated first, so "block everything except these" is a single `DomainRule("*")`
  plus an allowlist rather than a special case.
