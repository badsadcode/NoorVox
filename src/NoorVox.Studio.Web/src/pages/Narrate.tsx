import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { call, errorText, on } from "../bridge";
import { Download, Folder, Mic, Pause, Play, Spinner, Upload, Wave } from "../components/Icons";
import { Waveform } from "../components/Waveform";
import { estimateSeconds, fmtClock, fmtDuration, wordCount } from "../format";
import { useStudio } from "../studio";
import type { GenerateResponse, Take, VoicesResponse } from "../types";

const DRAFT_KEY = "noorvox.quick.draft";
const VOICE_KEY = "noorvox.quick.voice";
const SPEED_KEY = "noorvox.quick.speed";
const load = (k: string) => { try { return localStorage.getItem(k); } catch { return null; } };
const save = (k: string, v: string) => { try { localStorage.setItem(k, v); } catch { /* storage unavailable */ } };
const SAMPLE = "It started, as so many terrible ideas do, with a single image.\n\nA window, a Vulkan device, and a triangle. Nothing more. No terrain, no trees, no Alden. Just proof that the engine could draw something on the screen.";

function readDraft(): string {
  try { return localStorage.getItem(DRAFT_KEY) ?? SAMPLE; } catch { return SAMPLE; }
}

export function Narrate() {
  const { notify, refreshWorkers, go, info, takes, setTakes } = useStudio();
  const [voices, setVoices] = useState<VoicesResponse | null>(null);
  const [text, setText] = useState(readDraft);
  const [voiceId, setVoiceId] = useState("");
  const [speed, setSpeed] = useState(() => Number(load(SPEED_KEY)) || 1);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [playing, setPlaying] = useState<string | null>(null);
  const [progress, setProgress] = useState<Record<string, number>>({});
  const audio = useRef<HTMLAudioElement>(new Audio());
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    call<VoicesResponse>("voices.list").then((v) => {
      setVoices(v);
      const remembered = load(VOICE_KEY);
      setVoiceId(remembered && v.voices.some((x) => x.id === remembered) ? remembered
        : info?.settings.defaultVoice ?? v.defaultVoice ?? v.voices[0]?.id ?? "");
    }, (e) => setError(errorText(e)));
  }, [info?.settings.defaultVoice]);

  useEffect(() => save(DRAFT_KEY, text), [text]);
  useEffect(() => { if (voiceId) save(VOICE_KEY, voiceId); }, [voiceId]);
  useEffect(() => save(SPEED_KEY, String(speed)), [speed]);
  useEffect(() => on<{ message: string }>("narration.progress", (d) => setBusy(d.message)), []);

  useEffect(() => {
    const a = audio.current;
    const tick = () => { if (playing && a.duration) setProgress((p) => ({ ...p, [playing]: a.currentTime / a.duration })); };
    const end = () => { if (playing) setProgress((p) => ({ ...p, [playing]: 1 })); setPlaying(null); };
    a.addEventListener("timeupdate", tick);
    a.addEventListener("ended", end);
    return () => { a.removeEventListener("timeupdate", tick); a.removeEventListener("ended", end); };
  }, [playing]);

  useEffect(() => () => audio.current.pause(), []);

  const voice = voices?.voices.find((v) => v.id === voiceId);
  const groups = useMemo(() => {
    const m = new Map<string, NonNullable<VoicesResponse["voices"]>>();
    voices?.voices.forEach((v) => m.set(v.languageName, [...(m.get(v.languageName) ?? []), v]));
    return [...m.entries()];
  }, [voices]);

  const words = wordCount(text);

  const play = useCallback((take: Take, from?: number) => {
    const a = audio.current;
    if (playing === take.key && from == null) { a.pause(); setPlaying(null); return; }
    if (a.src !== take.mediaUrl) a.src = take.mediaUrl;
    a.onloadedmetadata = null;
    const seek = () => { if (from != null && a.duration) a.currentTime = from * a.duration; };
    if (a.readyState >= 1) seek(); else a.onloadedmetadata = seek;
    a.play().then(() => setPlaying(take.key), (e) => {
      if (e instanceof DOMException && e.name === "NotAllowedError") return; // autoplay declined: the user can press play
      notify(`Playback failed: ${errorText(e)}`, true);
    });
  }, [playing, notify]);

  const generate = useCallback(async (spoken: string, audition = false) => {
    if (!voiceId || !spoken.trim() || busy) return;
    setError(null);
    setBusy("Preparing…");
    try {
      const r = await call<GenerateResponse>("narration.generate", { text: spoken, voice: voiceId, speed });
      const take: Take = { key: r.result.outputPath, text: spoken, voice, mediaUrl: r.mediaUrl, result: r.result };
      if (!audition) setTakes((t) => [take, ...t]);
      play(take);
      if (audition) notify(`Audition: ${voice?.displayName} (${fmtDuration(r.result.durationMs)})`);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(null);
      refreshWorkers();
    }
  }, [voiceId, speed, busy, voice, play, notify, refreshWorkers]);

  const exportTake = async (take: Take) => {
    try {
      const name = `${take.voice?.displayName ?? take.result.voice}_${take.text.trim().split(/\s+/).slice(0, 5).join("_")}`.replace(/[^\w\- ]/g, "");
      const r = await call<{ cancelled: boolean; path?: string; manifestPath?: string }>("narration.export", { previewPath: take.result.outputPath, suggestedName: name });
      if (r.cancelled) return;
      setTakes((t) => t.map((x) => (x.key === take.key ? { ...x, exportedPath: r.path } : x)));
      notify(`Exported to ${r.path}`);
    } catch (e) { notify(errorText(e), true); }
  };

  const reveal = (path: string) => call("narration.reveal", { path }).catch((e) => notify(errorText(e), true));

  const importFile = (f: File | undefined) => {
    if (!f) return;
    if (f.size > 2_000_000) { notify("That file is larger than 2 MB. Quick Narration is meant for shorter texts.", true); return; }
    f.text().then(setText, (e) => notify(errorText(e), true));
  };

  if (voices && !voices.model) {
    return (
      <>
        <Head />
        <div className="empty">
          <p style={{ fontSize: 16, color: "var(--text)" }}>No voice model is installed yet.</p>
          <p>Install the recommended voice once (about 440 MB). After that, NoorVox works completely offline.</p>
          <button className="btn primary" onClick={() => go("setup")}><Download /> Set up a voice</button>
        </div>
      </>
    );
  }

  return (
    <>
      <Head />
      <div className="narrate">
        <section className="panel editor" aria-label="Script">
          <div className="editor-head">
            <h2>Script</h2>
            <div className="row">
              <input ref={fileInput} type="file" accept=".txt,.md,text/plain" hidden onChange={(e) => { importFile(e.target.files?.[0]); e.target.value = ""; }} />
              <button className="btn small" onClick={() => fileInput.current?.click()}><Upload size={15} /> Import text…</button>
              <button className="btn small ghost" onClick={() => setText("")} disabled={!text}>Clear</button>
            </div>
          </div>
          <textarea value={text} onChange={(e) => setText(e.target.value)} aria-label="Text to narrate" spellCheck
            placeholder="Type or paste what NoorVox should say…"
            onKeyDown={(e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); generate(text); } }} />
          <div className="editor-foot">
            <span>{words} words</span>
            <span>{text.length} characters</span>
            <span title="Estimate before generating. Real durations are measured from the audio.">≈ {fmtClock(estimateSeconds(words, speed))} estimated at {speed.toFixed(2)}×</span>
            <span style={{ marginLeft: "auto" }}>Draft saved · Ctrl+Enter to generate</span>
          </div>
        </section>

        <aside className="side stack" aria-label="Voice and generation">
          <section className="panel">
            <h2>Voice &amp; delivery</h2>
            <div className="voice-card">
              <Mic size={22} />
              <div style={{ minWidth: 0 }}>
                <b>{voice ? voice.displayName : "—"}</b>
                <span className="muted small">{voice ? `${voice.languageName} · ${voice.gender} · ${voices?.modelName}` : "Loading voices…"}</span>
              </div>
            </div>
            <label className="field" htmlFor="voice">Voice</label>
            <select id="voice" value={voiceId} onChange={(e) => setVoiceId(e.target.value)} disabled={!voices}>
              {groups.map(([lang, vs]) => (
                <optgroup key={lang} label={lang}>
                  {vs.map((v) => <option key={v.id} value={v.id}>{v.displayName} · {v.gender}</option>)}
                </optgroup>
              ))}
            </select>
            <button className="btn small" style={{ marginTop: 8 }} disabled={!!busy || !voice}
              onClick={() => generate(`Hello, I'm ${voice?.displayName}. This is how I sound when I narrate for you.`, true)}>
              <Wave size={15} /> Audition voice
            </button>

            <div className="speed-row" style={{ marginTop: 16 }}>
              <label className="field" htmlFor="speed">Speed</label>
              <span className="mono">{speed.toFixed(2)}×</span>
            </div>
            <input id="speed" type="range" min={voices?.capabilities?.minSpeed ?? 0.5} max={voices?.capabilities?.maxSpeed ?? 2} step={0.05}
              value={speed} onChange={(e) => setSpeed(Number(e.target.value))} onDoubleClick={() => setSpeed(1)} />
            <p className="note" style={{ marginTop: 12 }}>
              Kokoro offers voice and speed. Pitch, emotion and style aren't controls in this model, so NoorVox doesn't pretend they are.
            </p>
          </section>

          <section className="panel">
            <h2>Generate</h2>
            <button className="btn primary big" onClick={() => generate(text)} disabled={!!busy || !text.trim() || !voice}>
              {busy ? <Spinner /> : <Play />} {busy ? "Generating…" : "Generate narration"}
            </button>
            <div className="gen-status" aria-live="polite" style={{ marginTop: 8 }}>{busy}</div>
            {error && <p className="note err">{error}</p>}
          </section>
        </aside>
      </div>

      <section className="takes" aria-label="Takes">
        <div className="page-head" style={{ marginBottom: 10 }}>
          <h2 style={{ margin: 0, fontSize: 16 }}>Takes this session</h2>
          <span className="muted small">Previews live in your temp folder. Export the ones you want to keep.</span>
        </div>
        {takes.length === 0 && <div className="empty">Your generated takes appear here, ready to play, compare and export.</div>}
        {takes.map((t, i) => (
          <article key={t.key} className={`take ${i === 0 ? "current" : ""}`}>
            <button className="play" onClick={() => play(t)} aria-label={playing === t.key ? "Pause" : "Play take"}>
              {playing === t.key ? <Pause /> : <Play />}
            </button>
            <div style={{ minWidth: 0 }}>
              <div className="take-text" title={t.text}>{t.text}</div>
              <Waveform url={t.mediaUrl} progress={progress[t.key] ?? 0} label="Seek" onSeek={(f) => play(t, f)} />
              <div className="take-meta">
                <span><b style={{ color: "var(--text)" }}>{fmtDuration(t.result.durationMs)}</b> measured</span>
                <span>{t.voice?.displayName ?? t.result.voice} · {t.result.speed.toFixed(2)}×</span>
                <span>{t.result.sampleRate / 1000} kHz · {t.result.bitsPerSample}-bit mono</span>
                <span>generated in {(t.result.timings.synthesisMs / 1000).toFixed(1)} s ({t.result.backend.executionProvider})</span>
                {t.exportedPath && <span className="pill ok">Exported</span>}
              </div>
            </div>
            <div className="row" style={{ flexWrap: "nowrap" }}>
              <button className="btn small primary" onClick={() => exportTake(t)}><Download size={15} /> Export…</button>
              <button className="btn small" onClick={() => reveal(t.exportedPath ?? t.result.outputPath)} aria-label="Show file in Explorer"><Folder size={15} /></button>
            </div>
          </article>
        ))}
      </section>
    </>
  );
}

function Head() {
  return (
    <div className="page-head">
      <div>
        <h1>Quick Narration</h1>
        <p>Write or paste text, choose a voice, and generate a voiceover on this PC. No account, no cloud, no credits.</p>
      </div>
    </div>
  );
}
