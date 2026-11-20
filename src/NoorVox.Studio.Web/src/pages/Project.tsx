import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { BridgeError, call, errorText, on } from "../bridge";
import { Confirm, Progress } from "../components/Common";
import { Alert, Check, Download, Folder, Pause, Play, Refresh, Spinner, Stop, Upload } from "../components/Icons";
import { fmtDuration, plural } from "../format";
import { useStudio } from "../studio";
import type { GenerationJob, JobProgressEvent, ProjectOverview, SegmentStatusId, SegmentView, TakeView, VoicesResponse } from "../types";

const STATUS: Record<SegmentStatusId, { label: string; cls: string; icon: string; hint: string }> = {
  approved: { label: "Approved", cls: "ok", icon: "✔", hint: "The approved take matches the current text and voice." },
  generated: { label: "Ready to review", cls: "accent", icon: "●", hint: "A take exists for the current text but isn't approved yet." },
  stale: { label: "Changed since approval", cls: "warn", icon: "⚠", hint: "The text, voice or speed changed after approval. The approved take is kept until you choose." },
  missing: { label: "Not generated", cls: "", icon: "○", hint: "No take matches the current text and voice." },
  empty: { label: "Empty", cls: "", icon: "·", hint: "Nothing to speak." },
};

type Scope = "scene" | "section" | "project";

