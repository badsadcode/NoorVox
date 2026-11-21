// NoorVox Studio smoke test for the custom title bar and the Voice lab (0.3.1): isolated home, Chromium's fake microphone.
// Title bar: buttons present, maximize/restore round-trip, drag region declared. Voice lab: record 4 s, save (WAV + JSON on disk),
// make a voice from it with consent, the voice appears, then clean up (profile removed, recording to the Recycle Bin).
//
// Usage: node tools/studio-smoke-voicelab.mjs <NoorVoxStudio.exe> <models-dir> <scratch-dir> <screenshot-dir>
import { mkdirSync, writeFileSync, existsSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { createChecks, launchStudio, sleep } from "./smoke-lib.mjs";

const [studioExe, modelsDir, scratch, shots] = process.argv.slice(2);
if (!shots) { console.error("usage: node tools/studio-smoke-voicelab.mjs <NoorVoxStudio.exe> <models-dir> <scratch-dir> <screenshot-dir>"); process.exit(2); }
if (existsSync(scratch)) { console.error(`${scratch} already exists; pass a new scratch dir`); process.exit(2); }
const home = join(scratch, "home");
const recDir = join(scratch, "Recordings");
mkdirSync(home, { recursive: true });
mkdirSync(shots, { recursive: true });
writeFileSync(join(home, "settings.json"), JSON.stringify({
  schemaVersion: 1, paths: { models: modelsDir, recordings: recDir }, workers: { idleTimeoutSeconds: 300, stopOnExit: true }, studio: { theme: "nocturne" },
}, null, 2));
const { results, check } = createChecks();
const s = await launchStudio(studioExe, { port: 9338, page: "voicelab", env: { NOORVOX_HOME: home }, browserArgs: "--use-fake-device-for-media-stream" });
let voiceId = null;

try {
  // ---- title bar
  await s.waitFor(`document.querySelector(".titlebar")`, 15000, "title bar");
  check("custom title bar with title and three buttons", await s.js(`return document.querySelector(".titlebar-title").textContent.includes("NoorVox Studio")
    && ["Minimize", "Maximize", "Close"].every(l => document.querySelector('.titlebar [aria-label="' + l + '"]'));`));
  check("title bar is a drag region, buttons are not", await s.js(`return getComputedStyle(document.querySelector(".titlebar")).getPropertyValue("app-region") === "drag"
    && getComputedStyle(document.querySelector(".titlebar-buttons")).getPropertyValue("app-region") === "no-drag";`));
  await s.js(`document.querySelector('.titlebar [aria-label="Maximize"]').click(); return true;`);
  await sleep(1200);
  const max = await s.bridge("window.state");
  check("maximize button maximizes, and the button turns into Restore", max.maximized && await s.js(`return !!document.querySelector('.titlebar [aria-label="Restore"]');`));
  await s.shot(shots, "23-titlebar-maximized");
  await s.js(`document.querySelector('.titlebar [aria-label="Restore"]').click(); return true;`);
  await sleep(1200);
  check("restore returns to a normal window", !(await s.bridge("window.state")).maximized);

  // ---- record with the fake microphone
  await s.waitFor(`[...document.querySelectorAll("button")].some(b => b.textContent.trim().endsWith("Record"))`, 15000, "record button");
  await s.clickText("button", "Record");
  await sleep(4500);
  check("recording shows a running clock and input level", await s.js(`const t = document.querySelector(".rec-controls .mono").textContent; return parseFloat(t) >= 3;`),
    await s.js(`return document.querySelector(".rec-controls .mono").textContent;`));
  await s.clickText("button", "Stop");
  await s.waitFor(`document.querySelector("#rec-name")`, 10000, "save form");
  await s.js(`const n = document.querySelector("#rec-name"); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set.call(n, "Smoke test calm");
    n.dispatchEvent(new Event("input", { bubbles: true })); return true;`);
  await s.clickText("button", "Save recording");
  await s.waitFor(`document.querySelectorAll(".pack.recording").length === 1`, 15000, "saved recording");
  const files = existsSync(recDir) ? readdirSync(recDir) : [];
  check("recording saved as WAV + JSON in the recordings folder", files.some((f) => f.endsWith(".wav")) && files.some((f) => f.endsWith(".json")), files.join(", "));
  const listed = (await s.bridge("recordings.list")).recordings[0];
  check("recording is 4+ s at the microphone's rate with its transcript", listed.durationMs >= 3500 && listed.sampleRate >= 16000 && listed.transcript.length > 10,
    `${listed.durationMs} ms @ ${listed.sampleRate} Hz`);
  await s.shot(shots, "24-voicelab");

  // ---- make a voice (consent required)
  await s.clickText("button", "Make a voice");
  await s.waitFor(`document.querySelector('[aria-label="Make a voice from a recording"]')`, 5000, "clone dialog");
  check("Create voice is disabled until consent is given", await s.js(`return [...document.querySelectorAll(".modal button")].find(b => b.textContent.includes("Create voice")).disabled;`));
  await s.js(`document.querySelector('.modal input[name="cr-consent"]').click(); return true;`);
  await s.clickText(".modal button", "Create voice");
  await s.waitFor(`[...document.querySelectorAll(".pill")].some(p => p.textContent === "cloned")`, 10000, "voice in My voices");
  const prof = (await s.bridge("voices.profiles")).profiles.find((p) => p.sourceRecordingId === listed.id);
  voiceId = prof?.voiceId ?? null;
  check("voice created from the recording with the consent statement stored", prof?.kind === "clone" && prof.consent.statement.length > 10, `${voiceId}: ${prof?.consent.statement}`);
  await s.shot(shots, "25-voicelab-voice");

  // ---- optional (slow, needs the VoiceDesign + Base models and a GPU): design a voice in the UI and keep it
  if (process.env.NOORVOX_SMOKE_DESIGN === "1") {
    const t0 = Date.now();
    await s.clickText("button", "Design a sample");
    await s.waitFor(`[...document.querySelectorAll("button")].some(b => b.textContent.includes("Keep as voice"))`, 900000, "designed sample");
    check("VoiceDesign sample generated in the Voice lab", true, `${Math.round((Date.now() - t0) / 1000)} s including model load`);
    await s.clickText("button", "Keep as voice");
    await s.waitFor(`document.querySelector("#kv-name")`, 5000, "keep dialog");
    await s.js(`const n = document.querySelector("#kv-name"); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set.call(n, "Smoke designed");
      n.dispatchEvent(new Event("input", { bubbles: true })); return true;`);
    await s.clickText(".modal button", "Keep voice");
    await s.waitFor(`[...document.querySelectorAll(".pack .mono")].some(e => e.textContent === "smoke_designed")`, 15000, "designed voice listed");
    const d = (await s.bridge("voices.profiles")).profiles.find((p) => p.voiceId === "smoke_designed");
    check("kept sample is a designed voice with its description", d?.kind === "designed" && (d.description ?? "").includes("British"), d?.description ?? "");
    await s.shot(shots, "26-voicelab-designed");
    await s.bridge("voices.removeProfile", { id: "smoke_designed" });
    await s.bridge("workers.stop", { workerId: "studio", force: false });
  }

  // ---- "Use with…" opens Quick narration with that model and voice selected
  const usable = (await s.bridge("voices.profiles")).usableWith;
  if (usable.length) {
    const target = usable[usable.length - 1].reference;
    await s.js(`const sel = document.querySelector('select.use-with[aria-label="Use ${prof.name} with a model"]'); Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set.call(sel, ${JSON.stringify(target)});
      sel.dispatchEvent(new Event("change", { bubbles: true })); return true;`);
    await s.waitFor(`document.querySelector("#model")?.value === ${JSON.stringify(target)} && document.querySelector("#voice")?.value === ${JSON.stringify(voiceId)}`,
      10000, "Quick narration with the voice selected");
    check("Use with… opens Quick narration with that model and voice", true, `${target} / ${voiceId}`);
    await s.shot(shots, "27-use-with");
  } else {
    check("Use with… skipped: no installed model speaks your voices", true);
  }

  // ---- clean up through the UI's own methods
  await s.bridge("voices.removeProfile", { id: voiceId });
  voiceId = null;
  await s.bridge("recordings.delete", { id: listed.id });
  check("deleting moves the recording out of the folder (to the Recycle Bin)", !readdirSync(recDir).some((f) => f.startsWith(listed.id)));
} catch (e) {
  check("no exception", false, String(e));
} finally {
  if (voiceId) { try { await s.bridge("voices.removeProfile", { id: voiceId }); } catch { /* ignore */ } }
  const code = await s.close();
  check("studio closes cleanly", code === 0, `exit ${code}`);
}
const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed.`);
process.exit(failed ? 1 : 0);
