import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { call, errorText, on } from "../bridge";
import { Download, Folder, Mic, Pause, Play, Spinner, Upload, Wave } from "../components/Icons";
import { Waveform } from "../components/Waveform";
import { CloneDialog } from "../components/CloneDialog";
import { KeepVoiceDialog } from "./VoiceLab";
import { DeliveryHelp } from "../components/DeliveryHelp";
import { estimateSeconds, fmtClock, fmtDuration, wordCount } from "../format";
import { useStudio } from "../studio";
import type { GenerateResponse, Take, VoicesResponse } from "../types";

const DRAFT_KEY = "noorvox.quick.draft";
const VOICE_KEY = "noorvox.quick.voice";
const SPEED_KEY = "noorvox.quick.speed";
const MODEL_KEY = "noorvox.quick.model";
const STYLE_KEY = "noorvox.quick.style";
const LANG_KEY = "noorvox.quick.language";
/** What a model does, from its capabilities: speaks built-in voices, speaks your voices (clones/designed), or designs new ones. */
type ModelRole = "builtin" | "yours" | "design";
const roleOf = (c: { design?: boolean; cloning: boolean }): ModelRole => c.design ? "design" : c.cloning ? "yours" : "builtin";
const MODEL_ROLES: [string, ModelRole][] = [["Built-in voices", "builtin"], ["Speaks your voices (Voice lab)", "yours"], ["Designs new voices", "design"]];
const ROLE_HINT: Record<ModelRole, string> = {
  builtin: "Speaks its own ready-made voices.",
  yours: "Speaks the voices you cloned or designed in the Voice lab (My voices).",
  design: "Makes a brand-new voice from your description each time. Keep the good ones in the Voice lab.",
};
const KIND_GROUP: Record<string, string> ={ clone: "My cloned voices", designed: "My designed voices", synthetic: "My saved voices", blend: "My blended voices" };
const cap = (s: string) => s ? s[0].toUpperCase() + s.slice(1) : s;
const load = (k: string) => { try { return localStorage.getItem(k); } catch { return null; } };
const save = (k: string, v: string) => { try { localStorage.setItem(k, v); } catch { /* storage unavailable */ } };
const SAMPLE = "It started, as so many terrible ideas do, with a single image.\n\nA window, a Vulkan device, and a triangle. Nothing more. No terrain, no trees, no Alden. Just proof that the engine could draw something on the screen.";

function readDraft(): string {
  try { return localStorage.getItem(DRAFT_KEY) ?? SAMPLE; } catch { return SAMPLE; }
}

