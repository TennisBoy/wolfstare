import { useState } from "react";
import type { BlockListDto, RuleDto, RuleKind } from "../api/types";
import { api, ApiError } from "../api/client";
import { ruleLabel } from "../format";

interface Props {
  existing: BlockListDto | null;
  onClose: () => void;
  onSaved: () => void;
}

const RULE_KINDS: { kind: RuleKind; label: string; placeholder: string }[] = [
  { kind: "domain", label: "Site", placeholder: "reddit.com or *.reddit.com" },
  { kind: "app.imageName", label: "App", placeholder: "steam.exe" },
  { kind: "app.publisher", label: "Publisher", placeholder: "Valve" },
];

export function BlockListEditor({ existing, onClose, onSaved }: Props) {
  const [name, setName] = useState(existing?.name ?? "");
  const [rules, setRules] = useState<RuleDto[]>(existing?.rules ?? []);
  const [allowlist, setAllowlist] = useState<RuleDto[]>(existing?.allowlist ?? []);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save() {
    setError(null);
    setBusy(true);
    try {
      if (existing) await api.updateList(existing.id, name, rules, allowlist);
      else await api.createList(name, rules, allowlist);
      onSaved();
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onClick={onClose}>
      <div className="dialog" onClick={(e) => e.stopPropagation()} style={{ width: "min(34rem, 100%)" }}>
        <h3>{existing ? "Edit" : "New"} block list</h3>

        <label htmlFor="listname">Name</label>
        <input id="listname" value={name} onChange={(e) => setName(e.target.value)} placeholder="Deep work" />

        <RuleSection title="Block" rules={rules} onChange={setRules} />
        <RuleSection title="Allow (overrides block)" rules={allowlist} onChange={setAllowlist} />

        {error && <div className="error">{error}</div>}

        <div className="dialog-actions">
          <button onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary" onClick={save} disabled={busy || !name || rules.length === 0}>
            Save
          </button>
        </div>
      </div>
    </div>
  );
}

function RuleSection({
  title,
  rules,
  onChange,
}: {
  title: string;
  rules: RuleDto[];
  onChange: (rules: RuleDto[]) => void;
}) {
  const [kind, setKind] = useState<RuleKind>("domain");
  const [value, setValue] = useState("");

  const spec = RULE_KINDS.find((k) => k.kind === kind)!;

  function add() {
    const trimmed = value.trim();
    if (!trimmed) return;
    onChange([...rules, { kind, value: trimmed }]);
    setValue("");
  }

  return (
    <>
      <label>{title}</label>
      <div>
        {rules.length === 0 && <span className="empty">nothing yet</span>}
        {rules.map((rule, i) => (
          <span className="rule-chip" key={`${rule.kind}-${rule.value}-${i}`}>
            <span className="kind">{ruleLabel(rule.kind)}</span> {rule.value}
            <button onClick={() => onChange(rules.filter((_, j) => j !== i))} title="Remove">×</button>
          </span>
        ))}
      </div>
      <div className="add-rule">
        <select value={kind} onChange={(e) => setKind(e.target.value as RuleKind)}>
          {RULE_KINDS.map((k) => (
            <option key={k.kind} value={k.kind}>{k.label}</option>
          ))}
        </select>
        <input
          value={value}
          placeholder={spec.placeholder}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && add()}
        />
        <button onClick={add}>Add</button>
      </div>
    </>
  );
}
