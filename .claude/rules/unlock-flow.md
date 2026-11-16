---
paths:
  - "src/Wolfstare.Core/Sessions/**"
  - "src/Wolfstare.Service/Api/**"
  - "src/Wolfstare.Service/Storage/Sqlite*Repository.cs"
  - "web/src/components/UnlockDialog.tsx"
  - "web/src/api/**"
  - "tests/**/Sessions/**"
  - "tests/**/Api/**"
---

# Lock types and the unlock flow

Moved out of `CLAUDE.md` so it loads only when these files are touched. Read it before any
design decision about locks, the challenge, or unlock feedback.

**The domain supports four lock types; the API only lets you create one.** `StopPolicy` and
`SessionLock` still cover `NoLock`, `TimedLock`, `PasswordLock`, and `RandomTextLock` (which is
why the clock-tamper and integrity machinery above still matters). But the create endpoint
refuses anything but `"randomtext"` with a 400 — the user chose random-text as the *only* lock.
The challenge is a run of whole words (`RandomText.Generate`, grade-12 vocabulary), 100–5000
chars (`RandomTextLockMin/MaxLength`, generated to the **exact** length by `RandomText.GenerateExact`),
and it is served **only** as a PNG (`/blocklists/{id}/unlock-image`) — never as text. `/api/status`
returns `UnlockTextLength`, not the string. The PNG-only path is deliberate: text in the API
response would be trivially scriptable, and even the image is word-wrapped at word boundaries so
a screenshot-to-OCR round-trip is the least-friction bypass, not a one-liner. Unlocking is a
case-sensitive exact retype (`StringComparison.Ordinal`), no time gate.

A wrong retype answers 401 with the index of the **first** wrong character
(`RandomText.FirstMismatchIndex`) — never every wrong position, which would let a script recover
the text in one guess per letter, and never the expected character. Even first-mismatch is an
oracle, so `UnlockThrottle` refuses further attempts on that list for 5s (monotonic clock) with a
429, *before* evaluating them. `/unlock` and `/blocklists/{id}/stop` share one path
(`AttemptStopAsync`) so neither is an unthrottled side door. The feedback explains a refusal; the
decision is still `StopPolicy`'s alone.

Leaving the unlock page (tab hidden, or the window loses focus) or closing the dialog replaces the
challenge with new words (`POST /blocklists/{id}/challenge/reset` → `SessionManager.RegenerateChallengeAsync`).
It is allowed under a lock because it preserves the length **exactly** — `GenerateExact`, never
`Generate`, which stops short and would let repeated resets whittle the lock down. The SQLite
upsert otherwise never touches the lock; it updates `lock_text` only when both old and new kinds
are random-text, so a save still cannot change a lock's kind and the MAC matches the stored text.
