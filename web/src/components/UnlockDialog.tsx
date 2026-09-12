import { useState } from "react";
import type { ActiveSessionDto } from "../api/types";
import { api, ApiError } from "../api/client";
import { formatDuration } from "../format";

interface Props {
  session: ActiveSessionDto;
  onClose: () => void;
  onStopped: () => void;
}

/// Stops an active session. A password lock asks for the password; a timed lock cannot be
/// stopped at all, and the server enforces that regardless of what this dialog sends.
export function UnlockDialog({ session, onClose, onStopped }: Props) {
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const isPassword = session.lockKind === "password";

  async function stop() {
    setError(null);
    setBusy(true);
    try {
      if (isPassword) await api.unlock(session.blockListId, password);
      else await api.stop(session.blockListId);
      onStopped();
      onClose();
    } catch (e) {
      if (e instanceof ApiError && e.status === 423) {
        setError(
          `This block is locked for another ${formatDuration(e.remainingSeconds ?? session.remainingSeconds)}. `
          + "There is no way to end it early.",
        );
      } else {
        setError(e instanceof ApiError ? e.message : String(e));
      }
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onClick={onClose}>
      <div className="dialog" onClick={(e) => e.stopPropagation()}>
        <h3>Stop "{session.blockListName}"</h3>

        {isPassword ? (
          <>
            <label htmlFor="pw">Password</label>
            <input
              id="pw"
              type="password"
              autoFocus
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && stop()}
            />
          </>
        ) : (
          <p>Stop this block now?</p>
        )}

        {error && <div className="error">{error}</div>}

        <div className="dialog-actions">
          <button onClick={onClose} disabled={busy}>Cancel</button>
          <button className="danger" onClick={stop} disabled={busy || (isPassword && !password)}>
            Stop
          </button>
        </div>
      </div>
    </div>
  );
}
