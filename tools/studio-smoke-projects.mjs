// NoorVox Studio projects smoke test (Phase 3): isolated NoorVox home, real models, real worker.
// Prepares a documentary project with the CLI, then in the studio: open it, edit a segment, generate a scene,
// generate the project and stop it mid-job, resume, make a new take and approve it, close, check nothing is left running.
//
// Usage: node tools/studio-smoke-projects.mjs <NoorVoxStudio.exe> <noorvox.exe> <models-dir> <scratch-dir> <screenshot-dir>
import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync, existsSync } from "node:fs";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { createChecks, launchStudio, pidAlive, sleep } from "./smoke-lib.mjs";

const [studioExe, cliExe, modelsDir, scratch, shots] = process.argv.slice(2);
if (!shots) { console.error("usage: node tools/studio-smoke-projects.mjs <NoorVoxStudio.exe> <noorvox.exe> <models-dir> <scratch-dir> <screenshot-dir>"); process.exit(2); }
if (existsSync(scratch)) { console.error(`${scratch} already exists; pass a new scratch dir`); process.exit(2); }
const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const home = join(scratch, "home");
const projectDir = join(scratch, "NoorEngine Documentary");
mkdirSync(home, { recursive: true });
mkdirSync(shots, { recursive: true });
const { results, check } = createChecks();

const cli = (...args) => {
  const out = execFileSync(cliExe, [...args, "--json", "--quiet"], { env: { ...process.env, NOORVOX_HOME: home }, encoding: "utf8" });
  return JSON.parse(out).data;
};

// Isolated home: real models (read-only use), everything else inside the scratch dir; the project is on the allow-list.
writeFileSync(join(home, "settings.json"), JSON.stringify({
  schemaVersion: 1, paths: { models: modelsDir }, workers: { idleTimeoutSeconds: 300, stopOnExit: true },
  studio: { theme: "nocturne", recentProjects: [projectDir] },
}, null, 2));
cli("projects", "create", "--path", projectDir, "--title", "NoorEngine Documentary", "--kind", "documentary", "--voice", "bm_george");
cli("projects", "import", "--project", projectDir, "--file", join(repo, "examples", "projects", "documentary.md"));

const s = await launchStudio(studioExe, { port: 9334, page: "projects", env: { NOORVOX_HOME: home } });
const segStatus = (id) => s.js(`return document.querySelector('article.segment[aria-label="${id}"] .segment-top .pill')?.textContent ?? "";`);
const sceneCount = (cls) => s.js(`return document.querySelectorAll("article.segment.${cls}").length;`);

