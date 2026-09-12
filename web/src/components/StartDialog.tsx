import { useState } from "react";
import type { BlockListDto, LockKind } from "../api/types";
import { api, ApiError, type StartOptions } from "../api/client";

interface Props {
  list: BlockListDto;
  onClose: () => void;
  onStarted: () => void;
}

export function StartDialog({ list, onClose, onStarted }: Props) {
  const [lockKind, setLockKind] = useState<LockKind>("timed");
  const [minutes, setMinutes] = useState(25);
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const needsDuration = lockKind === "timed";
  const needsPassword = lockKind === "password";

  async function start() {
    setError(null);
    setBusy(true);
    try {
      const options: StartOptions = {
        durationMinutes: needsDuration || lockKind === "none" ? minutes : null,
        lock: needsPassword ? { kind: "password", password } : { kind: lockKind },
      };
      await api.start(list.id, options);
      onStarted();
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onClick={onClose}>
      <div className="dialog" onClick={(e) => e.stopPropagation()}>
        <h3>Start "{list.name}"</h3>

        <label htmlFor="lock">Lock</label>
        <select id="lock" value={lockKind} onChange={(e) => setLockKind(e.target.value as LockKind)}>
          <option value="timed">Timed — cannot be stopped early</option>
          <option value="password">Password — stop needs the password</option>
          <option value="none">None — stop any time</option>
        </select>

        {(needsDuration || lockKind === "none") && (
          <>
            <label htmlFor="mins">Duration (minutes)</label>
            <input
              id="mins"
              type="number"
              min={1}
              value={minutes}
              onChange={(e) => setMinutes(Math.max(1, Number(e.target.value)))}
            />
          </>
        )}

        {needsPassword && (
          <>
            <label htmlFor="pw">Password</label>
            <input id="pw" type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </>
        )}

        {lockKind === "timed" && (
          <p className="meta" style={{ marginTop: 12 }}>
            A timed lock has no early exit — not even with a password. Choose the duration carefully.
          </p>
        )}

        {error && <div className="error">{error}</div>}

        <div className="dialog-actions">
          <button onClick={onClose} disabled={busy}>Cancel</button>
          <button
            className="primary"
            onClick={start}
            disabled={busy || (needsPassword && password.length === 0)}
          >
            Start
          </button>
        </div>
      </div>
    </div>
  );
}
