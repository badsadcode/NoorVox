// NoorVox Studio Qwen smoke test (0.3.0): isolated home, real models + engines (read-only use), real GPU.
// Quick Narration with a Qwen CustomVoice model: model picker, only real controls (style/language/seed, no speed), a real take with
// a seed, the seed reproduces the audio, the Monitor shows measured GPU memory and the Python engine, and closing leaves nothing running.
//
// Usage: node tools/studio-smoke-qwen.mjs <NoorVoxStudio.exe> <models-dir> <scratch-dir> <screenshot-dir>
// Needs: qwen-tts engine + qwen3-tts-1.7b-customvoice + kokoro-82m installed in <models-dir>, an NVIDIA GPU. Loading takes minutes on an HDD.
import { mkdirSync, writeFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { createChecks, launchStudio, pidAlive, sleep } from "./smoke-lib.mjs";

const [studioExe, modelsDir, scratch, shots] = process.argv.slice(2);
if (!shots) { console.error("usage: node tools/studio-smoke-qwen.mjs <NoorVoxStudio.exe> <models-dir> <scratch-dir> <screenshot-dir>"); process.exit(2); }
if (existsSync(scratch)) { console.error(`${scratch} already exists; pass a new scratch dir`); process.exit(2); }
const home = join(scratch, "home");
mkdirSync(home, { recursive: true });
mkdirSync(shots, { recursive: true });
writeFileSync(join(home, "settings.json"), JSON.stringify({
  schemaVersion: 1, paths: { models: modelsDir }, workers: { idleTimeoutSeconds: 600, stopOnExit: true }, studio: { theme: "nocturne" },
}, null, 2));
const { results, check } = createChecks();
const MODEL = "qwen3-tts-1.7b-customvoice@1.0";
const s = await launchStudio(studioExe, { port: 9336, page: "narrate", env: { NOORVOX_HOME: home } });
let enginePid = null;

try {
  await s.waitFor(`document.querySelector("#model")`, 20000, "model picker");
  check("model picker lists Qwen", await s.js(`return [...document.querySelectorAll("#model option")].some(o => o.value === "${MODEL}");`));

  await s.js(`const m = document.querySelector("#model"); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set;
    set.call(m, "${MODEL}"); m.dispatchEvent(new Event("change", { bubbles: true })); return true;`);
  await s.waitFor(`document.querySelector("#style")`, 15000, "style box");
  check("Qwen shows style, language and seed but no speed slider",
    await s.js(`return !!document.querySelector("#style") && !!document.querySelector("#language") && !!document.querySelector('[aria-label="Seed"]') && !document.querySelector("#speed");`));

  await s.js(`const set = (el, v, proto) => { Object.getOwnPropertyDescriptor(proto.prototype, "value").set.call(el, v); el.dispatchEvent(new Event(el.tagName === "SELECT" ? "change" : "input", { bubbles: true })); };
    set(document.querySelector("textarea[aria-label='Text to narrate']"), "It started, as so many terrible ideas do, with a single image.", HTMLTextAreaElement);
    set(document.querySelector("#style"), "Calm, warm documentary narration with a hint of humour.", HTMLTextAreaElement);
    set(document.querySelector("#language"), "english", HTMLSelectElement);
    set(document.querySelector('[aria-label="Seed"]'), "2026", HTMLInputElement); return true;`);
  await sleep(500);
  const started = Date.now();
  await s.clickText("button", "Generate narration");
  // While it loads, the page must show what the engine is doing and a running clock (a long load must never look hung).
  const seen = new Set();
  while (!(await s.js(`return document.querySelectorAll("article.take").length >= 1;`))) {
    if (Date.now() - started > 900000) throw new Error("timeout waiting for first Qwen take");
    const status = await s.js(`return document.querySelector(".gen-status")?.textContent ?? "";`);
    for (const st of ["Starting Python", "Reading the model", "Preparing the GPU"]) if (status.includes(st)) seen.add(st);
    if (/ · \d+ s$/.test(status)) seen.add("clock");
    await sleep(400);
  }
  check("a Qwen take is generated in the studio", true, `${((Date.now() - started) / 1000).toFixed(0)} s including model load`);
  check("the load shows engine stages and a clock", seen.has("clock") && [...seen].some((x) => x !== "clock"), [...seen].join(", "));
  const meta1 = await s.js(`return document.querySelector("article.take .take-meta").textContent;`);
  check("the take shows its seed and style", meta1.includes("seed 2026") && meta1.includes("style:"), meta1);
  await s.shot(shots, "20-narrate-qwen");

  await s.clickText("button", "Generate narration");
  await s.waitFor(`document.querySelectorAll("article.take").length >= 2`, 300000, "second Qwen take");
  // The page can't read files; byte-identical output for a seed is covered by QwenTests. Here: same measured duration.
  const durations = await s.js(`return [...document.querySelectorAll("article.take .take-meta b")].slice(0, 2).map(b => b.textContent);`);
  check("same seed gives the same measured duration", durations.length === 2 && durations[0] === durations[1], durations.join(" vs "));

  await s.go("monitor");
  await s.waitFor(`[...document.querySelectorAll(".metric span")].some(e => e.textContent === "GPU memory")`, 15000, "GPU memory metric");
  const gpu = await s.js(`return [...document.querySelectorAll(".metric")].find(m => m.querySelector("span")?.textContent === "GPU memory")?.textContent ?? "";`);
  check("Monitor shows measured GPU memory and the engine process", gpu.includes("GB") &&
    await s.js(`return [...document.querySelectorAll(".metric span")].some(e => e.textContent === "Engine memory");`), gpu);
  const workers = await s.bridge("workers.list");
  enginePid = workers.workers[0]?.resources?.sidecar?.pid ?? null;
  check("worker reports CUDA and the Qwen backend", workers.workers[0]?.executionProvider === "CUDA" && workers.workers[0]?.backend === "qwen3-tts",
    `${workers.workers[0]?.backend} on ${workers.workers[0]?.executionProvider}, engine PID ${enginePid}`);
  await s.shot(shots, "21-monitor-qwen");

  await s.go("models");
  await sleep(1500);
  check("Models page lists the engine packs", await s.js(`return document.body.textContent.includes("Engine packs") && document.body.textContent.includes("Qwen3-TTS engine");`));
  await s.shot(shots, "22-models-engines");
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
