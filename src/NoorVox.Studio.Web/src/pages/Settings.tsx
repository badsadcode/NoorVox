import { useCallback, useEffect, useState } from "react";
import { call, errorText } from "../bridge";
import { Drive, Folder } from "../components/Icons";
import { fmtBytes } from "../format";
import { useStudio } from "../studio";
import type { AppInfo, StorageAuditReport, StoragePath, ThemeId, VoicesResponse } from "../types";
import { Spinner } from "../components/Icons";

export const THEMES: { id: ThemeId; name: string; blurb: string; chips: string[] }[] = [
  { id: "nocturne", name: "Nocturne", blurb: "Violet night", chips: ["#12101c", "#1b1829", "#8152f0", "#a78bfa", "#ece9f7"] },
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
            <div style={{ maxWidth: 520 }}>
              <label className="field" htmlFor="kokoro-provider">Run Kokoro on</label>
              <select id="kokoro-provider" value={s?.kokoroProvider ?? "cpu"} onChange={(e) => save({ kokoroProvider: e.target.value })}>
                <option value="cpu">CPU (default)</option>
                <option value="cuda">NVIDIA GPU (CUDA)</option>
              </select>
              <p className="note" style={{ marginTop: 8 }}>
                Measured on an RTX 3080: Kokoro generated about 11× faster than real time on the GPU and 6× on the CPU, with the same voice.
                The GPU option needs the “ONNX Runtime CUDA provider” and “Qwen3-TTS engine” packs (Models page). Without them, or if CUDA
                fails, Kokoro stays on the CPU and the Monitor says so. Qwen models always run on the GPU.
              </p>
            </div>
          </div>
        </section>

        <StorageAuditPanel />

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

function StorageAuditPanel() {
  const { notify } = useStudio();
  const [report, setReport] = useState<StorageAuditReport | null>(null);
  const [busy, setBusy] = useState<"audit" | "bundle" | null>(null);
  const audit = async () => {
    setBusy("audit");
    try { setReport(await call<StorageAuditReport>("storage.audit")); } catch (e) { notify(errorText(e), true); } finally { setBusy(null); }
  };
  const bundle = async () => {
    setBusy("bundle");
    try {
      const r = await call<{ cancelled: boolean; path?: string }>("diagnostics.bundle");
      if (!r.cancelled) notify(`Support bundle saved: ${r.path}`);
    } catch (e) { notify(errorText(e), true); } finally { setBusy(null); }
  };
  return (
    <section className="panel" aria-label="Storage audit and diagnostics">
      <h2>Storage audit &amp; diagnostics</h2>
      <p className="muted small" style={{ marginTop: -4 }}>
        Shows how much space each NoorVox folder uses, and finds NoorVox data in places your settings don't point to (for example a
        leftover first run with default paths). The audit only reads; nothing is moved or deleted.
      </p>
      <div className="row">
        <button className="btn" onClick={audit} disabled={!!busy}>{busy === "audit" && <Spinner size={14} />} Run storage audit</button>
        <button className="btn" onClick={bundle} disabled={!!busy} title="Versions, settings, storage audit, models, workers and recent logs. Never scripts or audio.">
          {busy === "bundle" && <Spinner size={14} />} Create support bundle…
        </button>
      </div>
      {report && (
        <div className="stack" style={{ marginTop: 12, gap: 8 }}>
          <table className="audit">
            <thead><tr><th>Location</th><th>Path</th><th>Used</th><th>Files</th><th>Free on drive</th></tr></thead>
            <tbody>
              {report.paths.map((p) => (
                <tr key={p.kind}>
                  <td>{p.kind}{p.onSystemDrive && <span className="pill" style={{ marginLeft: 6 }} title="On the Windows system drive (allowed)">system drive</span>}</td>
                  <td className="mono">{p.path}{!p.exists && <span className="muted"> (not created yet)</span>}</td>
                  <td>{fmtBytes(p.bytes)}</td><td>{p.files ?? "—"}</td><td>{fmtBytes(p.freeBytes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {report.strays.length > 0 ? report.strays.map((s) => (
            <p key={s.path} className="note err" style={{ margin: 0 }}>
              Stray NoorVox data: <span className="mono">{s.path}</span> · {fmtBytes(s.bytes)} in {s.files} files ({s.note}). Your current settings don't use it; delete it yourself if you don't need it.
            </p>
          )) : <p className="note" style={{ margin: 0 }}>✔ No NoorVox data found outside your configured folders.</p>}
          {report.notes.map((n) => <p key={n} className="muted small" style={{ margin: 0 }}>{n}</p>)}
        </div>
      )}
    </section>
  );
}
