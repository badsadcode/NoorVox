import { useCallback, useEffect, useRef, useState } from "react";
import { call, errorText } from "../bridge";
import { Confirm } from "../components/Common";
import { Download, Folder, Mic, Pause, Play, Shield, Spinner, Stop, Upload, Wave } from "../components/Icons";
import { fmtDuration } from "../format";
import { useStudio } from "../studio";
import type { GenerateResponse, ProfilesResponse, Recording, VoiceProfileView, VoicesResponse } from "../types";

const LANGS = ["auto", "english", "chinese", "japanese", "korean", "german", "french", "russian", "portuguese", "spanish", "italian"];
const cap = (s: string) => s[0].toUpperCase() + s.slice(1);
const KIND_LABEL: Record<string, string> = { clone: "cloned", designed: "designed", synthetic: "saved" };
const SCRIPTS = [
  "It started, as so many terrible ideas do, with a single image: a window, a triangle, and a stubborn sense that this could work.",
  "Good evening. Tonight we follow a small studio with very large ambitions, and a remarkable talent for questionable ideas.",
  "Hold the line! If they want this bridge, they'll have to take it from us, one plank at a time!",
];
const DESIGN_SAMPLE = "Hello. This is how I sound when I tell a story, from the quiet beginning to the very last word.";

/** One shared player for the page. */
function usePlayer() {
  const audio = useRef(new Audio());
  const [playing, setPlaying] = useState<string | null>(null);
  useEffect(() => {
    const a = audio.current;
    const end = () => setPlaying(null);
    a.addEventListener("ended", end);
    return () => { a.removeEventListener("ended", end); a.pause(); };
  }, []);
  const toggle = useCallback((key: string, url: string) => {
    const a = audio.current;
    if (playing === key) { a.pause(); setPlaying(null); return; }
    a.src = url;
    a.play().then(() => setPlaying(key), () => setPlaying(null));
  }, [playing]);
  return { playing, toggle };
}

