import { useCallback, useEffect, useState } from "react";
import { call } from "../bridge";
import { Check, Download, Logo } from "../components/Icons";
import { fmtBytes } from "../format";
import { useStudio } from "../studio";
import type { ModelsOverview } from "../types";
import { InstallStatus, useInstall } from "./Models";
import { StoragePaths } from "./Settings";

/** First run: three short, skippable steps (spec §13.1). */
export function Setup() {
  const { go, info, refreshInfo } = useStudio();
  const [step, setStep] = useState(0);
  const [models, setModels] = useState<ModelsOverview | null>(null);
  const load = useCallback(() => { call<ModelsOverview>("models.overview").then(setModels, () => undefined); }, []);
  const { active, progress, run } = useInstall(load);
  useEffect(load, [load]);

  const recommended = models?.catalog.find((c) => c.recommended) ?? models?.catalog[0];
  const installed = (models?.installed.length ?? 0) > 0;
  const finish = async () => { await refreshInfo(); go("narrate"); };

  return (
    <div className="setup">
      <div className="hero">
        <div className="row" style={{ gap: 14 }}>
          <span style={{ color: "var(--accent-strong)" }}><Logo size={44} /></span>
          <div>
            <h1>Welcome to NoorVox</h1>
            <p className="muted" style={{ margin: 0 }}>Narration, audiobooks, game dialogue and quick voiceovers, generated on this PC.</p>
          </div>
        </div>
      </div>
      <div className="steps" aria-hidden="true">{[0, 1, 2].map((i) => <span key={i} className={i <= step ? "on" : ""} />)}</div>

      {step === 0 && (
        <section className="panel stack">
          <h2>1 · Where should NoorVox keep things?</h2>
          <p className="muted" style={{ margin: 0 }}>Defaults are fine. If you'd rather keep large files off your system drive, choose folders on another drive now, before downloading anything.</p>
          <StoragePaths kinds={["models", "cache", "temp", "exports"]} />
          <div className="row" style={{ justifyContent: "space-between" }}>
            <button className="btn ghost" onClick={finish}>Skip setup</button>
            <button className="btn primary" onClick={() => setStep(1)}>Continue</button>
          </div>
        </section>
      )}

      {step === 1 && (
        <section className="panel stack">
          <h2>2 · Install a voice</h2>
          {installed ? (
            <p style={{ margin: 0 }}><span className="pill ok"><Check size={13} /> Installed</span> {models!.installed[0].displayName} with {models!.installed[0].voices} voices is ready.</p>
          ) : recommended ? (
            <>
              <div>
                <b style={{ fontSize: 16 }}>{recommended.displayName}</b> <span className="pill accent">Recommended</span>
                <p className="muted" style={{ margin: "6px 0" }}>{recommended.description}</p>
                <p className="muted small" style={{ margin: 0 }}>
                  One-time download of {fmtBytes(recommended.downloadBytes)} into <span className="mono">{models?.modelsDir}</span>, verified file by file.
                  License: {recommended.license}. After this, no internet is needed.
                </p>
              </div>
              {active ? <InstallStatus progress={progress} /> : (
                <button className="btn primary big" onClick={() => run("models.install", `${recommended.id}@${recommended.version}`, `${recommended.displayName} is ready.`)}>
                  <Download /> Install {recommended.displayName}
                </button>
              )}
            </>
          ) : <p className="muted">Loading…</p>}
          <div className="row" style={{ justifyContent: "space-between" }}>
            <button className="btn ghost" onClick={() => setStep(0)} disabled={!!active}>Back</button>
            <button className="btn primary" onClick={() => setStep(2)} disabled={!!active}>{installed ? "Continue" : "Skip for now"}</button>
          </div>
        </section>
      )}

      {step === 2 && (
        <section className="panel stack">
          <h2>3 · You're set</h2>
          <p style={{ margin: 0 }}>
            {installed ? "Write something and press Generate. The voice engine starts on demand and " + (info?.settings.stopOnExit ? "stops when you close the studio." : "keeps running in the background as configured.")
              : "You can install a voice any time from Models & voices. Until then you can still look around."}
          </p>
          <div className="row" style={{ justifyContent: "flex-end" }}>
            <button className="btn primary" onClick={finish}>Start narrating</button>
          </div>
        </section>
      )}
    </div>
  );
}