export function Narrate() {
  const { notify, refreshWorkers, go, info, takes, setTakes, workers } = useStudio();
  const [voices, setVoices] = useState<VoicesResponse | null>(null);
  const [text, setText] = useState(readDraft);
  const [voiceId, setVoiceId] = useState("");
  const [speed, setSpeed] = useState(() => Number(load(SPEED_KEY)) || 1);
  const [modelRef, setModelRef] = useState<string | null>(() => load(MODEL_KEY));
  const [style, setStyle] = useState(() => load(STYLE_KEY) ?? "");
  const [language, setLanguage] = useState(() => load(LANG_KEY) ?? "auto");
  const [seedLocked, setSeedLocked] = useState(false);
  const [emotion, setEmotion] = useState<number | null>(null); // null = the model's default
  const [pace, setPace] = useState<number | null>(null);
  const [seed, setSeed] = useState<number | null>(null);
  const [cloning, setCloning] = useState(false);
  const [keeping, setKeeping] = useState<Take | null>(null);
  const [reloadVoices, setReloadVoices] = useState(0);
  const [busy, setBusy] = useState<string | null>(null);
  const [busySince, setBusySince] = useState<number | null>(null);
  const [, setTick] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [playing, setPlaying] = useState<string | null>(null);
  const [progress, setProgress] = useState<Record<string, number>>({});
  const audio = useRef<HTMLAudioElement>(new Audio());
  const fileInput = useRef<HTMLInputElement>(null);
  const script = useRef<HTMLTextAreaElement>(null);
  /** Inserts a model tag at the cursor (or the end), with spaces around it, and puts the cursor after it. */
  const insertTag = useCallback((tag: string) => {
    const el = script.current;
    const at = el ? el.selectionStart : text.length;
    const endAt = el ? el.selectionEnd : text.length;
    const before = text.slice(0, at), after = text.slice(endAt);
    const piece = (before && !/\s$/.test(before) ? " " : "") + tag + (after && !/^\s/.test(after) ? " " : "");
    setText(before + piece + after);
    requestAnimationFrame(() => { if (el) { el.focus(); const pos = before.length + piece.length; el.setSelectionRange(pos, pos); } });
  }, [text]);

  useEffect(() => {
    call<VoicesResponse>("voices.list", { model: modelRef }).then((v) => {
      setVoices(v);
      if (v.model && v.model !== modelRef) setModelRef(v.model);
      const remembered = load(`${VOICE_KEY}.${v.model}`) ?? load(VOICE_KEY);
      const pick = (id: string | null | undefined) => (id && v.voices.some((x) => x.id === id) ? id : null);
      setVoiceId((cur) => pick(cur) ?? pick(remembered) ?? pick(info?.settings.defaultVoice) ?? v.defaultVoice ?? v.voices[0]?.id ?? "");
    }, (e) => {
      if (modelRef) { setModelRef(null); return; } // a remembered model that was removed: fall back to the default
      setError(errorText(e));
    });
  }, [info?.settings.defaultVoice, modelRef, reloadVoices]);

  useEffect(() => save(DRAFT_KEY, text), [text]);
  useEffect(() => { if (voiceId && voices?.model) save(`${VOICE_KEY}.${voices.model}`, voiceId); }, [voiceId, voices?.model]);
  useEffect(() => save(SPEED_KEY, String(speed)), [speed]);
  useEffect(() => { if (modelRef) save(MODEL_KEY, modelRef); }, [modelRef]);
  useEffect(() => save(STYLE_KEY, style), [style]);
  useEffect(() => save(LANG_KEY, language), [language]);
  const caps = voices?.capabilities;
  // What the studio's engine is doing while a generation waits (polled every 2 s by the studio shell).
  const loadingNow = busy ? workers.find((w) => w.workerId === "studio" && w.state === "loading")?.loading ?? null : null;
  const studioLoaded = workers.find((w) => w.workerId === "studio")?.loadedModel;
  const modelLoaded = !!studioLoaded && voices?.model === `${studioLoaded.id}@${studioLoaded.version}`;
  useEffect(() => on<{ message: string }>("narration.progress", (d) => setBusy(d.message)), []);
  useEffect(() => { // a visible clock while generating: a long model load must never look like a hang
    if (!busy) return;
    const id = window.setInterval(() => setTick((t) => t + 1), 1000);
    return () => window.clearInterval(id);
  }, [busy]);

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
    voices?.voices.forEach((v) => { const g = KIND_GROUP[v.kind] ?? v.languageName; m.set(g, [...(m.get(g) ?? []), v]); });
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
    setBusySince(Date.now());
    try {
      // Only send what this model supports (the C# side rejects anything else anyway).
      const r = await call<GenerateResponse>("narration.generate", {
        text: spoken, voice: voiceId, model: voices?.model,
        speed: caps?.speed ? speed : 1,
        style: (caps?.style || caps?.design) && style.trim() ? style.trim() : null,
        seed: caps?.seed && seedLocked && seed != null ? seed : null,
        language: caps?.languages.length ? (caps.languages.includes(language) ? language : null) : null,
        emotion: caps?.emotion ? emotion ?? caps.emotionDefault : null,
        pace: caps?.pace ? pace ?? caps.paceDefault : null,
      });
      if (r.result.seed != null && !seedLocked) setSeed(r.result.seed);
      const take: Take = { key: r.result.outputPath, text: spoken, voice, mediaUrl: r.mediaUrl, result: r.result };
      if (!audition) setTakes((t) => [take, ...t]);
      play(take);
      if (audition) notify(`Audition: ${voice?.displayName} (${fmtDuration(r.result.durationMs)})`);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(null);
      setBusySince(null);
      refreshWorkers();
    }
  }, [voiceId, speed, busy, voice, play, notify, refreshWorkers, voices?.model, caps, style, seedLocked, seed, language, emotion, pace]);

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
        <div className="narrate-main">
          <section className="panel editor" aria-label="Script">
          <div className="editor-head">
            <h2>Script</h2>
            <div className="row">
              <input ref={fileInput} type="file" accept=".txt,.md,text/plain" hidden onChange={(e) => { importFile(e.target.files?.[0]); e.target.value = ""; }} />
              <button className="btn small" onClick={() => fileInput.current?.click()}><Upload size={15} /> Import text…</button>
              <button className="btn small ghost" onClick={() => setText("")} disabled={!text}>Clear</button>
            </div>
          </div>
          <textarea ref={script} value={text} onChange={(e) => setText(e.target.value)} aria-label="Text to narrate" spellCheck
            placeholder="Type or paste what NoorVox should say…"
            onKeyDown={(e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); generate(text); } }} />
          <div className="editor-foot">
            <span>{words} words</span>
            <span>{text.length} characters</span>
            <span title="Estimate before generating. Real durations are measured from the audio.">≈ {fmtClock(estimateSeconds(words, speed))} estimated at {speed.toFixed(2)}×</span>
            <span style={{ marginLeft: "auto" }}>Draft saved · Ctrl+Enter to generate</span>
          </div>
        </section>

          <DeliveryHelp caps={caps} onInsertTag={insertTag} onStyle={setStyle}
            onSliders={(e, p) => { setEmotion(e); setPace(p); }} />
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
                    <span>{t.voice?.displayName ?? t.result.voice}{t.result.voiceKind === "clone" ? " (cloned)" : ""}{t.result.backend.id === "kokoro-onnx" && ` · ${t.result.speed.toFixed(2)}×`}</span>
                    {t.result.emotion != null && <span>emotion {t.result.emotion.toFixed(2)} · pace {t.result.pace?.toFixed(2)}</span>}
                    {t.result.watermark && <span title="The engine adds an inaudible watermark that marks the audio as AI-generated">watermarked</span>}
                    {t.result.style && <span title={t.result.style}>style: “{t.result.style.length > 40 ? t.result.style.slice(0, 40) + "…" : t.result.style}”</span>}
                    {t.result.seed != null && (
                      <button className="linkish mono" title="Use this seed for the next take" onClick={() => { setSeed(t.result.seed!); setSeedLocked(true); }}>
                        seed {t.result.seed}
                      </button>
                    )}
                    <span>{t.result.sampleRate / 1000} kHz · {t.result.bitsPerSample}-bit mono</span>
                    <span>generated in {(t.result.timings.synthesisMs / 1000).toFixed(1)} s ({t.result.backend.executionProvider})</span>
                    {t.exportedPath && <span className="pill ok">Exported</span>}
                  </div>
                </div>
                <div className="row" style={{ flexWrap: "nowrap" }}>
                  {t.result.voiceKind !== "clone" && (
                    <button className="btn small" onClick={() => setKeeping(t)} title="Keep this take's voice as a permanent voice (spoken by the Qwen Base model)">Keep as voice…</button>
                  )}
                  <button className="btn small primary" onClick={() => exportTake(t)}><Download size={15} /> Export…</button>
                  <button className="btn small" onClick={() => reveal(t.exportedPath ?? t.result.outputPath)} aria-label="Show file in Explorer"><Folder size={15} /></button>
                </div>
              </article>
            ))}
          </section>
        </div>

        <aside className="side stack" aria-label="Voice and generation">
          <section className="panel">
            <h2>Voice &amp; delivery</h2>
            {(voices?.models?.length ?? 0) > 1 && <>
              <label className="field" htmlFor="model">Model</label>
              <select id="model" value={voices?.model ?? ""} onChange={(e) => setModelRef(e.target.value)} disabled={!!busy}>
                {MODEL_ROLES.map(([label, test]) => {
                  const ms = voices!.models!.filter((m) => roleOf(m.capabilities) === test);
                  return ms.length > 0 && (
                    <optgroup key={label} label={label}>
                      {ms.map((m) => <option key={m.reference} value={m.reference}>{m.displayName}{m.capabilities.requiresGpu ? " (GPU)" : ""}</option>)}
                    </optgroup>
                  );
                })}
              </select>
              {caps && <p className="muted small" style={{ margin: "4px 0 0" }}>{ROLE_HINT[roleOf(caps)]}</p>}
            </>}
            <div className="voice-card" style={{ marginTop: 10 }}>
              <Mic size={22} />
              <div style={{ minWidth: 0 }}>
                <b>{voice ? voice.displayName : "—"}{voice?.kind === "clone" && <span className="pill accent" style={{ marginLeft: 6 }}>cloned</span>}</b>
                <span className="muted small">{voice ? `${voice.languageName} · ${voice.gender} · ${voices?.modelName}` : voices?.voices.length === 0 ? "No voices yet" : "Loading voices…"}</span>
                {voice?.description && <span className="muted small" title={voice.description} style={{ display: "block", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{voice.description}</span>}
              </div>
            </div>
            {voices?.engineReady === false && (
              <p className="note err">This model's engine pack isn't installed. <button className="btn small" onClick={() => go("models")}>Open Models</button></p>
            )}
            <label className="field" htmlFor="voice">Voice</label>
            <select id="voice" value={voiceId} onChange={(e) => setVoiceId(e.target.value)} disabled={!voices}>
              {groups.map(([lang, vs]) => (
                <optgroup key={lang} label={lang}>
                  {vs.map((v) => <option key={v.id} value={v.id}>{v.displayName} · {v.gender}</option>)}
                </optgroup>
              ))}
            </select>
            <div className="row" style={{ marginTop: 8 }}>
              <button className="btn small" disabled={!!busy || !voice}
                onClick={() => generate(`Hello, I'm ${voice?.displayName}. This is how I sound when I narrate for you.`, true)}>
                <Wave size={15} /> Audition voice
              </button>
              {caps?.cloning && voices?.consentStatements && (
                <button className="btn small" onClick={() => setCloning(true)} disabled={!!busy}><Mic size={15} /> Clone a voice…</button>
              )}
            </div>
            {caps?.cloning && voices?.voices.length === 0 && (
              <p className="note" style={{ marginTop: 10 }}>This model speaks in cloned voices. Create one from a short recording to start.</p>
            )}

            {caps?.speed && <>
              <div className="speed-row" style={{ marginTop: 16 }}>
                <label className="field" htmlFor="speed">Speed</label>
                <span className="mono">{speed.toFixed(2)}×</span>
              </div>
              <input id="speed" type="range" min={caps.minSpeed} max={caps.maxSpeed} step={0.05}
                value={speed} onChange={(e) => setSpeed(Number(e.target.value))} onDoubleClick={() => setSpeed(1)} />
            </>}

            {(caps?.style || caps?.design) && <>
              <label className="field" htmlFor="style" style={{ marginTop: 14 }}>{caps.design ? "Voice description" : "Style directions"}</label>
              <textarea id="style" rows={2} maxLength={1000} value={style} onChange={(e) => setStyle(e.target.value)}
                placeholder={caps.design ? "e.g. A gravelly old sea captain, slow, amused, with a soft Scottish accent."
                  : "e.g. Calm, warm documentary narration with a hint of humour. Speak slowly."} />
            </>}

            {caps?.emotion && (() => { const v = emotion ?? caps.emotionDefault ?? 0.5; return <>
              <div className="speed-row" style={{ marginTop: 14 }}>
                <label className="field" htmlFor="emotion">Emotion</label>
                <span className="mono">{v.toFixed(2)} {v < 0.4 ? "· calm" : v > 1.0 ? "· dramatic" : ""}</span>
              </div>
              <input id="emotion" type="range" min={caps.emotionMin} max={caps.emotionMax} step={0.05} value={v}
                onChange={(e) => setEmotion(Number(e.target.value))} onDoubleClick={() => setEmotion(null)} />
            </>; })()}

            {caps?.pace && (() => { const v = pace ?? caps.paceDefault ?? 0.5; return <>
              <div className="speed-row" style={{ marginTop: 10 }}>
                <label className="field" htmlFor="pace" title="Chatterbox's guidance weight: lower is slower and more deliberate">Pace</label>
                <span className="mono">{v.toFixed(2)} {v < 0.35 ? "· a little slower" : v > 0.7 ? "· a little brisker" : ""}</span>
              </div>
              <input id="pace" type="range" min={caps.paceMin} max={caps.paceMax} step={0.05} value={v}
                onChange={(e) => setPace(Number(e.target.value))} onDoubleClick={() => setPace(null)} />
            </>; })()}

            {!!caps?.languages.length && <>
              <label className="field" htmlFor="language" style={{ marginTop: 12 }}>Language</label>
              <select id="language" value={language} onChange={(e) => setLanguage(e.target.value)}>
                {caps.languages.map((l) => <option key={l} value={l}>{l === "auto" ? "Detect from text" : cap(l)}</option>)}
              </select>
            </>}

            {caps?.seed && (
              <div className="row" style={{ marginTop: 12, alignItems: "center" }}>
                <label className="radio" title="The same seed, text and settings give the same take on this PC.">
                  <input type="checkbox" checked={seedLocked} onChange={(e) => setSeedLocked(e.target.checked)} /> Keep seed
                </label>
                <input aria-label="Seed" className="mono" style={{ width: 130 }} inputMode="numeric" value={seed ?? ""} placeholder="random"
                  onChange={(e) => { const n = Number(e.target.value.replace(/\D/g, "")); setSeed(e.target.value === "" ? null : n); setSeedLocked(e.target.value !== ""); }} />
              </div>
            )}

            <p className="note" style={{ marginTop: 12 }}>
              {caps == null ? "Loading…"
                : caps.speed && !caps.style ? "This model offers voice and speed. Pitch, emotion and style aren't controls in it, so NoorVox doesn't pretend they are."
                : caps.emotion ? "Chatterbox: emotion goes from flat to dramatic; pace is gentle (lower is a little slower). Double-click a slider to reset. For more drama, try emotion 0.7+ with pace 0.3."
                : caps.watermark ? "This model has no emotion or pace controls; the voice comes from the reference."
                : caps.design ? "VoiceDesign invents a voice from your description on every take. Keep one you like as a permanent voice (on its take, or in the Voice lab)."
                : caps.style ? "Qwen follows written directions instead of sliders: describe the pace, mood and delivery. It has no speed control."
                : caps.cloning ? "This model has no speed or style controls; the voice comes from the recording."
                : "This model has no speed or style controls."}
              {caps?.requiresGpu && " Runs on your NVIDIA GPU."}
              {caps?.watermark && " Its audio carries an inaudible AI watermark."}
              {caps?.requiresGpu && !modelLoaded && " Not loaded yet: the first line also loads the model (a few minutes from a hard drive); after that it stays loaded."}
            </p>
          </section>

          <section className="panel">
            <h2>Generate</h2>
            <button className="btn primary big" onClick={() => generate(text)} disabled={!!busy || !text.trim() || !voice}>
              {busy ? <Spinner /> : <Play />} {busy ? "Generating…" : "Generate narration"}
            </button>
            <div className="gen-status" aria-live="polite" style={{ marginTop: 8 }}>
              {loadingNow ? `${loadingNow.stage ?? "Loading the model"}…` : busy}
              {busy && busySince != null && <span className="mono muted"> · {Math.round((Date.now() - busySince) / 1000)} s</span>}
            </div>
            {busy && caps?.requiresGpu && (loadingNow || /^(Starting|Loading)/.test(busy)) && (
              <p className="note" style={{ marginTop: 8 }}>
                The first line after starting NoorVox loads the model (about 4.5 GB) and prepares the GPU. On this PC that measured
                1½–4 minutes from the hard drive, longest right after a restart. Later lines take seconds while the model stays loaded.
              </p>
            )}
            {error && <p className="note err">{error}</p>}
          </section>
        </aside>
      </div>
      {keeping && (
        <KeepVoiceDialog take={{ outputPath: keeping.result.outputPath, text: keeping.text }} onClose={() => setKeeping(null)}
          onSaved={(name) => { setKeeping(null); notify(`Voice “${name}” kept. Pick it with the Qwen Base model, or see it in the Voice lab.`); setReloadVoices((n) => n + 1); }} />
      )}
      {cloning && voices?.consentStatements && (
        <CloneDialog consent={voices.consentStatements} onClose={() => setCloning(false)}
          onCreated={(id) => { setCloning(false); setVoiceId(id); setReloadVoices((n) => n + 1); notify("Voice created. It's ready to use."); }} />
      )}

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
