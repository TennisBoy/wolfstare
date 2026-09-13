import { useState } from "react";
import type { BlockListDto } from "../api/types";
import { api, ApiError, type StartOptions } from "../api/client";

interface Props {
  list: BlockListDto;
  onClose: () => void;
  onStarted: () => void;
}

const MIN_LENGTH = 100;
const MAX_LENGTH = 5000;
const DEFAULT_LENGTH = 500;

/// Only one lock exists: a random-text lock. To stop the block you retype a string of random
/// words (200-5000 characters), by hand. No timer, no password — the typing is the only way out
/// through the app.
export function StartDialog({ list, onClose, onStarted }: Props) {
  const [textLength, setTextLength] = useState(DEFAULT_LENGTH);
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

        <label htmlFor="len">Characters of random words to retype (up to; {MIN_LENGTH}–{MAX_LENGTH})</label>
        <input
          id="len"
          type="number"
          min={MIN_LENGTH}
          max={MAX_LENGTH}
          value={textLength}
          onChange={(e) => setTextLength(Math.min(MAX_LENGTH, Math.max(MIN_LENGTH, Number(e.target.value))))}
        />

        <p className="meta" style={{ marginTop: 12 }}>
          This block runs until you retype the words shown, exactly, by hand — pasting is disabled.
          It'll be up to {textLength} characters (whole words, so it stops just under that), and the
          unlock screen shows the exact count. There is no timer and no password: typing it is the
          only way out through the app.
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
