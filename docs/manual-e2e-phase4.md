# Manual E2E checklist — Phase 4 application enforcement

The decision logic (which processes to block, the critical-process guard, IFEO engagement) is
covered by automated tests. The parts that touch the machine — writing HKLM IFEO keys, the ETW
kernel session, terminating a real process — are not (spec §13). This checklist verifies them
by hand.

**Run an elevated shell.** IFEO keys live in HKLM, the ETW kernel-process session needs
administrator, and terminating another process needs the right to open it. Use a throwaway app
you don't mind having killed.

## 0. Baseline

- [ ] Pick a harmless test app you can copy, e.g. `copy C:\Windows\System32\notepad.exe %TEMP%\wolftest.exe`.
- [ ] Confirm no IFEO key exists: `reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\wolftest.exe"` → error (absent).

## 1. IFEO block of an installed app (ModifySystem=true)

- [ ] Start elevated: `dotnet run --project src/Wolfstare.Service -- --Wolfstare:Enforcement:ModifySystem=true`.
- [ ] Create a block list with an app rule `{"kind":"app.imageName","value":"wolftest.exe"}` and start a timed session.
- [ ] `reg query "...\Image File Execution Options\wolftest.exe" /v Debugger` → points at `Wolfstare.BlockStub.exe`.
- [ ] Launch `%TEMP%\wolftest.exe`. The Wolfstare block message appears **instead of** the app; the app does not run.

## 2. The not-yet-installed case (the headline requirement)

- [ ] With a session active, add a rule for an app that is **not installed**, e.g. `4kvideodownloaderplus.exe`.
- [ ] Confirm the IFEO key is written for it immediately, before the app exists.
- [ ] Install (or copy a renamed exe to) `4kvideodownloaderplus.exe` and launch it → blocked by the stub on first launch.

## 3. The rename backstop (ETW watcher)

- [ ] Add a rule matching the test app's **publisher** (read it with `Get-AuthenticodeSignature %TEMP%\wolftest.exe` — for notepad this is Microsoft; use an app with an embedded signature for a real test, e.g. a copied `git.exe` signed by "Johannes Schindelin").
- [ ] Rename the exe (`copy signedapp.exe renamed.exe`) so the IFEO name no longer matches.
- [ ] Launch `renamed.exe`. It starts, then the watcher terminates it within a moment (the inherent user-mode race), and the `Terminated` path fires. Confirm the process is gone in Task Manager.

## 4. Critical-process guard at the boundary

- [ ] Attempt to create a rule for `explorer.exe` via the API → **400** with the guard's message.
- [ ] Confirm no IFEO key was ever written for `explorer.exe`:
  `reg query "...\Image File Execution Options\explorer.exe"` → absent.
- [ ] (Sanity) `explorer.exe` keeps running throughout; the desktop shell is never affected.

## 5. Restore

- [ ] Stop the session (unlocked) or let a short timed lock expire.
- [ ] Every `wolftest.exe` / `4kvideodownloaderplus.exe` IFEO key is gone: `reg query` → absent.
- [ ] Relaunch `%TEMP%\wolftest.exe` → runs normally again.
- [ ] Domain rules restored independently: if the same session also blocked a site, DNS returns to baseline too (phase-3 checklist), and neither half's restore disturbed the other.

## 6. Crash recovery

- [ ] With an app session active and `ModifySystem=true`, `taskkill /F /IM Wolfstare.Service.exe` **without** stopping the session.
- [ ] The IFEO keys are still present (enforcement outlived the process).
- [ ] Restart the service; it restores-then-reapplies from the journal. Keys still present, session still counting down.
- [ ] Let the session end; confirm the keys are removed — the journal held the true "absent" original across the crash.

---

## 7. Tamper hardening (installed service, ModifySystem=true)

These only take full effect when installed via `scripts\install.ps1` (running as LocalSystem),
not a dev console run. None is admin-proof — they raise the cost.

- [ ] **Self-healing IFEO.** With an app block active, delete its key in regedit
  (`HKLM\...\Image File Execution Options\<app>.exe`). Within ~2 seconds the service rewrites
  it. Launching the app is blocked again.
- [ ] **ACL'd keys.** With an app block active, try to delete its IFEO key in regedit as an
  administrator → access denied. Confirm the key's owner is `SYSTEM` and Administrators have a
  Deny-Delete ACE. (Taking ownership first still lets an admin delete it — that's the accepted
  ceiling.)
- [ ] **Stop-denial.** With any block active, `sc stop Wolfstare` from an elevated prompt is
  refused. End the block (retype the text) → `sc stop` works again. Confirm `sc sdshow Wolfstare`
  shows the `(D;;WP;;;IU)` deny ACE while locked and not after.
- [ ] **Auto-restart.** Kill `Wolfstare.Service.exe` in Task Manager → it restarts within
  seconds (failure actions), and the block is re-applied.
- [ ] **The ceiling holds.** Confirm the honest limits still work as escapes: booting to safe
  mode, or `sc delete` after taking ownership, or deleting the database, all end the block.
  These are documented non-goals, not bugs.
