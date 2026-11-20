import { useCallback, useEffect, useState } from "react";
import { call, errorText, on } from "../bridge";
import { Progress } from "../components/Common";
import { Check, Download, Folder, Shield, Spinner } from "../components/Icons";
import { fmtBytes, shortHash } from "../format";
import { useStudio } from "../studio";
import type { InstallProgress, ModelsOverview } from "../types";

export function useInstall(onDone: () => void) {
  const { notify, refreshInfo } = useStudio();
  const [active, setActive] = useState<string | null>(null);
  const [progress, setProgress] = useState<InstallProgress | null>(null);

  useEffect(() => on<InstallProgress>("install.progress", (p) => setProgress(p.stage === "done" ? null : p)), []);

  const run = useCallback(async (method: string, id: string, success: string) => {
    setActive(id);
    try {
      await call(method, { id });
      notify(success);
      await refreshInfo();
    } catch (e) {
      notify(errorText(e), true);
    } finally {
      setActive(null);
      setProgress(null);
      onDone();
    }
  }, [notify, refreshInfo, onDone]);

  return { active, progress, run };
}

export function InstallStatus({ progress }: { progress: InstallProgress | null }) {
  if (!progress) return <Progress label="Working" />;
  const pct = progress.total ? Math.round(((progress.done ?? 0) / progress.total) * 100) : null;
  const label = progress.stage === "downloading" ? "Downloading" : progress.stage === "verifying" ? "Verifying" : progress.stage === "unpacking" ? "Unpacking" : progress.stage;
  return (
    <div className="stack" style={{ gap: 6 }}>
      <Progress value={progress.done} total={progress.total} label={label} />
      <span className="muted small">
        {label} {progress.file}
        {progress.stage === "downloading" && progress.total ? ` · ${fmtBytes(progress.done)} of ${fmtBytes(progress.total)} (${pct}%)` : pct != null ? ` · ${pct}%` : ""}
      </span>
    </div>
  );
}

export function Models() {
  const { notify } = useStudio();
  const [data, setData] = useState<ModelsOverview | null>(null);
  const load = useCallback(() => { call<ModelsOverview>("models.overview").then(setData, (e) => notify(errorText(e), true)); }, [notify]);
  const { active, progress, run } = useInstall(load);
  useEffect(load, [load]);

  const choose = async () => {
    try {
      const r = await call<{ cancelled: boolean; note?: string }>("storage.choose", { kind: "models" });
      if (!r.cancelled) { notify(r.note ?? "Models folder changed."); load(); }
    } catch (e) { notify(errorText(e), true); }
  };
  const openUrl = (url: string) => call("shell.openUrl", { url }).catch((e) => notify(errorText(e), true));
  const notInstalled = data?.catalog.filter((c) => !c.installed) ?? [];

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Models &amp; voices</h1>
          <p>Every download is checked against a known sha256 before it is used. Installed models work fully offline.</p>
        </div>
        <div className="row">
          <button className="btn" onClick={() => call("storage.reveal", { kind: "models" })}><Folder size={16} /> Open folder</button>
          <button className="btn" onClick={choose}>Change location…</button>
        </div>
      </div>
      {data && <p className="muted small" style={{ marginTop: -8, marginBottom: 14 }}>Models folder: <span className="mono">{data.modelsDir}</span> · downloads are staged in <span className="mono">{data.cacheDir}</span></p>}

      <div className="stack">
        {data?.installed.map((m) => {
          const cat = data.catalog.find((c) => c.id === m.id && c.updateAvailable);
          return (
            <section key={m.reference} className="panel model-card">
              <div className="model-title">
                <div>
                  <h3>{m.displayName}</h3>
                  <div className="row small muted" style={{ marginTop: 4 }}>
                    <span className="pill ok"><Check size={13} /> Installed</span>
                    <span>{m.voices} voices</span><span>·</span><span>{fmtBytes(m.sizeBytes)} on disk</span><span>·</span>
                    <a href="#" onClick={(e) => { e.preventDefault(); openUrl(m.licenseUrl); }}>{m.license}</a>
                  </div>
                </div>
                <div className="row">
                  {cat && <button className="btn primary small" disabled={!!active} onClick={() => run("models.install", `${cat.id}@${cat.version}`, `Updated to ${cat.displayName}.`)}>Update to {cat.version}</button>}
                  <button className="btn small" disabled={!!active} onClick={() => run("models.verify", m.reference, `${m.displayName}: all files verified.`)}>
                    {active === m.reference ? <Spinner size={14} /> : <Shield size={14} />} Verify files
                  </button>
                </div>
              </div>
              {active === m.reference && <InstallStatus progress={progress} />}
              <dl className="kv">
                <dt>Location</dt><dd className="mono">{m.path}</dd>
                <dt>Model sha256</dt><dd className="mono" title={m.modelSha256}>{shortHash(m.modelSha256)}</dd>
                <dt>Source</dt><dd><a href="#" onClick={(e) => { e.preventDefault(); openUrl(m.source); }}>{m.source.replace("https://", "")}</a> · {m.origin === "import" ? "imported" : "downloaded"} {new Date(m.installedAt).toLocaleDateString()}</dd>
              </dl>
              <div>
                <label className="field">Voice packs</label>
                <div className="stack" style={{ gap: 8 }}>
                  {m.voicePacks.map((p) => (
                    <div key={p.id} className="pack">
                      <div style={{ minWidth: 0 }}>
                        <b>{p.displayName}</b>
                        <div className="muted small">{p.description}</div>
                        {active === p.id && <div style={{ marginTop: 8 }}><InstallStatus progress={progress} /></div>}
                      </div>
                      {p.installed
                        ? <span className="pill ok"><Check size={13} /> {p.voices} voices</span>
                        : <button className="btn small primary" disabled={!!active} onClick={() => run("voices.installPack", p.id, `${p.displayName} installed.`)}><Download size={14} /> Install · {fmtBytes(p.downloadBytes)}</button>}
                    </div>
                  ))}
                  {m.voicePacks.length === 0 && <span className="muted small">No additional voice packs are available for this model yet.</span>}
                </div>
              </div>
            </section>
          );
        })}

        {notInstalled.length > 0 && <h2 style={{ fontSize: 16, margin: "8px 0 0" }}>Available to install</h2>}
        {notInstalled.map((c) => (
          <section key={`${c.id}@${c.version}`} className="panel model-card">
            <div className="model-title">
              <div>
                <h3>{c.displayName} {c.recommended && <span className="pill accent">Recommended</span>}</h3>
                <p className="muted" style={{ margin: "6px 0 0" }}>{c.description}</p>
                <div className="muted small" style={{ marginTop: 6 }}>{fmtBytes(c.downloadBytes)} download · <a href="#" onClick={(e) => { e.preventDefault(); openUrl(c.licenseUrl); }}>{c.license}</a></div>
              </div>
              <button className="btn primary" disabled={!!active} onClick={() => run("models.install", `${c.id}@${c.version}`, `${c.displayName} installed.`)}>
                {active === `${c.id}@${c.version}` ? <Spinner size={16} /> : <Download size={16} />} Install
              </button>
            </div>
            {active === `${c.id}@${c.version}` && <InstallStatus progress={progress} />}
          </section>
        ))}

        {data && <p className="muted small">Catalog {data.catalogVersion}. NoorVox never re-downloads installed files, and new versions install alongside old ones so earlier takes stay reproducible.</p>}
      </div>
    </>
  );
}
