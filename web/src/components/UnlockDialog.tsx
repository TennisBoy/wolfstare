import { useState } from "react";
import type { ActiveSessionDto } from "../api/types";
import { api, ApiError } from "../api/client";
import { formatDuration } from "../format";

interface Props {
  session: ActiveSessionDto;
  onClose: () => void;
  onStopped: () => void;
}

/// Stops an active session. A password lock asks for the password; a random-text lock shows the
/// string and makes you retype it exactly (paste disabled); a timed lock cannot be stopped at
/// all, and the server enforces that regardless of what this dialog sends.
export function UnlockDialog({ session, onClose, onStopped }: Props) {
  const [entry, setEntry] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const isPassword = session.lockKind === "password";
  const isRandomText = session.lockKind === "randomtext";
  const target = session.unlockText ?? "";
  const matches = isRandomText && entry === target;

  async function stop() {
    setError(null);
    setBusy(true);
    try {
      if (isPassword || isRandomText) await api.unlock(session.blockListId, entry);
      else await api.stop(session.blockListId);
      onStopped();
      onClose();
    } catch (e) {
      if (e instanceof ApiError && e.status === 423) {
        setError(
          `This block is locked for another ${formatDuration(e.remainingSeconds ?? session.remainingSeconds)}. `
          + "There is no way to end it early.",
        );
      } else if (e instanceof ApiError && e.status === 401) {
        setError(isRandomText ? "That doesn't match — check every character." : "Incorrect password.");
      } else {
        setError(e instanceof ApiError ? e.message : String(e));
      }
      setBusy(false);
    }
  }

  // Block every shortcut around actually typing the string: paste, drag-drop, and copying the
  // shown text. This is a friction nudge in the browser — see the honest caveat below about the
  // API — not a hard wall.
  const block = (e: React.SyntheticEvent) => e.preventDefault();
  const noTypingHelpers = {
    autoComplete: "off",
    autoCorrect: "off",
    autoCapitalize: "off",
    spellCheck: false,
  } as const;

  return (
    <div className="overlay" onClick={onClose}>
      <div
        className="dialog"
        onClick={(e) => e.stopPropagation()}
        style={isRandomText ? { width: "min(40rem, 100%)" } : undefined}
      >
        <h3>Stop "{session.blockListName}"</h3>

        {isPassword && (
          <>
            <label htmlFor="pw">Password</label>
            <input
              id="pw"
              type="password"
              autoFocus
              value={entry}
              onChange={(e) => setEntry(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && stop()}
            />
          </>
        )}

        {isRandomText && (
          <>
            <label>Retype this exactly ({target.length} characters) — no pasting</label>
            <textarea
              readOnly
              value={target}
              onCopy={block}
              onCut={block}
              onContextMenu={block}
              rows={4}
              style={{
                width: "100%", fontFamily: "monospace", wordBreak: "break-all", resize: "none",
                userSelect: "none",
              }}
            />
            <label htmlFor="retype">Your entry</label>
            <textarea
              id="retype"
              autoFocus
              value={entry}
              onChange={(e) => setEntry(e.target.value)}
              onPaste={block}
              onDrop={block}
              onContextMenu={block}
              rows={4}
              {...noTypingHelpers}
              style={{ width: "100%", fontFamily: "monospace", wordBreak: "break-all", resize: "none" }}
            />
            <div className="meta">{entry.length} / {target.length} characters{matches ? " — matches" : ""}</div>
          </>
        )}

        {!isPassword && !isRandomText && <p>Stop this block now?</p>}

        {error && <div className="error">{error}</div>}

        <div className="dialog-actions">
          <button onClick={onClose} disabled={busy}>Cancel</button>
          <button
            className="danger"
            onClick={stop}
            disabled={busy || (isPassword && !entry) || (isRandomText && !matches)}
          >
            Stop
          </button>
        </div>
      </div>
    </div>
  );
}