export function VoiceLab() {
  const { notify, go } = useStudio();
  const [recordings, setRecordings] = useState<Recording[] | null>(null);
  const [dir, setDir] = useState("");
  const [profiles, setProfiles] = useState<ProfilesResponse | null>(null);
  const [cloneFrom, setCloneFrom] = useState<Recording | null>(null);
  const [removing, setRemoving] = useState<{ kind: "recording"; r: Recording } | { kind: "profile"; p: VoiceProfileView } | null>(null);
  const player = usePlayer();

  const load = useCallback(() => {
    call<{ dir: string; recordings: Recording[] }>("recordings.list").then((r) => { setRecordings(r.recordings); setDir(r.dir); }, (e) => notify(errorText(e), true));
    call<ProfilesResponse>("voices.profiles").then(setProfiles, (e) => notify(errorText(e), true));
  }, [notify]);
  useEffect(load, [load]);

  const remove = async () => {
    if (!removing) return;
    try {
      if (removing.kind === "recording") { await call("recordings.delete", { id: removing.r.id }); notify(`“${removing.r.name}” moved to the Recycle Bin.`); }
      else { await call("voices.removeProfile", { id: removing.p.voiceId }); notify(`Voice “${removing.p.name}” removed. Takes made with it are kept.`); }
    } catch (e) { notify(errorText(e), true); }
    setRemoving(null);
    load();
  };

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Voice lab</h1>
          <p>Record your voice, keep a library of performances, design new voices from a description, and turn any of them into a voice NoorVox can speak with.</p>
        </div>
      </div>

      <div className="lab-grid">
        <Recorder onSaved={(r) => { notify(`Saved “${r.name}”.`); load(); }} />
        <Designer onSaved={() => load()} />
      </div>

      <section className="panel" style={{ marginTop: 16 }} aria-label="Recordings">
        <div className="row" style={{ justifyContent: "space-between" }}>
          <h2 style={{ margin: 0 }}>My recordings</h2>
          <div className="row">
            <button className="btn small" onClick={() => call<{ cancelled: boolean }>("recordings.import").then((r) => { if (!r.cancelled) load(); }, (e) => notify(errorText(e), true))}>
              <Upload size={15} /> Add a WAV file…
            </button>
            <button className="btn small" onClick={() => call("recordings.reveal", {}).catch((e) => notify(errorText(e), true))}><Folder size={15} /> Open folder</button>
          </div>
        </div>
        <p className="muted small" style={{ marginTop: 6 }}>Kept as WAV files in <span className="mono">{dir}</span> (change it in Settings → Storage). They stay yours: NoorVox never changes the audio, and Delete moves them to the Recycle Bin.</p>
        {recordings?.length === 0 && <div className="empty">No recordings yet. Record one above, or add a WAV you already have.</div>}
        <div className="stack" style={{ gap: 8 }}>
          {recordings?.map((r) => (
            <RecordingRow key={r.id} r={r} playing={player.playing === r.id} onPlay={() => player.toggle(r.id, r.mediaUrl)}
              onClone={() => setCloneFrom(r)} onRemove={() => setRemoving({ kind: "recording", r })} onChanged={load} />
          ))}
        </div>
      </section>

      <section className="panel" style={{ marginTop: 16 }} aria-label="My voices">
        <h2>My voices</h2>
        {profiles && profiles.usableWith.length === 0 && (
          <p className="note">Cloned, designed and saved voices are spoken by a Qwen3-TTS <b>Base</b> model, which isn't installed.
            <button className="btn small" style={{ marginLeft: 8 }} onClick={() => go("models")}><Download size={14} /> Models</button></p>
        )}
        {profiles?.profiles.length === 0 && <div className="empty">No voices yet. Make one from a recording or a design above.</div>}
        <div className="stack" style={{ gap: 8 }}>
          {profiles?.profiles.map((p) => (
            <div key={p.voiceId} className="pack">
              <button className="play small-play" onClick={() => player.toggle("p:" + p.voiceId, p.mediaUrl)} aria-label={`Play the reference of ${p.name}`}>
                {player.playing === "p:" + p.voiceId ? <Pause size={14} /> : <Play size={14} />}
              </button>
              <div style={{ minWidth: 0, flex: 1 }}>
                <b>{p.name}</b> <span className="pill accent">{KIND_LABEL[p.kind]}</span> <span className="mono muted small">{p.voiceId}</span>
                <div className="muted small" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}
                  title={p.kind === "designed" ? p.description ?? "" : p.consent.statement}>
                  {p.kind === "designed" ? `“${p.description}”` : p.kind === "synthetic" ? `From ${p.sourceModel} · ${p.sourceVoice}` : `From ${p.sourceFileName}`}
                  {" · "}{fmtDuration(p.referenceDurationMs)} reference · {p.language === "auto" ? "any language" : cap(p.language)}
                </div>
                {p.kind === "clone" && <div className="muted small"><Shield size={12} /> {p.consent.statement}</div>}
              </div>
              <button className="btn small" onClick={() => go("narrate")} title="Pick it in Quick narration with a Qwen Base model">Use…</button>
              <button className="btn small ghost danger" onClick={() => setRemoving({ kind: "profile", p })}>Remove</button>
            </div>
          ))}
        </div>
      </section>

      {cloneFrom && profiles && (
        <CloneFromRecording r={cloneFrom} consent={profiles.consentStatements} onClose={() => setCloneFrom(null)}
          onCreated={(name) => { setCloneFrom(null); notify(`Voice “${name}” is ready in Quick narration (Qwen Base model).`); load(); }} />
      )}
      {removing && (
        <Confirm title={removing.kind === "recording" ? `Delete “${removing.r.name}”?` : `Remove the voice “${removing.p.name}”?`}
          confirmText={removing.kind === "recording" ? "Move to Recycle Bin" : "Remove voice"} danger onCancel={() => setRemoving(null)} onConfirm={remove}>
          {removing.kind === "recording"
            ? "The WAV and its notes go to the Windows Recycle Bin, so you can still restore them. Voices already made from it keep working."
            : "NoorVox deletes its copy of the reference and the consent record. Takes and exports made with this voice are not touched."}
        </Confirm>
      )}
    </>
  );
}

// ---------------------------------------------------------------- recorder

type RecState = { kind: "idle" } | { kind: "recording"; started: number } | { kind: "done"; wav: Blob; url: string; seconds: number };

function encodeWav(chunks: Float32Array[], rate: number): Blob {
  const n = chunks.reduce((s, c) => s + c.length, 0);
  const buf = new ArrayBuffer(44 + n * 2), v = new DataView(buf);
  const str = (o: number, t: string) => [...t].forEach((c, i) => v.setUint8(o + i, c.charCodeAt(0)));
  str(0, "RIFF"); v.setUint32(4, 36 + n * 2, true); str(8, "WAVE"); str(12, "fmt "); v.setUint32(16, 16, true);
  v.setUint16(20, 1, true); v.setUint16(22, 1, true); v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true);
  v.setUint16(32, 2, true); v.setUint16(34, 16, true); str(36, "data"); v.setUint32(40, n * 2, true);
  let o = 44;
  for (const c of chunks) for (let i = 0; i < c.length; i++, o += 2) v.setInt16(o, Math.max(-1, Math.min(1, c[i])) * 0x7fff, true);
  return new Blob([buf], { type: "audio/wav" });
}

