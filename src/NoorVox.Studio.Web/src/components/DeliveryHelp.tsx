import { useState } from "react";
import type { ModelCapabilities } from "../types";

/**
 * What can be typed or chosen to shape a delivery, per model, and only what the model really supports (AGENTS.md rule 2):
 * inline tags come from the model's capabilities (its own tokenizer, e.g. Chatterbox Turbo), presets fill controls that exist.
 * Presets are starting points written by NoorVox, not features of the model.
 */
const SOUND_TAGS = new Set(["[laugh]", "[chuckle]", "[sigh]", "[gasp]", "[cough]", "[clear throat]", "[groan]", "[sniff]", "[shush]", "[crying]"]);

const STYLE_PRESETS = [
  ["Documentary", "Calm, warm documentary narration with a hint of humour."],
  ["Trailer", "Excited and energetic, building tension like a movie trailer."],
  ["Whisper", "Whispering, as if sharing a secret."],
  ["Angry", "Angry and shouting, barely holding it together."],
  ["Sad", "Sad and quiet, speaking slowly with pauses."],
  ["Cheerful", "Cheerful and friendly, smiling while speaking."],
  ["Newsreader", "Serious newsreader: crisp, neutral and clear."],
  ["Storyteller", "Slow, mysterious bedtime storyteller."],
];

const DESIGN_PRESETS = [
  ["Audiobook", "A warm British woman in her fifties, calm and kind, a seasoned audiobook narrator."],
  ["Sea captain", "A gravelly old sea captain, slow and amused, with a deep voice."],
  ["Young hero", "An eager young man in his twenties, bright and fast-talking, full of energy."],
  ["Villain", "A cold, precise villain with a low, velvety voice who never raises it."],
  ["Robot", "A calm synthetic assistant voice, even and slightly metallic, perfectly articulate."],
];

const EMOTION_PRESETS: [string, number, number][] = [["Calm", 0.3, 0.6], ["Neutral", 0.5, 0.5], ["Expressive", 0.8, 0.4], ["Dramatic", 1.3, 0.3]];

export function DeliveryHelp({ caps, onInsertTag, onStyle, onSliders }: {
  caps: ModelCapabilities | undefined;
  onInsertTag: (tag: string) => void;
  onStyle: (text: string) => void;
  onSliders: (emotion: number, pace: number) => void;
}) {
  const [open, setOpen] = useState(() => { try { return localStorage.getItem("noorvox.quick.help") !== "closed"; } catch { return true; } });
  const toggle = () => setOpen((o) => { try { localStorage.setItem("noorvox.quick.help", o ? "closed" : "open"); } catch { /* ignore */ } return !o; });
  if (!caps) return null;
  const tags = caps.tags ?? [];
  const sounds = tags.filter((t) => SOUND_TAGS.has(t));
  const moods = tags.filter((t) => !SOUND_TAGS.has(t));

  return (
    <section className="panel help" aria-label="Delivery help">
      <button className="help-head" onClick={toggle} aria-expanded={open}>
        <h2>Delivery help</h2>
        <span className="muted small">{tags.length ? `${tags.length} tags` : caps.style ? "style ideas" : caps.design ? "voice ideas" : caps.emotion ? "emotion presets" : "tips"} · {open ? "hide" : "show"}</span>
      </button>
      {open && <div className="help-body">
        {tags.length > 0 && <>
          <p className="muted small">Click to insert at the cursor. Sound tags go where the sound happens; mood tags at the start of a sentence. A few per paragraph work best. Captions leave tags out.</p>
          {sounds.length > 0 && <div className="chips" aria-label="Sound tags">{sounds.map((t) => <button key={t} className="chip" onClick={() => onInsertTag(t)}>{t}</button>)}</div>}
          {moods.length > 0 && <div className="chips" aria-label="Mood tags">{moods.map((t) => <button key={t} className="chip mood" onClick={() => onInsertTag(t)}>{t}</button>)}</div>}
        </>}
        {caps.emotion && <>
          <p className="muted small">Emotion presets set both sliders (emotion · pace). Higher emotion also speaks a little faster.</p>
          <div className="chips">{EMOTION_PRESETS.map(([n, e, p]) => <button key={n} className="chip" onClick={() => onSliders(e, p)}>{n} <span className="mono">{e} · {p}</span></button>)}</div>
        </>}
        {caps.style && <>
          <p className="muted small">Qwen follows plain-English directions in the Style box: mood, pace, volume, attitude. Click one to start from it, then edit.</p>
          <div className="chips">{STYLE_PRESETS.map(([n, s]) => <button key={n} className="chip" title={s} onClick={() => onStyle(s)}>{n}</button>)}</div>
        </>}
        {caps.design && <>
          <p className="muted small">Describe age, gender, accent, timbre, mood and pace. Each take invents a new voice; keep the ones you like.</p>
          <div className="chips">{DESIGN_PRESETS.map(([n, s]) => <button key={n} className="chip" title={s} onClick={() => onStyle(s)}>{n}</button>)}</div>
        </>}
        {!tags.length && !caps.emotion && !caps.style && !caps.design && (
          <p className="muted small">
            {caps.speed
              ? "This model has no tags or style directions. It follows punctuation: commas and full stops make pauses, and short sentences sound more natural than long ones. Use Speed for pace."
              : "This model has no tags, style directions or emotion controls: the delivery comes from the voice's reference recording, so record it in the mood you want."}
          </p>
        )}
      </div>}
    </section>
  );
}
