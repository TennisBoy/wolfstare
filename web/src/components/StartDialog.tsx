import { useState } from "react";
import type { BlockListDto } from "../api/types";
import { api, ApiError, type StartOptions } from "../api/client";

interface Props {
  list: BlockListDto;
  onClose: () => void;
  onStarted: () => void;
}

const MIN_LENGTH = 5000;

/// Only one lock exists: a random-text lock of at least 5000 characters. There is deliberately
/// no weaker option — no timer to wait out, no password to remember your way past. The only way
/// to end a block through the app is to retype its string by hand.
export function StartDialog({ list, onClose, onStarted }: Props) {
  const [textLength, setTextLength] = useState(MIN_LENGTH);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function start() {
    setError(null);
    setBusy(true);
    try {
      const options: StartOptions = {
        durationMinutes: null,
        lock: { kind: "randomtext", textLength },
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

        <label htmlFor="len">Characters to retype to unlock (minimum {MIN_LENGTH})</label>
        <input
          id="len"
          type="number"
          min={MIN_LENGTH}
          max={100000}
          value={textLength}
          onChange={(e) => setTextLength(Math.max(MIN_LENGTH, Number(e.target.value)))}
        />

        <p className="meta" style={{ marginTop: 12 }}>
          This block runs until you retype {textLength} random characters exactly, by hand —
          pasting is disabled. There is no timer and no password: the typing is the only way out
          through the app.
        </p>

        {error && <div className="error">{error}</div>}

        <div className="dialog-actions">
          <button onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary" onClick={start} disabled={busy}>Start</button>
        </div>
      </div>
    </div>
  );
}
