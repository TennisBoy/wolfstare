# 2026-10-10 — unlock feedback, challenge reset, reinstall

Shipped: first-wrong-character feedback with a 5s cooldown (PR #1), and replacing the challenge
when the user leaves the unlock page, with exact-length generation (PR #2).

## Design lessons

- **Any feedback about a secret is an oracle.** Reporting *every* wrong position recovers the
  whole challenge in ~60 scripted attempts; reporting the *first* is one character per ~30
  guesses. The 5s cooldown, refused *before* evaluation, is what makes guessing slower than
  typing. Check every endpoint that accepts the text — `/stop` was a second, unthrottled door
  until both were routed through `AttemptStopAsync`.
- **"Up to N" generators shrink under repetition.** `RandomText.Generate` stops before its
  target, so regenerating from the current length loses a few characters each time; leaving the
  tab repeatedly would have whittled a lock down. Anything that replaces a lock's content must
  preserve its size *exactly* (`GenerateExact`).
- **A change that can only make a lock harder may be allowed under a lock** — but prove the
  "only harder" part (here: identical length, same kind) with a test, because that claim is the
  whole justification.

## Testing lessons

- **In-memory fakes can hide persistence rules.** `InMemorySessionRepository` replaces the whole
  record, so Core tests passed while the SQLite upsert silently ignored the new `lock_text` — and
  wrote a MAC for text it hadn't stored. Only the API test against real SQLite caught it. When
  a feature changes a field that was previously immutable, test it through the real repository.
- **A running `dotnet run` instance locks `bin/`**, so `dotnet test` fails with MSB3027. Stop the
  throwaway instance before running the suite.

## Operations lessons

- **Reinstalling needs every lock cleared first** — the uninstaller refuses while locked, and the
  service can't be stopped to free its binaries. There is deliberately no "update while locked"
  path: the service can't tell a good build from one with enforcement removed.
- **Block lists live in `%ProgramData%\Wolfstare\wolfstare.db` and survive a reinstall**; only
  the service registration is removed.
- **`Invoke-RestMethod ... | Select-Object` can print an empty table for a JSON array** (the
  array arrives as one object). That looked like data loss after the reinstall and wasn't —
  check the raw response (`Invoke-WebRequest ... .Content`) before concluding anything is gone.
- **A dismissed UAC prompt is silent.** Always have the elevated script write a log and check it
  exists (as `CLAUDE.md` says) — one prompt here timed out and nothing ran.

## Working with the user

- The lock is the product. Requests to lift, pause, bypass, transcribe, or "reinstall around" an
  active lock were declined every time, however reasonable the stated reason — and the user, when
  it came to it, typed the 3888 characters. Help *with* the friction (spotting non-vocabulary
  typos in their own entry, explaining line-break traps), never *around* it.
