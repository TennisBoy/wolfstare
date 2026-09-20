import { useEffect, useState } from "react";
import type { ActiveSessionDto } from "../api/types";
import { api, ApiError } from "../api/client";
import { formatDuration } from "../format";

interface Props {
  session: ActiveSessionDto;
  onClose: () => void;
  onStopped: () => void;
}

/// Stops an active session. A random-text lock shows the string as an image (never as text a
/// script could read) and makes you retype it exactly; paste is disabled. A timed lock can't be
/// stopped at all, and the server enforces every decision regardless of what this dialog sends.
export function UnlockDialog({ session, onClose, onStopped }: Props) {
  const [entry, setEntry] = useState("");
  const [imageUrl, setImageUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const isPassword = session.lockKind === "password";
  const isRandomText = session.lockKind === "randomtext";
  const expectedLength = session.unlockTextLength ?? 0;

  // Load the unlock text as an image (only for a random-text lock).
  useEffect(() => {
    if (!isRandomText) return;
    let revoked: string | null = null;
    api.unlockImageUrl(session.blockListId)
      .then((url) => { revoked = url; setImageUrl(url); })
      .catch((e) => setError(e instanceof ApiError ? e.message : String(e)));
    return () => { if (revoked) URL.revokeObjectURL(revoked); };
  }, [isRandomText, session.blockListId]);

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

  const block = (e: React.SyntheticEvent) => e.preventDefault();

  return (
    <div className="overlay" onClick={onClose}>
      <div
        className="dialog"
        onClick={(e) => e.stopPropagation()}
        style={isRandomText ? { width: "min(46rem, 100%)" } : undefined}
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
            <label>Retype this exactly ({expectedLength} characters) — no pasting</label>
            {imageUrl
              ? <div className="challenge-image">
                  <img src={imageUrl} alt="" draggable={false} onContextMenu={block} />
                </div>
              : <div className="meta">Loading…</div>}
            <label htmlFor="retype">Your entry</label>
            <textarea
              id="retype"
              autoFocus
              value={entry}
              onChange={(e) => setEntry(e.target.value)}
              onPaste={block}
              onDrop={block}
              onContextMenu={block}
              rows={5}
              autoComplete="off"
              autoCorrect="off"
              autoCapitalize="off"
              spellCheck={false}
              style={{ width: "100%", fontFamily: "monospace", wordBreak: "break-all", resize: "none" }}
            />
            <div className="meta">{entry.length} / {expectedLength} characters</div>
          </>
        )}

        {!isPassword && !isRandomText && <p>Stop this block now?</p>}

        {error && <div className="error">{error}</div>}

        <div className="dialog-actions">
          <button onClick={onClose} disabled={busy}>Cancel</button>
          <button
            className="danger"
            onClick={stop}
            disabled={busy || (isPassword && !entry) || (isRandomText && entry.length !== expectedLength)}
          >
            Stop
          </button>
        </div>
      </div>
    </div>
  );
}