export function Project() {
  const { projectPath, notify, go, refreshWorkers } = useStudio();
  const [ov, setOv] = useState<ProjectOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sceneId, setSceneId] = useState<string | null>(null);
  const [voices, setVoices] = useState<VoicesResponse | null>(null);
  const [running, setRunning] = useState<JobProgressEvent | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [playing, setPlaying] = useState<string | null>(null);
  const [confirmExport, setConfirmExport] = useState<{ missing: number; sectionId: string | null } | null>(null);
  const audio = useRef(new Audio());
  const reloadTimer = useRef<number | undefined>(undefined);

  const load = useCallback(async () => {
    if (!projectPath) return;
    try {
      const r = await call<{ overview: ProjectOverview; running: boolean }>("projects.overview", { path: projectPath });
      setOv(r.overview);
      setError(null);
      setSceneId((cur) => cur && r.overview.sections.some((s) => s.scenes.some((x) => x.sceneId === cur)) ? cur : r.overview.sections[0]?.scenes[0]?.sceneId ?? null);
    } catch (e) { setError(errorText(e)); }
  }, [projectPath]);

  const reloadSoon = useCallback(() => {
    window.clearTimeout(reloadTimer.current);
    reloadTimer.current = window.setTimeout(load, 300);
  }, [load]);

  useEffect(() => { load(); call<VoicesResponse>("voices.list").then(setVoices, () => undefined); }, [load]);

  useEffect(() => {
    const offs = [
      on<JobProgressEvent>("job.progress", (p) => { if (p.path === ov?.root) { setRunning(p); if (p.lastItem) reloadSoon(); } }),
      on<{ path: string; message: string }>("job.status", (s) => { if (s.path === ov?.root) setStatus(s.message); }),
      on<{ path: string; job: GenerationJob; error?: { message: string } }>("job.finished", (f) => {
        if (f.path !== ov?.root) return;
        setRunning(null); setStatus(null); load(); refreshWorkers();
        if (f.error) notify(f.error.message, f.error.message.startsWith("Stopped") ? false : true);
        else notify(`Done: ${f.job.done}/${f.job.total} segment(s)${f.job.failedCount ? `, ${f.job.failedCount} failed` : ""}.`, f.job.failedCount > 0);
      }),
    ];
    return () => offs.forEach((o) => o());
  }, [ov?.root, load, reloadSoon, notify, refreshWorkers]);

  useEffect(() => {
    const a = audio.current;
    const end = () => setPlaying(null);
    a.addEventListener("ended", end);
    return () => { a.removeEventListener("ended", end); a.pause(); };
  }, []);

  const play = useCallback((key: string, url: string) => {
    const a = audio.current;
    if (playing === key) { a.pause(); setPlaying(null); return; }
    a.src = url;
    a.play().then(() => setPlaying(key), (e) => { if (!(e instanceof DOMException && e.name === "NotAllowedError")) notify(errorText(e), true); });
  }, [playing, notify]);

  const section = useMemo(() => ov?.sections.find((s) => s.scenes.some((x) => x.sceneId === sceneId)), [ov, sceneId]);
  const scene = section?.scenes.find((x) => x.sceneId === sceneId);

  if (!projectPath) {
    return <div className="empty"><p>No project is open.</p><button className="btn primary" onClick={() => go("projects")}>Go to projects</button></div>;
  }
  if (error) return <div className="empty"><p className="note err">{error}</p><button className="btn" onClick={() => go("projects")}>Back to projects</button></div>;
  if (!ov) return <div className="empty"><Spinner /> Loading project…</div>;

  const k = ov.kind;
  const total = ov.segmentCount - (ov.counts.empty ?? 0);
  const approved = ov.counts.approved ?? 0;

  const generate = async (scope: Scope, policy: "missing" | "all", approve: boolean, segmentIds?: string[]) => {
    try {
      const args = {
        path: ov.root, policy, approve,
        sectionId: scope === "section" ? section?.sectionId : null,
        sceneId: scope === "scene" ? sceneId : null,
        segmentIds: segmentIds ?? null,
      };
      const r = await call<{ jobId: string; total: number }>("projects.generate", args);
      setRunning({ path: ov.root, jobId: r.jobId, state: "running", done: 0, total: r.total, failed: 0, currentSegmentId: null, lastItem: null });
    } catch (e) { notify(errorText(e), true); }
  };
  const resume = async (jobId: string) => {
    try {
      const r = await call<{ jobId: string; total: number }>("projects.resume", { path: ov.root, jobId });
      setRunning({ path: ov.root, jobId: r.jobId, state: "running", done: 0, total: r.total, failed: 0, currentSegmentId: null, lastItem: null });
    } catch (e) { notify(errorText(e), true); }
  };
  const stop = (now: boolean) => call("projects.stop", { path: ov.root, now }).then(() => setStatus(now ? "Stopping now…" : "Stopping after the current segment…"));

  const importScript = async () => {
    try {
      const r = await call<{ cancelled: boolean; sections?: { sectionId: string; title: string }[]; firstSceneId?: string }>("projects.import", { path: ov.root });
      if (r.cancelled) return;
      notify(`Imported ${plural(r.sections!.length, k.sectionLabel)}. The original file is kept in references/.`);
      await load();
      if (r.firstSceneId) setSceneId(r.firstSceneId);
    } catch (e) { notify(errorText(e), true); }
  };

  const exportClips = async (sectionId: string | null, allowMissing: boolean) => {
    try {
      const r = await call<{ cancelled: boolean; result?: { outputDir: string; clips: unknown[]; missing: string[]; stale: string[] } }>("projects.export", { path: ov.root, sectionId, allowMissing });
      if (r.cancelled) return;
      notify(`Exported ${r.result!.clips.length} clip(s) to ${r.result!.outputDir}${r.result!.stale.length ? ` (${r.result!.stale.length} approved take(s) are out of date)` : ""}.`);
    } catch (e) {
      if (e instanceof BridgeError && e.code === "invalid_input" && /no approved take/.test(e.message)) {
        const d = e.details as { total?: number } | undefined;
        setConfirmExport({ missing: d?.total ?? 0, sectionId });
      } else notify(errorText(e), true);
    }
  };

  const lastJob = ov.recentJobs[0];
  const resumable = ov.recentJobs.find((j) => j.state === "interrupted" || (j.failedCount > 0 && j.remaining + j.failedCount > 0));

  return (
    <>
      <div className="page-head project-head">
        <div style={{ minWidth: 0 }}>
          <div className="row" style={{ gap: 10 }}>
            <h1 style={{ margin: 0 }}>{ov.project.title}</h1>
            <span className="pill accent">{k.displayName}</span>
          </div>
          <p className="muted" style={{ margin: "4px 0 0" }}>
            {plural(ov.sections.length, k.sectionLabel)} · {plural(total, k.segmentLabel)} · {approved} approved · {fmtDuration(ov.approvedDurationMs)} of approved audio
          </p>
        </div>
        <div className="row">
          <button className="btn" onClick={importScript}><Upload size={16} /> Import script…</button>
          <button className="btn" onClick={() => exportClips(null, false)} disabled={approved === 0}><Download size={16} /> Export clips…</button>
          <button className="btn" onClick={() => call("projects.reveal", { path: ov.root })} aria-label="Open project folder"><Folder size={16} /></button>
        </div>
      </div>
      <div className="bar" style={{ marginBottom: 14 }} role="progressbar" aria-label="Approved" aria-valuenow={total ? Math.round(approved * 100 / total) : 0}>
        <i style={{ width: `${total ? (approved * 100) / total : 0}%`, background: "var(--ok)" }} />
      </div>
      {ov.warnings.length > 0 && <p className="note err" style={{ marginBottom: 12 }}>{ov.warnings.slice(0, 3).join(" · ")}{ov.warnings.length > 3 ? ` · +${ov.warnings.length - 3} more` : ""}</p>}

      {ov.sections.length === 0 ? (
        <div className="empty">
          <p style={{ fontSize: 16, color: "var(--text)" }}>This project is empty.</p>
          <p>Import a script: Markdown/text (<span className="mono"># {k.sectionLabel}</span>, <span className="mono">## {k.sceneLabel}</span>, paragraphs become {k.segmentLabel.toLowerCase()}s)
            {k.id === "dialogue" && <> or a CSV/JSON with <span className="mono">line_id, character, text</span></>}.</p>
          <button className="btn primary" onClick={importScript}><Upload size={16} /> Import script…</button>
        </div>
      ) : (
        <div className="workspace">
          <nav className="panel tree" aria-label={`${k.sectionLabel}s`}>
            {ov.sections.map((s) => {
              const segs = s.scenes.flatMap((x) => x.segments).filter((x) => x.status !== "empty");
              const ok = segs.filter((x) => x.status === "approved").length;
              return (
                <div key={s.sectionId} className="tree-section">
                  <div className="tree-title"><span>{s.title}</span><span className="muted small">{ok}/{segs.length}</span></div>
                  {s.scenes.map((sc) => {
                    const done = sc.segments.filter((x) => x.status === "approved").length;
                    const all = sc.segments.filter((x) => x.status !== "empty").length;
                    return (
                      <button key={sc.sceneId} className="tree-scene" aria-current={sc.sceneId === sceneId ? "true" : undefined} onClick={() => setSceneId(sc.sceneId)}>
                        <span>{sc.title}</span>
                        <span className={`small ${done === all && all > 0 ? "ok-text" : "muted"}`}>{done === all && all > 0 ? "✔" : `${done}/${all}`}</span>
                      </button>
                    );
                  })}
                </div>
              );
            })}
          </nav>

          <section className="scene" aria-label={scene?.title}>
            {scene && section && (
              <>
                <div className="scene-head">
                  <div>
                    <span className="muted small">{section.title}</span>
                    <h2 style={{ margin: 0, fontSize: 19, textTransform: "none", letterSpacing: 0, color: "var(--text)" }}>{scene.title}</h2>
                  </div>
                  <span className="muted small">{plural(scene.segments.length, k.segmentLabel)}</span>
                </div>
                {scene.segments.map((seg) => (
                  <SegmentCard key={seg.segmentId} seg={seg} ov={ov} voices={voices} busy={!!running}
                    playing={playing} onPlay={play} onChanged={reloadSoon}
                    onGenerate={() => generate("scene", "all", false, [seg.segmentId])} />
                ))}
                <button className="btn small" onClick={async () => {
                  try { await call("projects.addSegment", { path: ov.root, sceneId: scene.sceneId, afterSegmentId: null }); load(); } catch (e) { notify(errorText(e), true); }
                }}>＋ Add {k.segmentLabel.toLowerCase()}</button>
              </>
            )}
          </section>

          <aside className="stack side-panel" aria-label="Cast and generation">
            <GeneratePanel ov={ov} sectionTitle={section?.title} sceneTitle={scene?.title} running={running} status={status}
              onGenerate={generate} onStop={stop} lastJob={lastJob} resumable={running ? undefined : resumable} onResume={resume} />
            <CastPanel ov={ov} voices={voices} onChanged={load} />
          </aside>
        </div>
      )}

      {confirmExport && (
        <Confirm title="Some segments aren't approved" confirmText="Export approved only" onCancel={() => setConfirmExport(null)}
          onConfirm={() => { const c = confirmExport; setConfirmExport(null); exportClips(c.sectionId, true); }}>
          {confirmExport.missing} segment(s) have no approved take and will be skipped. Export the approved ones anyway?
        </Confirm>
      )}
    </>
  );
}

