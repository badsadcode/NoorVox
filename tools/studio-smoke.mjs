// NoorVox Studio scripted smoke test: drives the REAL studio (real C# host, real worker, real model) through the
// WebView2 DevTools protocol, then closes it the way a user would and checks nothing is left running.
//
// Usage: node tools/studio-smoke.mjs <path-to-NoorVoxStudio.exe> <screenshot-dir>
// Requires an installed model. Remote debugging is enabled only for this run, bound to 127.0.0.1.
import { spawn, execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [exe, shotDir] = process.argv.slice(2);
if (!exe || !shotDir) { console.error("usage: node tools/studio-smoke.mjs <NoorVoxStudio.exe> <screenshot-dir>"); process.exit(2); }
mkdirSync(shotDir, { recursive: true });
const PORT = 9333;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
const check = (name, ok, detail = "") => { results.push({ name, ok, detail }); console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  — " + detail : ""}`); };

const studio = spawn(exe, ["--page", "narrate"], {
  env: { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${PORT} --remote-allow-origins=* --autoplay-policy=no-user-gesture-required` },
  stdio: "ignore",
});

async function target() {
  for (let i = 0; i < 60; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
      const t = list.find((x) => x.type === "page" && x.url.startsWith("https://app.noorvox.local/"));
      if (t) return t;
    } catch { /* not up yet */ }
    await sleep(500);
  }
  throw new Error("studio page did not appear");
}

const t = await target();
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let seq = 0;
const pending = new Map();
ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); } };
const send = (method, params = {}) => new Promise((r) => { const id = ++seq; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
const js = async (expr) => {
  const r = await send("Runtime.evaluate", { expression: `(async () => { ${expr} })()`, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description ?? "eval failed");
  return r.result?.result?.value;
};
const waitFor = async (expr, timeoutMs, label) => {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) { if (await js(`return !!(${expr});`)) return true; await sleep(250); }
  throw new Error(`timeout waiting for ${label}`);
};
const shot = async (name) => {
  const r = await send("Page.captureScreenshot", { format: "png" });
  writeFileSync(join(shotDir, `${name}.png`), Buffer.from(r.result.data, "base64"));
};
const go = async (page) => { await js(`location.hash = "#/${page}";`); await sleep(1200); };
const clickText = (sel, text) => js(`const b = [...document.querySelectorAll(${JSON.stringify(sel)})].find(e => e.textContent.includes(${JSON.stringify(text)})); if (!b) throw new Error("no ${text}"); b.click(); return true;`);
const workers = () => js(`return await new Promise((res) => { const id = "smoke" + Math.random(); const h = (e) => { if (e.data.id === id) { chrome.webview.removeEventListener("message", h); res(e.data.result.workers); } }; chrome.webview.addEventListener("message", h); chrome.webview.postMessage(JSON.stringify({ id, method: "workers.list", params: {} })); });`);

try {
  await send("Page.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
  await waitFor(`[...document.querySelectorAll("button")].some(b => b.textContent.includes("Generate narration") && !b.disabled)`, 20000, "Generate button");
  check("UI loads with real voices", await js(`return document.querySelectorAll("#voice option").length;`) >= 20);
  await shot("01-narrate-empty");

  const before = await workers();
  check("no studio engine before first generate", !before.some((w) => w.workerId === "studio"), `${before.length} worker(s)`);

  await js(`const s = document.querySelector("#voice"); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set; set.call(s, "bm_george"); s.dispatchEvent(new Event("change", { bubbles: true })); return true;`);
  const t0 = Date.now();
  await clickText("button", "Generate narration");
  await waitFor(`document.querySelector(".take")`, 120000, "take");
  const meta = await js(`return document.querySelector(".take .take-meta").textContent;`);
  check("real narration generated in GUI", /measured/.test(meta), `${meta} (${((Date.now() - t0) / 1000).toFixed(1)} s incl. engine start)`);
  await sleep(1500);
  await shot("02-narrate-take");

  const after = await workers();
  const eng = after.find((w) => w.workerId === "studio");
  check("engine is ready with model loaded", eng?.state === "ready" && !!eng.loadedModel, `state=${eng?.state} pid=${eng?.pid}`);
  check("studio engine is bound to the studio (stop-on-exit default)", eng?.boundToLauncher === true);

  await go("monitor");
  await sleep(2500);
  await shot("03-monitor");
  check("monitor shows Ready state", await js(`return document.querySelector(".worker-card .state")?.textContent ?? "";`) === "Ready");

  await clickText("button", "Unload model");
  await waitFor(`document.querySelector(".worker-card .state")?.textContent === "Running · no model"`, 30000, "unloaded state");
  const recycled = (await workers()).find((w) => w.workerId === "studio");
  check("unload recycles to a new process", recycled && recycled.pid !== eng.pid && !recycled.loadedModel,
    `pid ${eng.pid} → ${recycled?.pid}, memory ${(eng.resources.workingSetBytes / 1e6).toFixed(0)} MB → ${(recycled?.resources.workingSetBytes / 1e6).toFixed(0)} MB`);

  await go("models"); await shot("04-models");
  await go("settings"); await shot("05-settings");

  for (const theme of ["daylight", "tape", "thalassa", "graphite", "lavender"]) {
    await js(`document.documentElement.dataset.theme = "${theme}"; return true;`);
    await go("narrate"); await sleep(400);
    await shot(`06-theme-${theme}`);
  }
  await js(`document.documentElement.dataset.theme = "nocturne"; return true;`);

  // Generate again (engine restarts with model), then quit like a user: graceful WM_CLOSE.
  await go("narrate");
  await clickText("button", "Generate narration");
  await waitFor(`document.querySelectorAll(".take").length >= 2`, 120000, "second take");
  const live = (await workers()).find((w) => w.workerId === "studio");
  check("second generate reloads engine", live?.state === "ready", `pid=${live?.pid}`);
  ws.close();

  execFileSync("taskkill", ["/PID", String(studio.pid)]); // WM_CLOSE → OnClosing → stop-on-exit policy
  const exitCode = await Promise.race([new Promise((r) => studio.on("exit", r)), sleep(30000).then(() => "timeout")]);
  check("studio closes cleanly", exitCode !== "timeout", `exit ${exitCode}`);
  await sleep(1000);
  let alive = false;
  try { process.kill(live.pid, 0); alive = true; } catch { alive = false; }
  check("no worker left consuming resources after close", !alive, `worker pid ${live?.pid} ${alive ? "STILL RUNNING" : "gone"}`);
} catch (e) {
  check("smoke test ran to completion", false, e.message);
  try { studio.kill(); } catch { /* ignore */ }
}

const failed = results.filter((r) => !r.ok).length;
writeFileSync(join(shotDir, "smoke-results.json"), JSON.stringify({ when: new Date().toISOString(), results }, null, 2));
console.log(`\n${results.length - failed}/${results.length} checks passed. Screenshots: ${shotDir}`);
process.exit(failed ? 1 : 0);
