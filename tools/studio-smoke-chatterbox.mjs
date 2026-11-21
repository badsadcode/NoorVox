// NoorVox Studio smoke test for Chatterbox (0.4.0): isolated home, real models + engine, real GPU.
// Quick narration with chatterbox-english: the emotion and pace sliders exist (no speed slider), a take is generated through the
// studio's own bridge with emotion/pace set, the take reports them and the watermark, and closing leaves no engine process.
// Usage: node tools/studio-smoke-chatterbox.mjs <NoorVoxStudio.exe> <models-dir> <scratch-dir> <screenshot-dir>
import { mkdirSync, writeFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { createChecks, launchStudio, pidAlive, sleep } from "./smoke-lib.mjs";

const [studioExe, modelsDir, scratch, shots] = process.argv.slice(2);
if (!shots) { console.error("usage: node tools/studio-smoke-chatterbox.mjs <NoorVoxStudio.exe> <models-dir> <scratch-dir> <screenshot-dir>"); process.exit(2); }
if (existsSync(scratch)) { console.error(`${scratch} already exists; pass a new scratch dir`); process.exit(2); }
const home = join(scratch, "home");
mkdirSync(home, { recursive: true });
mkdirSync(shots, { recursive: true });
writeFileSync(join(home, "settings.json"), JSON.stringify({ schemaVersion: 1, paths: { models: modelsDir }, workers: { idleTimeoutSeconds: 600, stopOnExit: true } }, null, 2));
const { results, check } = createChecks();
const MODEL = "chatterbox-english@1.0";
const s = await launchStudio(studioExe, { port: 9340, page: "narrate", env: { NOORVOX_HOME: home } });
let enginePid = null;
try {
  await s.waitFor(`document.querySelector("#model")`, 30000, "model picker");
  check("without a default, Quick narration starts on the recommended model (Kokoro)", await s.js(`return document.querySelector("#model").value.startsWith("kokoro");`));
  await s.js(`const m = document.querySelector("#model"); Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set.call(m, "${MODEL}");
    m.dispatchEvent(new Event("change", { bubbles: true })); return true;`);
  await s.waitFor(`document.querySelector("#emotion")`, 15000, "emotion slider");
  check("Chatterbox shows emotion and pace, no speed or style", await s.js(`return !!document.querySelector("#emotion") && !!document.querySelector("#pace") && !document.querySelector("#speed") && !document.querySelector("#style");`));
  const t0 = Date.now();
  const r = await s.bridge("narration.generate", { text: "Hold the line! If they want this bridge, they will have to take it from us.", voice: "default",
    model: MODEL, speed: 1, style: null, seed: 5, language: null, emotion: 1.2, pace: 0.3 });
  check("a take is generated through the studio bridge", r.result.durationMs > 1000, `${(r.result.durationMs / 1000).toFixed(1)} s audio, ${Math.round((Date.now() - t0) / 1000)} s incl. load`);
  check("the take records emotion, pace, seed and the watermark", r.result.emotion === 1.2 && r.result.pace === 0.3 && r.result.seed === 5 && r.result.watermark === "perth",
    JSON.stringify({ emotion: r.result.emotion, pace: r.result.pace, seed: r.result.seed, watermark: r.result.watermark }));
  let bad = null;
  try { await s.bridge("narration.generate", { text: "hi", voice: "default", model: MODEL, speed: 1, emotion: 0.5, pace: 0 }); } catch (e) { bad = String(e); }
  check("pace 0 is refused before it reaches the model", bad?.includes("Pace must be between"), bad ?? "accepted");
  const w = (await s.bridge("workers.list")).workers.find((x) => x.workerId === "studio");
  enginePid = w?.resources?.sidecar?.pid ?? null;
  check("worker runs Chatterbox on CUDA with measured GPU memory", w?.backend === "chatterbox" && w.executionProvider === "CUDA" && w.resources?.gpuMemory?.reservedBytes > 0,
    `${w?.backend} ${w?.executionProvider} ${(w?.resources?.gpuMemory?.reservedBytes / 2 ** 30).toFixed(2)} GB`);
  await s.shot(shots, "27-narrate-chatterbox");
} catch (e) {
  check("no exception", false, String(e));
} finally {
  const code = await s.close();
  check("studio closes cleanly", code === 0, `exit ${code}`);
  await sleep(3000);
  if (enginePid) check("the Python engine is gone after close", !pidAlive(enginePid), `engine pid ${enginePid}`);
}
const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed.`);
process.exit(failed ? 1 : 0);
