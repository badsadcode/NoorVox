import { useCallback, useEffect, useState } from "react";
import { call, errorText } from "../bridge";
import { Folder, Spinner } from "../components/Icons";
import { useStudio } from "../studio";
import type { ProjectKind, RecentProject, VoicesResponse } from "../types";

const KIND_ICON: Record<string, string> = { documentary: "🎬", audiobook: "📖", dialogue: "💬", narration: "🎙️", education: "🎓", custom: "🧩" };

export function Projects() {
  const { notify, openProject } = useStudio();
  const [recent, setRecent] = useState<RecentProject[] | null>(null);
  const [creating, setCreating] = useState(false);
  const load = useCallback(() => { call<RecentProject[]>("projects.recent").then(setRecent, (e) => notify(errorText(e), true)); }, [notify]);
  useEffect(load, [load]);

  const openDialog = async () => {
    try {
      const r = await call<{ cancelled: boolean; path?: string }>("projects.openDialog");
      if (!r.cancelled && r.path) openProject(r.path);
    } catch (e) { notify(errorText(e), true); }
  };
  const forget = async (path: string) => { try { setRecent(await call<RecentProject[]>("projects.forget", { path })); } catch (e) { notify(errorText(e), true); } };

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Projects</h1>
          <p>Documentaries, audiobooks, game dialogue and more. Each project is a plain folder you can back up, version and move.</p>
        </div>
        <div className="row">
          <button className="btn" onClick={openDialog}><Folder size={16} /> Open project…</button>
          <button className="btn primary" onClick={() => setCreating(true)}>＋ New project</button>
        </div>
      </div>

      {creating && <NewProject onClose={() => setCreating(false)} />}

      {recent && recent.length === 0 && !creating && (
        <div className="empty">
          <p style={{ fontSize: 16, color: "var(--text)", margin: "0 0 6px" }}>No projects yet.</p>
          <p style={{ margin: "0 0 14px" }}>Create one, then import a script (Markdown, text, or a dialogue CSV) or start typing.</p>
          <button className="btn primary" onClick={() => setCreating(true)}>＋ New project</button>
        </div>
      )}

      <div className="project-grid">
        {recent?.map((r) => (
          <article key={r.path} className={`panel project-card ${r.exists ? "" : "missing"}`}>
            <button className="project-open" disabled={!r.exists} onClick={() => openProject(r.path)}>
              <span className="kind-icon" aria-hidden="true">{KIND_ICON[r.kind] ?? "📁"}</span>
              <span style={{ minWidth: 0 }}>
                <b>{r.title}</b>
                <span className="muted small">{r.exists ? `${r.kind} · ${r.sections} part${r.sections === 1 ? "" : "s"} · ${r.modifiedAt ? new Date(r.modifiedAt).toLocaleString() : ""}` : "Folder not found (moved or on a disconnected drive)"}</span>
                <span className="mono muted path">{r.path}</span>
              </span>
            </button>
            <button className="btn small ghost" onClick={() => forget(r.path)} aria-label={`Remove ${r.title} from the list`}>Remove from list</button>
          </article>
        ))}
      </div>
    </>
  );
}

function NewProject({ onClose }: { onClose: () => void }) {
  const { notify, openProject } = useStudio();
  const [kinds, setKinds] = useState<ProjectKind[]>([]);
  const [voices, setVoices] = useState<VoicesResponse | null>(null);
  const [title, setTitle] = useState("");
  const [kind, setKind] = useState("documentary");
  const [voice, setVoice] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    call<ProjectKind[]>("projects.kinds").then(setKinds, () => undefined);
    call<VoicesResponse>("voices.list").then((v) => { setVoices(v); setVoice(v.defaultVoice ?? ""); }, () => undefined);
  }, []);

  const create = async () => {
    setBusy(true);
    try {
      const r = await call<{ cancelled: boolean; path?: string }>("projects.create", { title, kind, voice: voice || null });
      if (!r.cancelled && r.path) { notify(`Created “${title}”.`); openProject(r.path); }
    } catch (e) { notify(errorText(e), true); } finally { setBusy(false); }
  };

  return (
    <section className="panel stack" style={{ marginBottom: 16 }} aria-label="New project">
      <h2>New project</h2>
      <div style={{ maxWidth: 520 }}>
        <label className="field" htmlFor="ptitle">Name</label>
        <input id="ptitle" type="text" value={title} onChange={(e) => setTitle(e.target.value)} placeholder="e.g. NoorEngine Documentary" autoFocus
          onKeyDown={(e) => { if (e.key === "Enter" && title.trim()) create(); }} />
      </div>
      <div>
        <label className="field">Template</label>
        <div className="kind-grid" role="radiogroup" aria-label="Template">
          {kinds.map((k) => (
            <button key={k.id} role="radio" aria-checked={kind === k.id} className="kind-card" onClick={() => setKind(k.id)}>
              <span className="kind-icon" aria-hidden="true">{KIND_ICON[k.id]}</span>
              <b>{k.displayName}</b>
              <span className="muted small">{k.description}</span>
              <span className="muted small">{k.sectionLabel} → {k.sceneLabel} → {k.segmentLabel}</span>
            </button>
          ))}
        </div>
      </div>
      <div style={{ maxWidth: 360 }}>
        <label className="field" htmlFor="pvoice">{kinds.find((k) => k.id === kind)?.usesSpeakers ? "Default voice (characters can be cast later)" : "Narrator voice"}</label>
        <select id="pvoice" value={voice} onChange={(e) => setVoice(e.target.value)}>
          {voices?.voices.map((v) => <option key={v.id} value={v.id}>{v.displayName} · {v.languageName} · {v.gender}</option>)}
        </select>
      </div>
      <p className="muted small" style={{ margin: 0 }}>Next you'll choose where the project folder goes (default: your Projects folder).</p>
      <div className="row" style={{ justifyContent: "flex-end" }}>
        <button className="btn ghost" onClick={onClose}>Cancel</button>
        <button className="btn primary" disabled={!title.trim() || busy} onClick={create}>{busy && <Spinner size={15} />} Create project…</button>
      </div>
    </section>
  );
}