function blobToBase64(b: Blob): Promise<string> {
  return new Promise((res, rej) => {
    const r = new FileReader();
    r.onload = () => res(String(r.result).split(",", 2)[1] ?? "");
    r.onerror = () => rej(r.error);
    r.readAsDataURL(b);
  });
}

function Recorder({ onSaved }: { onSaved: (r: Recording) => void }) {
  const { notify } = useStudio();
  const [state, setState] = useState<RecState>({ kind: "idle" });
  const [level, setLevel] = useState(0);
  const [peakHit, setPeakHit] = useState(false);
  const [name, setName] = useState("");
  const [transcript, setTranscript] = useState(SCRIPTS[0]);
  const [busy, setBusy] = useState(false);
  const [, setTick] = useState(0);
  const rig = useRef<{ ctx: AudioContext; stream: MediaStream; chunks: Float32Array[] } | null>(null);

  useEffect(() => () => { rig.current?.stream.getTracks().forEach((t) => t.stop()); rig.current?.ctx.close(); }, []);
  useEffect(() => {
    if (state.kind !== "recording") return;
    const id = window.setInterval(() => setTick((t) => t + 1), 250);
    return () => window.clearInterval(id);
  }, [state.kind]);

  const start = async () => {
    try {
      // Raw microphone: no echo cancellation, noise suppression or auto gain, which would colour a voice reference.
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
      const ctx = new AudioContext();
      const src = ctx.createMediaStreamSource(stream);
      const proc = ctx.createScriptProcessor(4096, 1, 1);
      const chunks: Float32Array[] = [];
      proc.onaudioprocess = (e) => {
        const d = e.inputBuffer.getChannelData(0);
        chunks.push(new Float32Array(d));
        let peak = 0;
        for (let i = 0; i < d.length; i++) peak = Math.max(peak, Math.abs(d[i]));
        setLevel(peak);
        if (peak > 0.98) setPeakHit(true);
      };
      const mute = ctx.createGain();
      mute.gain.value = 0; // the processor must be connected to run; nothing reaches the speakers
      src.connect(proc); proc.connect(mute); mute.connect(ctx.destination);
      rig.current = { ctx, stream, chunks };
      setPeakHit(false);
      setState({ kind: "recording", started: Date.now() });
    } catch (e) {
      notify(e instanceof DOMException && e.name === "NotAllowedError"
        ? "NoorVox can't use the microphone. Check Windows Settings → Privacy → Microphone (allow desktop apps)."
        : `Microphone unavailable: ${errorText(e)}`, true);
    }
  };

  const stop = async () => {
    const r = rig.current;
    if (!r) return;
    r.stream.getTracks().forEach((t) => t.stop());
    const rate = r.ctx.sampleRate;
    await r.ctx.close();
    rig.current = null;
    setLevel(0);
    const wav = encodeWav(r.chunks, rate);
    const seconds = r.chunks.reduce((s, c) => s + c.length, 0) / rate;
    setState({ kind: "done", wav, url: URL.createObjectURL(wav), seconds });
  };

  const discard = () => { if (state.kind === "done") URL.revokeObjectURL(state.url); setState({ kind: "idle" }); };

  const save = async () => {
    if (state.kind !== "done") return;
    setBusy(true);
    try {
      const r = await call<Recording>("recordings.save", { name, transcript, wavBase64: await blobToBase64(state.wav) });
      discard();
      setName("");
      onSaved(r);
    } catch (e) { notify(errorText(e), true); } finally { setBusy(false); }
  };

  const secs = state.kind === "recording" ? (Date.now() - state.started) / 1000 : state.kind === "done" ? state.seconds : 0;
  return (
    <section className="panel" aria-label="Record">
      <h2><Mic size={16} /> Record</h2>
      <label className="field" htmlFor="rec-script">Script to read (this becomes the transcript)</label>
      <textarea id="rec-script" rows={3} value={transcript} onChange={(e) => setTranscript(e.target.value)} />
      <div className="row" style={{ marginTop: 6, gap: 6 }}>
        {SCRIPTS.map((s, i) => <button key={i} className="btn small ghost" onClick={() => setTranscript(s)}>{["Narration", "Documentary", "Battle cry"][i]}</button>)}
      </div>
      <p className="muted small">For a voice clone, read 5–15 seconds clearly, in the mood you want: calm, angry and whispering recordings make different voices.
        Change the text if you improvise; it must match what you say.</p>

      <div className="rec-controls">
        {state.kind !== "recording"
          ? <button className="btn primary" onClick={start} disabled={busy}><span className="rec-dot" /> {state.kind === "done" ? "Record again" : "Record"}</button>
          : <button className="btn danger" onClick={stop}><Stop size={15} /> Stop</button>}
        <span className="mono" aria-live="polite">{secs.toFixed(1)} s</span>
        <div className="meter" role="meter" aria-label="Input level" aria-valuemin={0} aria-valuemax={1} aria-valuenow={level}>
          <i style={{ width: `${Math.min(100, level * 100)}%` }} className={level > 0.9 ? "hot" : ""} />
        </div>
      </div>
      {peakHit && <p className="note err">The input clipped (too loud). Move back a little or lower the microphone level, then record again.</p>}
      {state.kind === "recording" && secs > 0 && secs < 2 && <p className="muted small">Keep going: at least a few seconds of speech…</p>}

      {state.kind === "done" && (
        <div className="stack" style={{ gap: 8, marginTop: 10 }}>
          <audio controls src={state.url} style={{ width: "100%" }} />
          <label className="field" htmlFor="rec-name">Name</label>
          <input id="rec-name" value={name} maxLength={80} onChange={(e) => setName(e.target.value)} placeholder="e.g. Tom, calm narrator" />
          <div className="row" style={{ justifyContent: "flex-end" }}>
            <button className="btn ghost" onClick={discard} disabled={busy}>Discard</button>
            <button className="btn primary" onClick={save} disabled={busy || state.seconds < 0.5}>{busy ? <Spinner /> : <Download />} Save recording</button>
          </div>
        </div>
      )}
    </section>
  );
}

