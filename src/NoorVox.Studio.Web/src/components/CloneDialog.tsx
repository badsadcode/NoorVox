import { useEffect, useState } from "react";
import { call, errorText } from "../bridge";
import { fmtDuration } from "../format";
import { Mic, Shield, Spinner, Upload } from "./Icons";

const LANGS = ["auto", "english", "chinese", "japanese", "korean", "german", "french", "russian", "portuguese", "spanish", "italian"];

interface Reference { token: string; fileName: string; durationMs: number }

/**
 * Creates a cloned voice profile. The recording is picked in a native dialog (the page never sees its path), the
 * transcript is required by the model, and the user must confirm one of the consent statements, which is stored verbatim.
 */
export function CloneDialog({ consent, onClose, onCreated }: {
  consent: Record<string, string>; onClose: () => void; onCreated: (voiceId: string) => void;
}) {
  const [name, setName] = useState("");
  const [ref, setRef] = useState<Reference | null>(null);
  const [transcript, setTranscript] = useState("");
  const [language, setLanguage] = useState("english");
  const [gender, setGender] = useState("unknown");
  const [basis, setBasis] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const k = (e: KeyboardEvent) => { if (e.key === "Escape" && !busy) onClose(); };
    window.addEventListener("keydown", k);
    return () => window.removeEventListener("keydown", k);
  }, [busy, onClose]);

  const choose = async () => {
    setError(null);
    try {
      const r = await call<{ cancelled: boolean } & Partial<Reference>>("voices.chooseReference");
      if (!r.cancelled) setRef({ token: r.token!, fileName: r.fileName!, durationMs: r.durationMs! });
    } catch (e) { setError(errorText(e)); }
  };

  const tooShort = ref && ref.durationMs < 2000;
  const tooLong = ref && ref.durationMs > 60000;
  const ready = name.trim() && ref && !tooShort && !tooLong && transcript.trim().length > 1 && basis;

  const create = async () => {
    if (!ready || !ref || !basis) return;
    setBusy(true);
    setError(null);
    try {
      const r = await call<{ voiceId: string }>("voices.clone", {
        name: name.trim(), referenceToken: ref.token, transcript: transcript.trim(), consent: basis, language, gender,
      });
      onCreated(r.voiceId);
    } catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  };

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-label="Clone a voice">
      <div className="modal" style={{ width: "min(600px, 100%)", maxHeight: "92vh", overflow: "auto" }}>
        <h3><Mic size={18} /> Clone a voice</h3>
        <p className="muted small" style={{ marginTop: 0 }}>
          Qwen3-TTS Base speaks in the voice of a short recording. Use 5–15 seconds of clean speech, one speaker, no music.
          NoorVox keeps a copy of the recording with the voice; your original file is not changed.
        </p>

        <label className="field" htmlFor="clone-name">Name</label>
        <input id="clone-name" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} placeholder="e.g. Tom narrator" />

        <label className="field">Reference recording (WAV)</label>
        <div className="row">
          <button className="btn small" onClick={choose}><Upload size={15} /> Choose recording…</button>
          {ref && <span className="small">{ref.fileName} · <b>{fmtDuration(ref.durationMs)}</b></span>}
        </div>
        {tooShort && <p className="note err">That recording is under 2 seconds; the model needs more speech.</p>}
        {tooLong && <p className="note err">That recording is over 60 seconds; trim it to 5–15 s of clean speech.</p>}

        <label className="field" htmlFor="clone-text">What is said in the recording (word for word)</label>
        <textarea id="clone-text" rows={3} value={transcript} onChange={(e) => setTranscript(e.target.value)}
          placeholder="Type exactly what the speaker says in the recording." />

        <div className="row" style={{ gap: 12 }}>
          <div style={{ flex: 1 }}>
            <label className="field" htmlFor="clone-lang">Language of the recording</label>
            <select id="clone-lang" value={language} onChange={(e) => setLanguage(e.target.value)}>
              {LANGS.map((l) => <option key={l} value={l}>{l === "auto" ? "Detect" : l[0].toUpperCase() + l.slice(1)}</option>)}
            </select>
          </div>
          <div style={{ flex: 1 }}>
            <label className="field" htmlFor="clone-gender">Voice (for sorting only)</label>
            <select id="clone-gender" value={gender} onChange={(e) => setGender(e.target.value)}>
              <option value="unknown">Not specified</option><option value="female">Female</option><option value="male">Male</option>
            </select>
          </div>
        </div>

        <fieldset className="consent">
          <legend><Shield size={15} /> Consent (required)</legend>
          {Object.entries(consent).map(([key, statement]) => (
            <label key={key} className="radio">
              <input type="radio" name="consent" checked={basis === key} onChange={() => setBasis(key)} /> {statement}
            </label>
          ))}
          <p className="muted small" style={{ margin: "6px 0 0" }}>
            Your confirmation is stored with the voice, and takes and exports made with it are labelled as a cloned voice.
          </p>
        </fieldset>

        {error && <p className="note err">{error}</p>}
        <div className="row" style={{ justifyContent: "flex-end", marginTop: 14 }}>
          <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn primary" onClick={create} disabled={!ready || busy}>{busy ? <Spinner /> : <Mic />} Create voice</button>
        </div>
      </div>
    </div>
  );
}