function SegmentCard({ seg, ov, voices, busy, playing, onPlay, onChanged, onGenerate }: {
  seg: SegmentView; ov: ProjectOverview; voices: VoicesResponse | null; busy: boolean; playing: string | null;
  onPlay: (key: string, url: string) => void; onChanged: () => void; onGenerate: () => void;
}) {
  const { notify } = useStudio();
  const [text, setText] = useState(seg.text);
  const [saving, setSaving] = useState(false);
  const [open, setOpen] = useState(false);
  const [takes, setTakes] = useState<TakeView[] | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const saveTimer = useRef<number | undefined>(undefined);
  const lastSaved = useRef(seg.text);

  useEffect(() => { if (seg.text !== lastSaved.current) { setText(seg.text); lastSaved.current = seg.text; } }, [seg.text]);

  const loadTakes = useCallback(async () => {
    try { setTakes(await call<TakeView[]>("projects.takes", { path: ov.root, segmentId: seg.segmentId })); } catch (e) { notify(errorText(e), true); }
  }, [ov.root, seg.segmentId, notify]);
  useEffect(() => { if (open) loadTakes(); }, [open, seg.takes, seg.approvedTakeId, loadTakes]);

  const save = useCallback(async (patch: Record<string, unknown>) => {
    setSaving(true);
    try { await call("projects.updateSegment", { path: ov.root, segmentId: seg.segmentId, ...patch }); onChanged(); }
    catch (e) { notify(errorText(e), true); }
    finally { setSaving(false); }
  }, [ov.root, seg.segmentId, onChanged, notify]);

  const scheduleSave = (value: string) => {
    setText(value);
    window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => { if (value !== lastSaved.current) { lastSaved.current = value; save({ text: value }); } }, 900);
  };
  const flush = () => {
    window.clearTimeout(saveTimer.current);
    if (text !== lastSaved.current) { lastSaved.current = text; save({ text }); }
  };

  const playUrl = (takeId: string) => `https://takes.noorvox.local/${encodeURIComponent(seg.segmentId)}/${encodeURIComponent(takeId)}.wav`;
  const mainTake = seg.approvedTakeId ?? seg.latestTakeId;
  const st = STATUS[seg.status];
  const showSpeaker = ov.kind.usesSpeakers || ov.project.speakers.length > 1;
  const voiceName = voices?.voices.find((v) => v.id === seg.effectiveVoice)?.displayName ?? seg.effectiveVoice;

  return (
    <article className={`segment ${seg.status}`} aria-label={seg.segmentId}>
      <div className="segment-top">
        <span className="mono muted small" title="Stable id: used for take folders and exported file names">{seg.segmentId}</span>
        {showSpeaker && (
          <select className="speaker-select" value={seg.speakerId ?? ""} aria-label="Speaker" onChange={(e) => save({ speakerId: e.target.value })}>
            <option value="">(no speaker)</option>
            {ov.project.speakers.map((s) => <option key={s.speakerId} value={s.speakerId}>{s.name}</option>)}
          </select>
        )}
        <span className="muted small">{voiceName} · {seg.effectiveSpeed.toFixed(2)}×</span>
        <span className={`pill ${st.cls}`} title={st.hint} style={{ marginLeft: "auto" }}>{st.icon} {st.label}</span>
      </div>
      <textarea value={text} onChange={(e) => scheduleSave(e.target.value)} onBlur={flush} rows={Math.min(8, Math.max(2, Math.ceil(text.length / 95)))}
        aria-label={`Text of ${seg.segmentId}`} placeholder="Type what should be spoken…" />
      {seg.notes && <div className="seg-notes">📝 {seg.notes}</div>}
      {seg.spokenText && seg.spokenText !== seg.text && <div className="seg-notes">🔊 Spoken as: {seg.spokenText}</div>}
      <div className="segment-actions">
        <button className="btn small" disabled={!mainTake} onClick={() => mainTake && onPlay(`${seg.segmentId}/${mainTake}`, playUrl(mainTake))}
          aria-label={seg.approvedTakeId ? "Play approved take" : "Play latest take"}>
          {playing === `${seg.segmentId}/${mainTake}` ? <Pause size={14} /> : <Play size={14} />} {seg.approvedTakeId ? "Approved" : "Latest"}{seg.durationMs ? ` · ${fmtDuration(seg.durationMs)}` : ""}
        </button>
        <button className="btn small" disabled={busy || seg.status === "empty"} onClick={() => { flush(); onGenerate(); }} title="Always creates a new take; approvals are never replaced automatically">
          <Refresh size={14} /> {seg.takes ? "New take" : "Generate"}
        </button>
        <button className="btn small ghost" onClick={() => setOpen((o) => !o)} aria-expanded={open}>Takes ({seg.takes})</button>
        {saving && <span className="muted small"><Spinner size={12} /> saving</span>}
        <button className="btn small ghost danger" style={{ marginLeft: "auto" }} onClick={() => setConfirmDelete(true)} aria-label="Remove segment">Remove</button>
      </div>
      {open && (
        <div className="takes-list">
          {takes === null && <span className="muted small">Loading…</span>}
          {takes?.length === 0 && <span className="muted small">No takes yet.</span>}
          {takes?.map((t) => (
            <div key={t.takeId} className={`take-row ${t.approved ? "approved" : ""}`}>
              <button className="btn small" onClick={() => onPlay(`${seg.segmentId}/${t.takeId}`, t.mediaUrl)} aria-label={`Play take ${t.takeId}`}>
                {playing === `${seg.segmentId}/${t.takeId}` ? <Pause size={13} /> : <Play size={13} />}
              </button>
              <span className="mono small">{t.takeId}</span>
              <span className="small">{fmtDuration(t.durationMs)} · {voices?.voices.find((v) => v.id === t.voiceId)?.displayName ?? t.voiceId} · {t.speed.toFixed(2)}×</span>
              <span className={`small ${t.matchesCurrent ? "ok-text" : "muted"}`}>{t.matchesCurrent ? "matches current text" : "older text/voice"}</span>
              {t.approved
                ? <span className="pill ok" style={{ marginLeft: "auto" }}><Check size={12} /> Approved</span>
                : <button className="btn small primary" style={{ marginLeft: "auto" }} onClick={async () => {
                    try { await call("projects.approve", { path: ov.root, segmentId: seg.segmentId, takeId: t.takeId }); onChanged(); } catch (e) { notify(errorText(e), true); }
                  }}>Approve</button>}
            </div>
          ))}
          {seg.approvedTakeId && <button className="btn small ghost" onClick={async () => {
            try { await call("projects.approve", { path: ov.root, segmentId: seg.segmentId, takeId: null }); onChanged(); } catch (e) { notify(errorText(e), true); }
          }}>Clear approval</button>}
        </div>
      )}
      {confirmDelete && (
        <Confirm title={`Remove ${seg.segmentId}?`} confirmText="Remove" danger onCancel={() => setConfirmDelete(false)}
          onConfirm={async () => { setConfirmDelete(false); try { await call("projects.removeSegment", { path: ov.root, segmentId: seg.segmentId }); onChanged(); } catch (e) { notify(errorText(e), true); } }}>
          The segment leaves the script. Its {seg.takes} take file(s) stay in the project's takes folder.
        </Confirm>
      )}
    </article>
  );
}