// ---------------------------------------------------------------- recordings list

function RecordingRow({ r, playing, onPlay, onClone, onRemove, onChanged }: {
  r: Recording; playing: boolean; onPlay: () => void; onClone: () => void; onRemove: () => void; onChanged: () => void;
}) {
  const { notify } = useStudio();
  const update = (patch: Record<string, string>) =>
    call("recordings.update", { id: r.id, ...patch }).then(onChanged, (e) => notify(errorText(e), true));
  return (
    <div className="pack recording">
      <button className="play small-play" onClick={onPlay} aria-label={playing ? "Pause" : `Play ${r.name}`}>{playing ? <Pause size={14} /> : <Play size={14} />}</button>
      <div style={{ minWidth: 0, flex: 1 }} className="stack">
        <input key={"n" + r.name} className="inline-edit" defaultValue={r.name} aria-label="Recording name" maxLength={80}
          onBlur={(e) => { if (e.target.value.trim() && e.target.value.trim() !== r.name) update({ name: e.target.value }); }} />
        <input key={"t" + r.transcript} className="inline-edit muted" defaultValue={r.transcript} aria-label="What is said (transcript)"
          placeholder="What is said, word for word (needed to make a voice)"
          onBlur={(e) => { if (e.target.value.trim() !== r.transcript) update({ transcript: e.target.value }); }} />
        <span className="muted small">{fmtDuration(r.durationMs)} · {r.sampleRate / 1000} kHz · {r.source === "microphone" ? "recorded" : `imported from ${r.sourceFileName}`} · {new Date(r.createdAt).toLocaleString()}</span>
      </div>
      <button className="btn small primary" onClick={onClone} disabled={!r.transcript.trim() || r.durationMs < 2000 || r.durationMs > 60000}
        title={!r.transcript.trim() ? "Add the transcript first" : r.durationMs < 2000 || r.durationMs > 60000 ? "Voices need 2–60 s of speech" : "Make a voice from this recording"}>
        <Mic size={14} /> Make a voice…
      </button>
      <button className="btn small" onClick={() => call("recordings.reveal", { id: r.id }).catch((e) => notify(errorText(e), true))} aria-label="Show in Explorer"><Folder size={14} /></button>
      <button className="btn small ghost danger" onClick={onRemove}>Delete</button>
    </div>
  );
}

