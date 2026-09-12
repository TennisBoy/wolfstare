# Wolfstare Phase 4: Application Enforcement

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Block applications from running — including ones not installed yet — via IFEO redirection to a block stub, backed by an ETW process watcher that catches renamed executables (spec §8).

**Architecture:** IFEO registry keys are `ISystemSetting`s driven by the same journalled mutator as phase 3, so they are reversible and survive a crash. The block stub is a tiny standalone exe. The ETW watcher is a hosted component reading `Microsoft-Windows-Kernel-Process` start events, resolving each process's identity, and terminating matches. The decision — which processes to redirect or kill — stays in Core (`RuleSet.EvaluateApp`, `CriticalProcesses`), already built and tested in phase 1.

**Tech Stack:** .NET 9, `Microsoft.Diagnostics.Tracing.TraceEvent` for ETW, `Microsoft.Win32.Registry` for IFEO, P/Invoke `MessageBoxW` for the stub.

**Spec:** `docs/superpowers/specs/2026-08-27-wolfstare-design.md`

## Global Constraints

- `Wolfstare.Core` stays `net9.0`, zero package references. App-matching and the critical-process guard already live there.
- App enforcement, like website enforcement, engages only under `ModifySystem=true`. Default off.
- **The critical-process guard is checked again at the enforcement boundary, not only at rule creation.** Defence in depth: a rule that somehow reached the enforcer targeting `explorer.exe` must still never be acted on. IFEO-ing or killing a critical process can make the machine unbootable.
- The ETW watcher never terminates a process in the critical-process denylist, whatever the rules say.
- Terminating a process is best-effort: access-denied (a protected or higher-integrity process) is logged, not fatal.

---

### Task 1: `ImageFileExecutionOptionsSetting`

**Files:** `src/Wolfstare.Enforcement/Machine/ImageFileExecutionOptionsSetting.cs`, test helper only (adapter itself is not unit-tested — it writes HKLM; spec §13).

**Produces:** `sealed class ImageFileExecutionOptionsSetting(string imageName, string stubPath) : ISystemSetting`
- `Key => $"ifeo:{imageName}"` (imageName canonicalised via `ImageNameMatcher.Canonical`).
- Value: the `Debugger` string under `HKLM\...\Image File Execution Options\<name>` (and the `Wow6432Node` view), or null when absent.
- `WriteAsync(stubPath)` sets `Debugger` in both views; `WriteAsync(null)` removes it (and removes the key if now empty).
- **Constructor throws `ArgumentException` if `CriticalProcesses.IsProtected(imageName)`** — the guard at the boundary.

Pure helper to test: the IFEO subkey path builder (`IfeoPaths.For(imageName)` → native + Wow64 paths), and that constructing for a protected name throws. These need no registry.

- [x] Test the path builder and the protected-name guard (both pure). Red, implement, green, commit.

### Task 2: Block stub executable

**Files:** `src/Wolfstare.BlockStub/Wolfstare.BlockStub.csproj` (`net9.0-windows`, `OutputType=WinExe`), `Program.cs`, `NativeMethods.cs`.

The exe Windows launches in place of a blocked app. IFEO passes it the original command line as arguments. It shows a `MessageBoxW` naming the blocked app and exits non-zero. No UI framework, no project references — it must stay tiny and dependency-free so it starts instantly and cannot itself be blocked by a missing dependency.

Pure helper to test (in `Wolfstare.Enforcement.Tests`, referencing the stub or a copied helper): `BlockStubMessage.ForCommandLine(string[] args)` → the human-readable exe name pulled from the IFEO-supplied argv. IFEO passes `<stub> <originalExePath> <originalArgs...>`, so argv[0] here is the blocked exe path.

- [x] Test the message/arg parsing helper. Red, implement, green. Build the exe (`dotnet build`) and confirm it produces a WinExe. Commit.

### Task 3: Process identity resolution

**Files:** `src/Wolfstare.Enforcement/Apps/ProcessInspector.cs`, test `Apps/ProcessInspectorTests.cs`.

**Produces:** `static ProcessInspector` with:
- `ProcessIdentity FromImagePath(string imagePath)` — image name from the path; publisher and file description read from the file's Authenticode signature and version info. Missing/unsigned → nulls.
- `static string? PublisherOf(string path)` and `static string? DescriptionOf(string path)` — separated so they are individually testable against real files.

Testable without ETW: run against real on-disk executables. `notepad.exe` under System32 has a Microsoft publisher and a file description; a temp file with a random name has neither. The mapping from an image path to a `ProcessIdentity` is pure given the file.

- [x] Test identity resolution against `%WINDIR%\System32\notepad.exe` (has publisher + description) and a throwaway unsigned file (both null). Red, implement, green, commit.

### Task 4: App enforcement engagement

**Files:** `src/Wolfstare.Service/Enforcement/IAppEnforcement.cs` (interface + `NullAppEnforcement` + `WindowsAppEnforcement`), fold into `WebsiteEnforcer` or a sibling `ApplicationEnforcer`; test `Enforcement/AppEnforcementTests.cs`.

**Produces:**
- `interface IAppEnforcement { Task ApplyAsync(RuleSet rules, CancellationToken); Task RestoreAsync(CancellationToken); }`
- `NullAppEnforcement` — ModifySystem=false, no-op.
- `WindowsAppEnforcement(ISystemMutator, stubPath, logger)` — for each `AppRule` with an `ImageNameMatcher`, apply an `ImageFileExecutionOptionsSetting`; skip protected names (guard already throws, so filter first and log). Publisher/description matchers cannot be expressed as IFEO (no name to key on) — they are enforced only by the ETW watcher, so `ApplyAsync` records which matchers are IFEO-covered and leaves the rest to the watcher. `RestoreAsync` delegates to the mutator (shared journal with website settings — one `RestoreAllAsync` puts everything back).

