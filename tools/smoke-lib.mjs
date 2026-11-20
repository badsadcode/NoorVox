// Shared helpers for NoorVox Studio smoke tests: launch the real studio with WebView2 remote debugging (127.0.0.1,
// test runs only), talk the DevTools protocol, click/inspect/screenshot, call the bridge, record checks.
import { spawn, execFileSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import { join } from "node:path";

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function createChecks() {
  const results = [];
  const check = (name, ok, detail = "") => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  — " + detail : ""}`); };
  return { results, check };
}

export async function launchStudio(exe, { port, page = "narrate", env = {}, browserArgs = "" }) {
  const studio = spawn(exe, ["--page", page], {
    env: { ...process.env, ...env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port} --remote-allow-origins=* --autoplay-policy=no-user-gesture-required ${browserArgs}`.trim() },
    stdio: "ignore",
  });
  let target;
  for (let i = 0; i < 180 && !target; i++) { // up to 90 s: a fresh WebView2 profile can be slow on first start
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      target = list.find((x) => x.type === "page" && x.url.startsWith("https://app.noorvox.local/"));
    } catch { /* not up yet */ }
    if (!target) await sleep(500);
  }
  if (!target) throw new Error("studio page did not appear");

  const ws = new WebSocket(target.webSocketDebuggerUrl);
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
  await send("Page.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });

  return {
    studio, send, js, waitFor,
    shot: async (dir, name) => { const r = await send("Page.captureScreenshot", { format: "png" }); writeFileSync(join(dir, `${name}.png`), Buffer.from(r.result.data, "base64")); },
    go: async (page) => { await js(`location.hash = "#/${page}";`); await sleep(1200); },
    clickText: (sel, text) => js(`const b = [...document.querySelectorAll(${JSON.stringify(sel)})].find(e => e.textContent.includes(${JSON.stringify(text)}) && !e.disabled); if (!b) throw new Error("no clickable '${text}'"); b.click(); return true;`),
    bridge: (method, params = {}) => js(`return await new Promise((res, rej) => { const id = "smoke" + Math.random(); const h = (e) => { if (e.data.id === id) { chrome.webview.removeEventListener("message", h); e.data.ok ? res(e.data.result) : rej(new Error(e.data.error.message)); } }; chrome.webview.addEventListener("message", h); chrome.webview.postMessage(JSON.stringify({ id, method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} })); });`),
    /** Graceful close like a user (WM_CLOSE), returns exit code or "timeout". */
    close: async () => {
      ws.close();
      execFileSync("taskkill", ["/PID", String(studio.pid)]);
      return await Promise.race([new Promise((r) => studio.on("exit", r)), sleep(30000).then(() => "timeout")]);
    },
    kill: () => { try { studio.kill(); } catch { /* ignore */ } },
  };
}

export function pidAlive(pid) {
  try { process.kill(pid, 0); return true; } catch { return false; }
}
