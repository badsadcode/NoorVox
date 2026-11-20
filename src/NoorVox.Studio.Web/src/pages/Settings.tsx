import { useCallback, useEffect, useState } from "react";
import { call, errorText } from "../bridge";
import { Drive, Folder } from "../components/Icons";
import { fmtBytes } from "../format";
import { useStudio } from "../studio";
import type { AppInfo, StoragePath, ThemeId, VoicesResponse } from "../types";

export const THEMES: { id: ThemeId; name: string; blurb: string; chips: string[] }[] = [
  { id: "nocturne", name: "Nocturne", blurb: "Violet night", chips: ["#12101c", "#1b1829", "#8b5cf6", "#a78bfa", "#ece9f7"] },
  { id: "daylight", name: "Daylight", blurb: "Calm and light", chips: ["#f4f2fa", "#ffffff", "#6d3fe0", "#5428c4", "#1e1a2e"] },
  { id: "tape", name: "Tape Deck", blurb: "Warm amber studio", chips: ["#17120d", "#211a13", "#f59e0b", "#fbbf24", "#f3e9dc"] },
  { id: "thalassa", name: "Thalassa", blurb: "Deep sea teal", chips: ["#08151a", "#0e2128", "#22c3c9", "#5eead4", "#e2f4f6"] },
  { id: "graphite", name: "Graphite", blurb: "Grey with a lime signal", chips: ["#111213", "#191b1d", "#c6f432", "#d9ff66", "#f1f3f4"] },
  { id: "lavender", name: "Purple Heart", blurb: "Light orchid 💜", chips: ["#fbf5fb", "#ffffff", "#b0279c", "#8e1c7e", "#2a1630"] },
];

const KIND_LABEL: Record<StoragePath["kind"], string> = {
  models: "Models & voices", cache: "Download cache", temp: "Temporary audio", projects: "Projects", exports: "Exports", webview: "Interface data",
};

export function StoragePaths({ kinds }: { kinds?: StoragePath["kind"][] }) {
  const { notify } = useStudio();
  const [paths, setPaths] = useState<StoragePath[]>([]);
  const load = useCallback(() => { call<{ paths: StoragePath[] }>("storage.get").then((r) => setPaths(r.paths), (e) => notify(errorText(e), true)); }, [notify]);
  useEffect(load, [load]);

  const choose = async (kind: string) => {
    try {
      const r = await call<{ cancelled: boolean; note?: string }>("storage.choose", { kind });
      if (!r.cancelled) { notify(r.note ?? "Location changed."); load(); }
    } catch (e) { notify(errorText(e), true); }
  };
  const reset = async (kind: string) => { try { await call("storage.reset", { kind }); load(); } catch (e) { notify(errorText(e), true); } };

  return (
    <div className="paths">
      {paths.filter((p) => !kinds || kinds.includes(p.kind)).map((p) => (
        <div key={p.kind} className="path-row">
          <div><b>{KIND_LABEL[p.kind]}</b><div className="muted small">{p.source === "configured" ? "custom" : "default"}</div></div>
          <div className="p">
            <span className="mono">{p.path}</span>
            <div className="muted small"><Drive size={13} /> {p.drive} · {fmtBytes(p.freeBytes)} free{!p.exists && " · will be created when needed"}</div>
          </div>
          <div className="row" style={{ flexWrap: "nowrap" }}>
            <button className="btn small" onClick={() => choose(p.kind)}>Change…</button>
            <button className="btn small" onClick={() => call("storage.reveal", { kind: p.kind })} aria-label={`Open ${KIND_LABEL[p.kind]} folder`}><Folder size={14} /></button>
            {p.source === "configured" && <button className="btn small ghost" onClick={() => reset(p.kind)}>Reset</button>}
          </div>
        </div>
      ))}
    </div>
  );
}

