import { useState } from "react";
import { call, errorText } from "../bridge";
import { Confirm, StateBadge } from "../components/Common";
import { Play, Refresh, Shield, Spinner, Stop } from "../components/Icons";
import { fmtBytes, fmtUptime, shortHash } from "../format";
import { useStudio } from "../studio";
import type { WorkerStatus } from "../types";

type Pending = { kind: "force"; w: WorkerStatus } | { kind: "stopAll" } | null;

export function Monitor() {
  const { workers, cpu, refreshWorkers, notify, info, go } = useStudio();
  const [busy, setBusy] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<Pending>(null);

  const run = async (label: string, fn: () => Promise<unknown>, done: string) => {
    setBusy(label);
    try { await fn(); notify(done); } catch (e) { notify(errorText(e), true); } finally { setBusy(null); refreshWorkers(); }
  };

  const start = () => run("start", () => call("workers.start"), "Voice engine is ready.");
  const unload = (w: WorkerStatus) => run(w.workerId, () => call("workers.unload", { workerId: w.workerId }), `Model unloaded from '${w.workerId}' (worker restarted to free memory).`);
  const stop = (w: WorkerStatus, force = false) =>
    run(w.workerId, () => call("workers.stop", { workerId: w.workerId, force }), force ? `Worker '${w.workerId}' terminated.` : `Worker '${w.workerId}' stopped.`);
  const stopAll = () => run("all", () => call("workers.stopAll", { force: false }), "All NoorVox workers stopped.");

  const studioRunning = workers.some((w) => w.workerId === "studio");

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Runtime monitor</h1>
          <p>What is actually running right now: process, model, memory and current work. Only workers NoorVox started are shown or touched.</p>
        </div>
        <div className="row">
          <button className="btn" onClick={() => refreshWorkers()}><Refresh size={16} /> Refresh</button>
          {!studioRunning && <button className="btn primary" onClick={start} disabled={!!busy || !info?.hasModel}>{busy === "start" ? <Spinner size={16} /> : <Play size={16} />} Start voice engine</button>}
          <button className="btn danger" onClick={() => setConfirm({ kind: "stopAll" })} disabled={!!busy || workers.length === 0}><Stop size={16} /> Stop all</button>
        </div>
      </div>

      <p className="note" style={{ marginBottom: 14, display: "flex", gap: 8, alignItems: "center" }}>
        <Shield size={16} />
        <span>
          {info?.settings.stopOnExit
            ? "Engines started by the studio stop automatically when you close NoorVox Studio, even if it crashes."
            : "Engines started by the studio keep running after you close it, until they've been idle for the timeout or you stop them."}{" "}
          <a href="#/settings" onClick={(e) => { e.preventDefault(); go("settings"); }}>Change in Settings</a>
        </span>
      </p>

      {workers.length === 0 && (
        <div className="empty">
          <p style={{ fontSize: 16, color: "var(--text)", margin: "0 0 6px" }}>No voice engine is running.</p>
          <p style={{ margin: 0 }}>Nothing is using memory or CPU. The engine starts automatically when you generate, or you can start it now.</p>
        </div>
      )}

      <div className="stack">
        {workers.map((w) => {
          const isBusy = busy === w.workerId;
          const owner = w.launchedBy === "studio" ? "Started by NoorVox Studio" : w.launchedBy === "cli" ? "Started from the command line" : `Started by ${w.launchedBy ?? "NoorVox"}`;
          const lifetime = w.boundToLauncher ? "ends with the program that started it" : "persistent";
          const jobSecs = w.currentJob ? (Date.now() - Date.parse(w.currentJob.startedAt)) / 1000 : 0;
          return (
            <section key={w.workerId} className="panel worker-card" aria-label={`Worker ${w.workerId}`}>
              <div style={{ minWidth: 0 }}>
                <div className="row" style={{ marginBottom: 6 }}>
                  <b style={{ fontSize: 16 }}>{w.workerId === "studio" ? "Voice engine" : w.workerId}</b>
                  <span className="pill">{w.backend === "kokoro-onnx" ? "Kokoro (ONNX)" : w.backend ?? "worker"}</span>
                </div>
                <StateBadge state={w.state} />
                <div className="muted small" style={{ marginTop: 6 }}>{owner} · {lifetime}</div>
                <div className="muted small">PID <span className="mono">{w.pid ?? "—"}</span> · up {fmtUptime(w.uptimeSeconds)}</div>
                {w.error && <p className="note err" style={{ marginTop: 8 }}>{w.error}</p>}
              </div>

              <div className="metrics">
                <div className="metric"><span>Model</span><b title={w.loadedModel?.modelSha256}>{w.loadedModel ? `Kokoro ${w.loadedModel.version}` : "none loaded"}</b></div>
                <div className="metric"><span>Memory</span><b>{fmtBytes(w.resources?.workingSetBytes)}</b></div>
                <div className="metric"><span>CPU</span><b>{cpu[w.workerId] != null ? `${cpu[w.workerId].toFixed(0)} %` : "measuring…"}</b></div>
                <div className="metric"><span>Provider</span><b>{w.executionProvider || "—"}</b></div>
                <div className="metric"><span>GPU</span><b title={w.resources?.gpu}>unavailable</b></div>
                <div className="metric"><span>Jobs done</span><b>{w.jobsCompleted ?? "—"}</b></div>
                <div className="metric" style={{ gridColumn: "1 / -1" }}>
                  <span>Current work</span>
                  <b>{w.currentJob ? `Synthesizing ${w.currentJob.textChars} characters · ${w.currentJob.voiceId} · ${jobSecs.toFixed(0)} s` : w.state === "loading" ? "Loading model…" : "Idle"}</b>
                </div>
                <div className="muted small" style={{ gridColumn: "1 / -1" }}>
                  {w.loadedModel && <>Model sha256 <span className="mono">{shortHash(w.loadedModel.modelSha256)}</span> · loaded in {(w.loadedModel.loadMs / 1000).toFixed(1)} s · </>}
                  {w.idleTimeoutSeconds ? `exits after ${Math.round(w.idleTimeoutSeconds / 60)} min idle` : "no idle timeout"}
                  {w.lastHeartbeatAt && <> · last answer {new Date(w.lastHeartbeatAt).toLocaleTimeString()}</>}
                </div>
              </div>

              <div className="worker-actions">
                {w.loadedModel && w.state !== "generating" && (
                  <button className="btn small" disabled={isBusy} onClick={() => unload(w)} title="Restarts the worker process: the only way to really free the model's memory">Unload model</button>
                )}
                {w.state !== "error" && (
                  <button className="btn small" disabled={isBusy} onClick={() => stop(w)} title="Finishes the current job, then exits">{isBusy ? <Spinner size={14} /> : <Stop size={14} />} Stop</button>
                )}
                <button className="btn small danger" disabled={isBusy} onClick={() => (w.state === "error" ? stop(w) : setConfirm({ kind: "force", w }))}>
                  {w.state === "error" ? "Clear" : "Force stop"}
                </button>
              </div>
            </section>
          );
        })}
      </div>

      {confirm?.kind === "force" && (
        <Confirm title={`Force stop ${confirm.w.workerId === "studio" ? "the voice engine" : confirm.w.workerId}?`} confirmText="Terminate" danger
          onCancel={() => setConfirm(null)} onConfirm={() => { const w = confirm.w; setConfirm(null); stop(w, true); }}>
          {confirm.w.currentJob
            ? "It is generating right now. The unfinished audio will be discarded; nothing already saved is affected."
            : "The process will be terminated immediately. Nothing you've saved is affected."}
        </Confirm>
      )}
      {confirm?.kind === "stopAll" && (
        <Confirm title="Stop all NoorVox workers?" confirmText="Stop all" onCancel={() => setConfirm(null)} onConfirm={() => { setConfirm(null); stopAll(); }}>
          {workers.length} worker{workers.length === 1 ? "" : "s"} will finish current work and exit, including any started from the command line.
        </Confirm>
      )}
    </>
  );
}