function CloneFromRecording({ r, consent, onClose, onCreated }: {
  r: Recording; consent: Record<string, string>; onClose: () => void; onCreated: (name: string) => void;
}) {
  const [name, setName] = useState(r.name);
  const [language, setLanguage] = useState("english");
  const [gender, setGender] = useState("unknown");
  const [basis, setBasis] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const create = async () => {
    setBusy(true); setError(null);
    try {
      await call("voices.cloneFromRecording", { recordingId: r.id, name, consent: basis, language, gender });
      onCreated(name);
    } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  };
  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label="Make a voice from a recording">
      <div className="modal" style={{ width: "min(560px, 100%)" }}>
        <h3><Mic size={18} /> Make a voice from “{r.name}”</h3>
        <p className="muted small" style={{ marginTop: 0 }}>{fmtDuration(r.durationMs)} · “{r.transcript}”</p>
        <label className="field" htmlFor="cr-name">Voice name</label>
        <input id="cr-name" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} />
        <div className="row" style={{ gap: 12 }}>
          <div style={{ flex: 1 }}>
            <label className="field" htmlFor="cr-lang">Language of the recording</label>
            <select id="cr-lang" value={language} onChange={(e) => setLanguage(e.target.value)}>
              {LANGS.map((l) => <option key={l} value={l}>{l === "auto" ? "Detect" : cap(l)}</option>)}
            </select>
          </div>
          <div style={{ flex: 1 }}>
            <label className="field" htmlFor="cr-gender">Voice (for sorting only)</label>
            <select id="cr-gender" value={gender} onChange={(e) => setGender(e.target.value)}>
              <option value="unknown">Not specified</option><option value="female">Female</option><option value="male">Male</option>
            </select>
          </div>
        </div>
        <fieldset className="consent">
          <legend><Shield size={15} /> Consent (required)</legend>
          {Object.entries(consent).map(([key, statement]) => (
            <label key={key} className="radio"><input type="radio" name="cr-consent" checked={basis === key} onChange={() => setBasis(key)} /> {statement}</label>
          ))}
        </fieldset>
        {error && <p className="note err">{error}</p>}
        <div className="row" style={{ justifyContent: "flex-end", marginTop: 14 }}>
          <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn primary" onClick={create} disabled={!basis || !name.trim() || busy}>{busy ? <Spinner /> : <Mic />} Create voice</button>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- VoiceDesign

interface DesignTake { key: string; description: string; text: string; mediaUrl: string; outputPath: string; seed: number | null; durationMs: number }