function GeneratePanel({ ov, sectionTitle, sceneTitle, running, status, onGenerate, onStop, lastJob, resumable, onResume }: {
  ov: ProjectOverview; sectionTitle?: string; sceneTitle?: string; running: JobProgressEvent | null; status: string | null;
  onGenerate: (scope: Scope, policy: "missing" | "all", approve: boolean) => void; onStop: (now: boolean) => void;
  lastJob?: GenerationJob; resumable?: GenerationJob; onResume: (jobId: string) => void;
}) {
  const [scope, setScope] = useState<Scope>("scene");
  const [approve, setApprove] = useState(true);
  const [all, setAll] = useState(false);
  const k = ov.kind;
  return (
    <section className="panel">
      <h2>Generate</h2>
      {running ? (
        <div className="stack" style={{ gap: 8 }}>
          <Progress value={running.done + running.failed} total={running.total} label="Job progress" />
          <span className="small">{running.done + running.failed} of {running.total} done{running.failed ? ` · ${running.failed} failed` : ""}</span>
          <span className="muted small" aria-live="polite">{running.currentSegmentId ? `Now: ${running.currentSegmentId}` : status ?? "Starting…"}</span>
          <div className="row">
            <button className="btn small" onClick={() => onStop(false)}><Pause size={13} /> Stop after this one</button>
            <button className="btn small danger" onClick={() => onStop(true)}><Stop size={13} /> Stop now</button>
          </div>
          <p className="muted small" style={{ margin: 0 }}>Every finished segment is saved immediately. A stopped job can be resumed.</p>
        </div>
      ) : (
        <div className="stack" style={{ gap: 10 }}>
          <div role="radiogroup" aria-label="Scope" className="stack" style={{ gap: 4 }}>
            {([["scene", `${k.sceneLabel}: ${sceneTitle ?? "—"}`], ["section", `${k.sectionLabel}: ${sectionTitle ?? "—"}`], ["project", "Whole project"]] as [Scope, string][]).map(([v, label]) => (
              <label key={v} className="radio"><input type="radio" name="scope" checked={scope === v} onChange={() => setScope(v)} /> {label}</label>
            ))}
          </div>
          <label className="radio"><input type="checkbox" checked={approve} onChange={(e) => setApprove(e.target.checked)} /> Approve new takes where nothing is approved yet</label>
          <label className="radio"><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} /> New takes even if a matching take exists</label>
          <button className="btn primary big" onClick={() => onGenerate(scope, all ? "all" : "missing", approve)}><Play /> Generate {all ? "all" : "missing"}</button>
          {resumable && (
            <div className="note">
              <Alert size={14} /> Job {resumable.jobId} stopped at {resumable.done}/{resumable.total}.
              <button className="btn small" style={{ marginLeft: 8 }} onClick={() => onResume(resumable.jobId)}>Resume</button>
            </div>
          )}
          {lastJob && !resumable && <span className="muted small">Last job: {lastJob.state}, {lastJob.done}/{lastJob.total}{lastJob.finishedAt ? ` · ${new Date(lastJob.finishedAt).toLocaleTimeString()}` : ""}</span>}
        </div>
      )}
    </section>
  );
}