try {
  await s.waitFor(`document.querySelector(".project-open")`, 15000, "project card");
  await s.shot(shots, "10-projects");
  check("projects page lists the project", (await s.js(`return document.querySelector(".project-open b").textContent;`)) === "NoorEngine Documentary");

  await s.clickText(".project-open", "NoorEngine Documentary");
  await s.waitFor(`document.querySelectorAll("article.segment").length >= 2`, 15000, "workspace segments");
  check("workspace shows the imported structure",
    (await s.js(`return document.querySelectorAll(".tree-scene").length;`)) === 3, `${await s.js(`return document.querySelectorAll(".tree-scene").length;`)} scenes`);

  // Edit the first segment (autosave) and check the script on disk.
  await s.js(`const t = document.querySelector('article.segment[aria-label="ep01.s01.p001"] textarea');
    const set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set;
    set.call(t, "Every engine starts somewhere. This one started on a rainy Tuesday."); t.dispatchEvent(new Event("input", { bubbles: true })); t.dispatchEvent(new Event("blur")); return true;`);
  await sleep(2000);
  const onDisk = cli("projects", "show", "--project", projectDir, "--segments").sections[0].scenes[0].segments[0].text;
  check("segment edit autosaves to the script file", onDisk.includes("rainy Tuesday"), onDisk);

  // Generate this scene (2 segments) with auto-approve.
  await s.clickText("button", "Generate missing");
  await s.waitFor(`document.querySelectorAll("article.segment.approved").length === 2`, 120000, "scene approved");
  check("scene generated and auto-approved in the GUI", (await sceneCount("approved")) === 2);
  await sleep(800);
  await s.shot(shots, "11-project-scene");

  // Whole project, then Stop now after the first new segment finishes.
  await s.js(`[...document.querySelectorAll('input[name="scope"]')][2].click(); return true;`);
  await s.clickText("button", "Generate missing");
  await s.waitFor(`/[1-9] of \\d+ done/.test(document.querySelector(".side-panel").textContent)`, 120000, "first project item");
  await s.clickText("button", "Stop now");
  await s.waitFor(`[...document.querySelectorAll("button")].some(b => b.textContent.trim() === "Resume")`, 60000, "resume offer");
  const jobs = cli("jobs", "list", "--project", projectDir).jobs;
  check("Stop now leaves a resumable, interrupted job", jobs[0].state === "interrupted" && jobs[0].done < jobs[0].total, `${jobs[0].state} ${jobs[0].done}/${jobs[0].total}`);
  await s.shot(shots, "12-project-interrupted");

  await s.clickText("button", "Resume");
  await s.waitFor(`![...document.querySelectorAll("button")].some(b => b.textContent.includes("Stop now")) && document.querySelector(".side-panel").textContent.includes("Last job")`, 180000, "job finished");
  const summary = cli("projects", "show", "--project", projectDir);
  check("resume completes the whole project", summary.counts.approved === summary.segments, JSON.stringify(summary.counts));

  // New take for one segment, then approve it explicitly; approval moves, old take stays.
  await s.clickText(".tree-scene", "The First Image");
  await s.waitFor(`document.querySelector('article.segment[aria-label="ep01.s02.p001"]')`, 10000, "scene 2");
  await s.js(`document.querySelector('article.segment[aria-label="ep01.s02.p001"] .segment-actions .btn:nth-child(2)').click(); return true;`);
  await s.waitFor(`document.querySelector('article.segment[aria-label="ep01.s02.p001"] .segment-actions').textContent.includes("Takes (2)")`, 120000, "second take");
  await s.js(`[...document.querySelectorAll('article.segment[aria-label="ep01.s02.p001"] .segment-actions button')].find(b => b.textContent.startsWith("Takes")).click(); return true;`);
  await s.waitFor(`document.querySelectorAll('article.segment[aria-label="ep01.s02.p001"] .take-row').length === 2`, 10000, "takes list");
  check("auto-approve did not replace the existing approval", (await s.js(`return document.querySelectorAll('article.segment[aria-label="ep01.s02.p001"] .take-row.approved').length;`)) === 1);
  await s.js(`[...document.querySelectorAll('article.segment[aria-label="ep01.s02.p001"] .take-row button')].find(b => b.textContent === "Approve").click(); return true;`);
  await sleep(1500);
  const takes = cli("takes", "list", "--project", projectDir, "--segment", "ep01.s02.p001");
  check("explicit approval moves to the new take; old take kept", takes.takes.length === 2 && takes.takes[1].approved && !takes.takes[0].approved);
  await s.shot(shots, "13-project-takes");

  // Play a take through the UI the way a user does (regression: takes host failed after opening a project).
  await s.js(`document.querySelector('article.segment[aria-label="ep01.s02.p001"] .take-row button').click(); return true;`);
  await sleep(1500);
  const toast = await s.js(`return document.querySelector(".toast.err")?.textContent ?? "";`);
  const probe = await s.js(`const u = document.querySelector('article.segment[aria-label="ep01.s02.p001"]') && ${JSON.stringify("https://takes.noorvox.local/ep01.s02.p001/")} + ${JSON.stringify("")};
    const takes = await new Promise((res) => { const id = "p" + Math.random(); const h = (e) => { if (e.data.id === id) { chrome.webview.removeEventListener("message", h); res(e.data.result); } };
      chrome.webview.addEventListener("message", h); chrome.webview.postMessage(JSON.stringify({ id, method: "projects.takes", params: { path: ${JSON.stringify(projectDir)}, segmentId: "ep01.s02.p001" } })); });
    return await new Promise((res) => { const a = new Audio(takes[0].mediaUrl); a.onloadedmetadata = () => res("ok " + a.duration.toFixed(2)); a.onerror = () => res("error " + (a.error && a.error.message)); setTimeout(() => res("timeout"), 8000); });`);
  check("takes play in the workspace", probe.startsWith("ok") && !/Failed to load/.test(toast), `${probe}${toast ? " · toast: " + toast : ""}`);

  const studioWorker = (await s.bridge("workers.list")).workers.find((w) => w.workerId === "studio");
  const exit = await s.close();
  check("studio closes cleanly", exit !== "timeout", `exit ${exit}`);
  await sleep(1000);
  check("no worker left after close", !studioWorker || !pidAlive(studioWorker.pid), `worker pid ${studioWorker?.pid}`);
} catch (e) {
  check("projects smoke test ran to completion", false, e.message);
  s.kill();
}

const failed = results.filter((r) => !r.ok).length;
writeFileSync(join(shots, "smoke-projects-results.json"), JSON.stringify({ when: new Date().toISOString(), results }, null, 2));
console.log(`\n${results.length - failed}/${results.length} checks passed.`);
process.exit(failed ? 1 : 0);