function Designer({ onSaved }: { onSaved: () => void }) {
  const { notify, go, workers } = useStudio();
  const [voices, setVoices] = useState<VoicesResponse | null>(null);
  const [description, setDescription] = useState("A warm British woman in her fifties, calm and kind, a seasoned audiobook narrator.");
  const [text, setText] = useState(DESIGN_SAMPLE);
  const [language, setLanguage] = useState("english");
  const [takes, setTakes] = useState<DesignTake[]>([]);
  const [busy, setBusy] = useState<number | null>(null);
  const [, setTick] = useState(0);
  const [saving, setSaving] = useState<DesignTake | null>(null);
  const player = usePlayer();
  const design = voices?.models?.find((m) => m.capabilities.design);

  useEffect(() => { call<VoicesResponse>("voices.list").then(setVoices, () => undefined); }, []);
  useEffect(() => {
    if (busy == null) return;
    const id = window.setInterval(() => setTick((t) => t + 1), 1000);
    return () => window.clearInterval(id);
  }, [busy]);

  const generate = async () => {
    if (!design) return;
    setBusy(Date.now());
    try {
      const r = await call<GenerateResponse>("narration.generate", { text, voice: "designed", model: design.reference, speed: 1, style: description, language, seed: null });
      setTakes((t) => [{ key: r.result.outputPath, description, text, mediaUrl: r.mediaUrl, outputPath: r.result.outputPath, seed: r.result.seed ?? null, durationMs: r.result.durationMs }, ...t].slice(0, 6));
      player.toggle(r.result.outputPath, r.mediaUrl);
    } catch (e) { notify(errorText(e), true); } finally { setBusy(null); }
  };

  const loading = workers.find((w) => w.workerId === "studio" && w.state === "loading")?.loading;
  if (voices && !design) {
    return (
      <section className="panel" aria-label="Design a voice">
        <h2><Wave size={16} /> Design a voice</h2>
        <p className="muted">Describe a voice in words and Qwen3-TTS VoiceDesign creates it. Keep the ones you like as permanent voices.</p>
        <p className="note">Needs the <b>Qwen3-TTS 1.7B VoiceDesign</b> model (4.5 GB) and an NVIDIA GPU.</p>
        <button className="btn primary" onClick={() => go("models")}><Download size={15} /> Install it on the Models page</button>
      </section>
    );
  }
  return (
    <section className="panel" aria-label="Design a voice">
      <h2><Wave size={16} /> Design a voice</h2>
      <label className="field" htmlFor="ds-desc">Describe the voice</label>
      <textarea id="ds-desc" rows={3} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)}
        placeholder="Age, gender, accent, timbre, mood, pace… e.g. a gravelly old sea captain, slow and amused" />
      <label className="field" htmlFor="ds-text">Sample sentence (also the voice's reference text)</label>
      <textarea id="ds-text" rows={2} value={text} onChange={(e) => setText(e.target.value)} />
      <div className="row" style={{ gap: 12, alignItems: "flex-end" }}>
        <div style={{ flex: 1 }}>
          <label className="field" htmlFor="ds-lang">Language</label>
          <select id="ds-lang" value={language} onChange={(e) => setLanguage(e.target.value)}>
            {LANGS.map((l) => <option key={l} value={l}>{l === "auto" ? "Detect from text" : cap(l)}</option>)}
          </select>
        </div>
        <button className="btn primary" onClick={generate} disabled={busy != null || !description.trim() || !text.trim()}>
          {busy != null ? <Spinner /> : <Wave />} {busy != null ? "Designing…" : "Design a sample"}
        </button>
      </div>
      {busy != null && (
        <p className="muted small" aria-live="polite">
          {loading ? `${loading.stage ?? "Loading the model"}… ` : "Generating… "}{Math.round((Date.now() - busy) / 1000)} s
          {loading && " · the first sample loads the model (a few minutes from a hard drive)"}
        </p>
      )}
      <p className="muted small">Each sample is a new voice: generate a few, keep the best. A kept voice is spoken by the Qwen Base model, so it stays the same every time.</p>
      <div className="stack" style={{ gap: 6 }}>
        {takes.map((t) => (
          <div key={t.key} className="pack">
            <button className="play small-play" onClick={() => player.toggle(t.key, t.mediaUrl)} aria-label="Play sample">{player.playing === t.key ? <Pause size={14} /> : <Play size={14} />}</button>
            <div style={{ minWidth: 0, flex: 1 }} className="muted small" title={t.description}>
              “{t.description.length > 70 ? t.description.slice(0, 70) + "…" : t.description}” · {fmtDuration(t.durationMs)}{t.seed != null && ` · seed ${t.seed}`}
            </div>
            <button className="btn small primary" onClick={() => setSaving(t)}>Keep as voice…</button>
          </div>
        ))}
      </div>
      {saving && <KeepVoiceDialog take={saving} onClose={() => setSaving(null)} onSaved={(name) => { setSaving(null); notify(`Voice “${name}” kept. Use it with the Qwen Base model.`); onSaved(); }} />}
    </section>
  );
}

export function KeepVoiceDialog({ take, onClose, onSaved }: { take: { outputPath: string; text: string; description?: string }; onClose: () => void; onSaved: (name: string) => void }) {
  const [name, setName] = useState("");
  const [gender, setGender] = useState("unknown");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const keep = async () => {
    setBusy(true); setError(null);
    try { await call("voices.saveTake", { previewPath: take.outputPath, name, transcript: take.text, gender }); onSaved(name); }
    catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  };
  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label="Keep as voice">
      <div className="modal">
        <h3>Keep this as a voice</h3>
        <p className="muted small" style={{ marginTop: 0 }}>The sample becomes the voice's reference. It's synthetic, so no consent statement is needed; its origin is recorded with it.</p>
        <label className="field" htmlFor="kv-name">Name</label>
        <input id="kv-name" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} placeholder="e.g. Margaret, audiobook narrator" autoFocus />
        <label className="field" htmlFor="kv-gender">Voice (for sorting only)</label>
        <select id="kv-gender" value={gender} onChange={(e) => setGender(e.target.value)}>
          <option value="unknown">Not specified</option><option value="female">Female</option><option value="male">Male</option>
        </select>
        {error && <p className="note err">{error}</p>}
        <div className="row" style={{ justifyContent: "flex-end", marginTop: 14 }}>
          <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn primary" onClick={keep} disabled={!name.trim() || busy}>{busy && <Spinner />} Keep voice</button>
        </div>
      </div>
    </div>
  );
}