export function Settings() {
  const { info, setTheme, notify, refreshInfo } = useStudio();
  const [voices, setVoices] = useState<VoicesResponse | null>(null);
  useEffect(() => { call<VoicesResponse>("voices.list").then(setVoices, () => undefined); }, []);

  const save = async (patch: Record<string, unknown>) => {
    try { await call<AppInfo>("settings.set", patch); await refreshInfo(); notify("Saved."); } catch (e) { notify(errorText(e), true); }
  };
  const s = info?.settings;

  return (
    <>
      <div className="page-head">
        <div><h1>Settings</h1><p>Everything NoorVox stores and how it behaves. Changes are saved immediately.</p></div>
      </div>
      <div className="stack">
        <section className="panel">
          <h2>Storage locations</h2>
          <p className="muted small" style={{ marginTop: -4 }}>Put models, caches, temporary audio and exports on any drive, C: included. NoorVox always shows the exact folder it uses. Windows itself may still write small system files to its own locations.</p>
          <StoragePaths />
        </section>

        <section className="panel">
          <h2>Appearance</h2>
          <div className="themes" role="group" aria-label="Theme">
            {THEMES.map((t) => (
              <button key={t.id} className="theme-swatch" aria-pressed={s?.theme === t.id} onClick={() => setTheme(t.id)} style={{ background: t.chips[0], color: t.chips[4] }}>
                <b>{t.name}</b><div style={{ fontSize: 12, opacity: .75 }}>{t.blurb}</div>
                <div className="chips">{t.chips.map((c) => <i key={c} style={{ background: c, border: "1px solid rgba(128,128,128,.35)" }} />)}</div>
              </button>
            ))}
          </div>
        </section>

        <section className="panel">
          <h2>Voice engine</h2>
          <div className="stack">
            <label className="toggle">
              <input type="checkbox" checked={s?.stopOnExit ?? true} onChange={(e) => save({ stopOnExit: e.target.checked })} />
              <span><b>Stop voice engines when NoorVox Studio closes</b><br /><span className="muted small">Recommended. When off, engines keep running in the background (you'll be warned when closing) until the idle timeout. Applies to engines started after the change.</span></span>
            </label>
            <div style={{ maxWidth: 360 }}>
              <label className="field" htmlFor="idle">Stop an idle engine after</label>
              <select id="idle" value={s?.idleTimeoutSeconds ?? 900} onChange={(e) => save({ idleTimeoutSeconds: Number(e.target.value) })}>
                <option value={300}>5 minutes</option><option value={900}>15 minutes</option><option value={1800}>30 minutes</option>
                <option value={3600}>1 hour</option><option value={0}>Never</option>
              </select>
            </div>
            <div style={{ maxWidth: 360 }}>
              <label className="field" htmlFor="defvoice">Default voice</label>
              <select id="defvoice" value={s?.defaultVoice ?? ""} onChange={(e) => save({ defaultVoice: e.target.value })}>
                <option value="">Model default{voices?.defaultVoice ? ` (${voices.defaultVoice})` : ""}</option>
                {voices?.voices.map((v) => <option key={v.id} value={v.id}>{v.displayName} · {v.languageName} · {v.gender}</option>)}
              </select>
            </div>
            <p className="note">Execution: CPU (about 6× faster than real time for long passages on this kind of PC). GPU acceleration isn't available yet, and NoorVox won't claim it is.</p>
          </div>
        </section>

        <section className="panel">
          <h2>About</h2>
          <dl className="kv">
            <dt>Version</dt><dd>NoorVox {info?.version}</dd>
            <dt>Settings file</dt><dd className="mono">{info?.home.settingsFile}</dd>
            <dt>Worker</dt><dd className="mono">{info?.workerExecutable.path}{info && !info.workerExecutable.exists && " (missing!)"}</dd>
            <dt>Licenses</dt><dd>Kokoro-82M model &amp; voices: Apache-2.0 (hexgrad) · KokoroSharp: MIT · MisakiSharp: Apache-2.0 · ONNX Runtime: MIT · WebView2 · React: MIT</dd>
            <dt>Automation</dt><dd>Everything here is also available headless via <span className="mono">noorvox.exe</span> (see docs/AI_INTEGRATION.md).</dd>
          </dl>
        </section>
      </div>
    </>
  );
}
