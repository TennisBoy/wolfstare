import { useCallback, useEffect, useState } from "react";
import type { ActiveSessionDto, BlockListDto, StatusDto } from "./api/types";
import { api, ApiError } from "./api/client";
import { formatDuration, ruleLabel } from "./format";
import { BlockListEditor } from "./components/BlockListEditor";
import { StartDialog } from "./components/StartDialog";
import { UnlockDialog } from "./components/UnlockDialog";

export function App() {
  const [status, setStatus] = useState<StatusDto | null>(null);
  const [lists, setLists] = useState<BlockListDto[]>([]);
  const [error, setError] = useState<string | null>(null);

  const [editing, setEditing] = useState<BlockListDto | null | "new">(null);
  const [starting, setStarting] = useState<BlockListDto | null>(null);
  const [stopping, setStopping] = useState<ActiveSessionDto | null>(null);

  const refresh = useCallback(async () => {
    try {
      const [s, l] = await Promise.all([api.status(), api.blockLists()]);
      setStatus(s);
      setLists(l);
      setError(null);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    }
  }, []);

  // Poll once a second so countdowns and lock states stay live without websockets.
  useEffect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), 1000);
    return () => clearInterval(timer);
  }, [refresh]);

  const activeByList = new Map(status?.activeSessions.map((s) => [s.blockListId, s]) ?? []);

  async function deleteList(list: BlockListDto) {
    try {
      await api.deleteList(list.id);
      await refresh();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    }
  }

  return (
    <div className="app">
      <header className="app-header">
        <h1><span>Wolf</span>stare</h1>
        {status && (
          <span className={`health ${status.health}`}>
            {status.health === "ok" ? "● enforcing" : "▲ degraded"}
          </span>
        )}
      </header>

      {error && <div className="error" style={{ marginBottom: 16 }}>{error}</div>}

      <section>
        <h2>Active</h2>
        {status && status.activeSessions.length === 0 && (
          <p className="empty">No active sessions. Start one from a block list below.</p>
        )}
        {status?.activeSessions.map((session) => (
          <div className="panel" key={session.id}>
            <div className="row row-wrap">
              <div>
                <div className="name">{session.blockListName}</div>
                <div className="meta">
                  {session.lockKind === "timed" && <span className="locked-badge">timed lock</span>}{" "}
                  {session.lockKind === "password" && <span className="locked-badge">password</span>}{" "}
                  {session.remainingSeconds === null ? "running" : "remaining"}
                </div>
              </div>
              <div className="row" style={{ gap: 16 }}>
                <span className="countdown">{formatDuration(session.remainingSeconds)}</span>
                <button
                  className="danger"
                  onClick={() => setStopping(session)}
                  disabled={session.lockKind === "timed" && !session.canBeStopped}
                  title={
                    session.lockKind === "timed" && !session.canBeStopped
                      ? "A timed lock cannot be stopped early"
                      : "Stop this block"
                  }
                >
                  Stop
                </button>
              </div>
            </div>
          </div>
        ))}
      </section>

      <section>
        <div className="row">
          <h2>Block lists</h2>
          <button className="primary" onClick={() => setEditing("new")}>New list</button>
        </div>

        {lists.length === 0 && <p className="empty">No block lists yet.</p>}
        {lists.map((list) => {
          const active = activeByList.get(list.id);
          const locked = active && active.lockKind !== "none" && !active.canBeStopped;
          return (
            <div className="panel" key={list.id}>
              <div className="row row-wrap">
                <div>
                  <div className="name">{list.name}</div>
                  <div className="meta">
                    {list.rules.map((r) => `${ruleLabel(r.kind)}:${r.value}`).join(", ") || "no rules"}
                  </div>
                </div>
                <div className="row" style={{ gap: 8 }}>
                  {active ? (
                    <span className="meta">active</span>
                  ) : (
                    <>
                      <button className="primary" onClick={() => setStarting(list)}>Start</button>
                      <button onClick={() => setEditing(list)}>Edit</button>
                      <button className="danger" onClick={() => deleteList(list)}>Delete</button>
                    </>
                  )}
                  {locked && <span className="locked-badge">locked</span>}
                </div>
              </div>
            </div>
          );
        })}
      </section>

      {editing !== null && (
        <BlockListEditor
          existing={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={refresh}
        />
      )}
      {starting && (
        <StartDialog list={starting} onClose={() => setStarting(null)} onStarted={refresh} />
      )}
      {stopping && (
        <UnlockDialog session={stopping} onClose={() => setStopping(null)} onStopped={refresh} />
      )}
    </div>
  );
}