Test with a fake mutator: a session with an `ImageNameMatcher("steam.exe")` applies one IFEO setting; a `PublisherMatcher` applies none (watcher-only); an `ImageNameMatcher` for a critical process applies none and logs; ending the session restores.

- [x] Red, implement, green, commit.

### Task 5: ETW process watcher

**Files:** `src/Wolfstare.Enforcement/Apps/ProcessWatcher.cs`, test `Apps/ProcessWatcherTests.cs`.

**Produces:** `sealed class ProcessWatcher(RuleSetCache rules, IProcessTerminator terminator, Func<int,string?> imagePathOf, ILogger) : IDisposable` with `void Start()`, `event Action<ProcessIdentity>? Terminated`.
- `interface IProcessTerminator { void Terminate(int pid); }` and a real `WindowsProcessTerminator` (`OpenProcess`+`TerminateProcess`, access-denied logged not thrown).
- On each start event: resolve identity, evaluate against the cache, and if `Deny` **and not** a critical process, terminate.

The ETW subscription itself is not unit-tested (it needs a live session and admin). The **decision core is**: extract `ProcessStartDecision.ShouldTerminate(RuleSet, ProcessIdentity)` → bool, which folds in the critical-process guard, and test it exhaustively with a fake terminator and synthetic identities. `ProcessWatcher`'s event handler calls that method, so the tested logic is the logic that runs.

- [x] Test `ShouldTerminate`: blocked image name → true; blocked publisher (renamed exe) → true; critical process even if "blocked" → false; permitted → false; allowlisted → false. Test that `ProcessWatcher`, fed a synthetic start event through an injected seam, terminates a match and raises `Terminated`, and never terminates a critical process. Red, implement, green, commit.

### Task 6: Wire into the service

**Files:** modify `Program.cs`, `SystemEnforcementFactory` (or a new app factory), `appsettings.json`; extend the manual E2E checklist.

- Register `IAppEnforcement` (Null unless ModifySystem) and the `ProcessWatcher` (started only under ModifySystem, since termination needs admin and ETW kernel-process needs elevation).
- `WebsiteEnforcer`'s refresh (rename to `EnforcementCoordinator` if it now covers both, or add an `ApplicationEnforcer` hosted service that shares the refresh) also drives `IAppEnforcement.ApplyAsync/RestoreAsync`.
- The stub path resolves next to the service exe.
- Extend `docs/manual-e2e-phase4.md`: install an IFEO rule for a test app (e.g. a copied `calc` renamed), launch it, confirm the stub appears; rename the exe and confirm the ETW watcher still kills it by publisher; confirm restore removes the IFEO keys; confirm `explorer.exe` can never be added.

- [x] Red where testable, implement, green, full suite, commit, push. Manual E2E documented (privileged path).

## Phase 4 Definition of Done

- `dotnet test` green.
- With a fake mutator, an `ImageNameMatcher` session installs an IFEO setting and a critical-process name never does.
- `ProcessStartDecision.ShouldTerminate` returns true for a blocked publisher on a renamed image and false for any critical process.
- No automated step writes HKLM IFEO keys or terminates a real process.
- `docs/manual-e2e-phase4.md` covers the privileged path, including the not-yet-installed case and the rename case.

---

## Execution record

Completed 2026-09-12. **283 tests passing** (134 Core, 97 Enforcement, 52 Service).

Verified against the running service (`ModifySystem=false`): a mixed app+domain session starts
and reports healthy, and an `explorer.exe` rule is rejected at the API with the guard's message.
The privileged IFEO/ETW/terminate paths are covered by `docs/manual-e2e-phase4.md`.

### Deviations

1. **`RestoreMatchingAsync` added to `ISystemMutator`.** Website and app enforcement share one
   journal but engage independently — a session may block only apps or only sites — so a full
   `RestoreAllAsync` would rip out the other half's settings when one disengages. The scoped
   restore (by key prefix) is the fix; `RestoreAllAsync` is now it with a match-everything filter.

2. **`WindowsSystemEnforcement` no longer owns its mutator.** Phase 3 had it build a private
   mutator whose resolver only knew website settings. With a shared journal now also holding
   `ifeo:` keys, that resolver would leave IFEO keys unresolved forever. Restore reconstruction
   moved to `SystemSettingResolver`, which covers every setting type; each enforcer holds its
   own (stateless) `JournalledMutator` over the one journal.

3. **`ImageNameMatcher.Canonical` made public.** The IFEO key builder must canonicalise names
   identically to the matcher — they have to agree on what "the same executable" is.

4. **`ProcessInspector` uses `X509Certificate.CreateFromSignedFile` with SYSLIB0057 suppressed.**
   Its suggested replacement reads certificate files, not embedded Authenticode signatures. Only
   embedded signatures are read (not catalog), which is right for the third-party apps we block;
   catalog-signed OS binaries are Microsoft-published and already barred.

5. **The block stub duplicates its message logic** rather than referencing `BlockStubMessage`,
   to stay dependency-free — a stub that failed to launch for a missing dependency would let a
   blocked app through. `BlockStubMessage` is the tested reference copy.

6. **The process watcher is always constructed, started only under `ModifySystem`.** Avoids a
   nullable DI registration; construction is cheap and only `Start()` is privileged.

### Not yet done

- **Immediate refresh after a start/stop** (shared with phase 3) — IFEO keys appear on the next
  2s tick, not instantly.
- The **interactive-user SID** for the proxy hive is still best-effort (`ModifySystem=true`
  installer concern, phase 5).
