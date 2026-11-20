import { useEffect, useRef, type ReactNode } from "react";
import type { WorkerState } from "../types";
import { Alert, Check, Spinner, Stop } from "./Icons";

const stateText: Record<WorkerState, string> = {
  stopped: "Stopped", starting: "Starting", idle: "Running · no model", loading: "Loading model", ready: "Ready",
  generating: "Generating", stopping: "Stopping", error: "Crashed", unresponsive: "Not responding",
};

/** Worker state as icon + words (never colour alone). */
export function StateBadge({ state }: { state: WorkerState }) {
  const icon = state === "ready" || state === "idle" ? <Check size={15} />
    : state === "stopped" ? <Stop size={14} />
    : state === "error" || state === "unresponsive" ? <Alert size={15} />
    : <Spinner size={15} />;
  return <span className={`state ${state}`}>{icon}{stateText[state]}</span>;
}

export function Progress({ value, total, label }: { value?: number; total?: number; label?: string }) {
  const pct = total && total > 0 && value != null ? Math.min(100, (value / total) * 100) : null;
  return (
    <div>
      <div className={`bar ${pct == null ? "indeterminate" : ""}`} role="progressbar" aria-valuenow={pct ?? undefined} aria-valuemin={0} aria-valuemax={100} aria-label={label}>
        <i style={{ width: `${pct ?? 30}%` }} />
      </div>
    </div>
  );
}

export function Confirm({ title, children, confirmText, danger, onConfirm, onCancel }: {
  title: string; children: ReactNode; confirmText: string; danger?: boolean; onConfirm: () => void; onCancel: () => void;
}) {
  const ref = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    ref.current?.focus();
    const k = (e: KeyboardEvent) => { if (e.key === "Escape") onCancel(); };
    window.addEventListener("keydown", k);
    return () => window.removeEventListener("keydown", k);
  }, [onCancel]);
  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label={title}>
      <div className="modal">
        <h3>{title}</h3>
        <div className="muted" style={{ marginBottom: 18 }}>{children}</div>
        <div className="row" style={{ justifyContent: "flex-end" }}>
          <button className="btn" onClick={onCancel}>Cancel</button>
          <button ref={ref} className={`btn ${danger ? "danger" : "primary"}`} onClick={onConfirm}>{confirmText}</button>
        </div>
      </div>
    </div>
  );
}