function CastPanel({ ov, voices, onChanged }: { ov: ProjectOverview; voices: VoicesResponse | null; onChanged: () => void }) {
  const { notify } = useStudio();
  const cast = async (speakerId: string | null, voiceId: string) => {
    try { await call("projects.cast", { path: ov.root, speakerId, voiceId }); onChanged(); notify("Voice changed. Affected segments now show as changed until regenerated."); }
    catch (e) { notify(errorText(e), true); }
  };
  const VoiceSelect = ({ value, onPick, label }: { value: string; onPick: (v: string) => void; label: string }) => (
    <select value={value} aria-label={label} onChange={(e) => onPick(e.target.value)}>
      {!value && <option value="">(project default)</option>}
      {voices?.voices.map((v) => <option key={v.id} value={v.id}>{v.displayName} · {v.languageName} · {v.gender}</option>)}
    </select>
  );
  return (
    <section className="panel">
      <h2>{ov.kind.usesSpeakers ? "Cast" : "Voice"}</h2>
      <label className="field">Default voice</label>
      <VoiceSelect label="Default voice" value={ov.project.defaults.voiceId ?? voices?.defaultVoice ?? ""} onPick={(v) => cast(null, v)} />
      {ov.project.speakers.filter((s) => ov.kind.usesSpeakers || s.speakerId !== "narrator" || s.voiceId).map((s) => (
        <div key={s.speakerId} style={{ marginTop: 10 }}>
          <label className="field">{s.name}</label>
          <VoiceSelect label={`Voice for ${s.name}`} value={s.voiceId ?? ""} onPick={(v) => cast(s.speakerId, v)} />
        </div>
      ))}
      <p className="muted small" style={{ marginBottom: 0 }}>Changing a voice never touches approved takes: affected segments are marked as changed until you regenerate and approve.</p>
    </section>
  );
}
