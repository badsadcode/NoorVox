// Browser-only stand-in for the C# host, so the UI can be developed with `npm run dev`.
// Never used inside NoorVox Studio. Data is clearly fake (paths under X:\Mock).
import { emitLocal } from "./bridge";
import type { WorkerStatus } from "./types";

const voices = [
  ["af_heart", "en-us", "American English", "female"], ["af_bella", "en-us", "American English", "female"],
  ["am_adam", "en-us", "American English", "male"], ["am_michael", "en-us", "American English", "male"],
  ["bf_emma", "en-gb", "British English", "female"], ["bm_george", "en-gb", "British English", "male"],
  ["bm_fable", "en-gb", "British English", "male"], ["jf_alpha", "ja", "Japanese", "female"],
  ["ff_siwis", "fr", "French", "female"], ["if_sara", "it", "Italian", "female"],
].map(([id, language, languageName, gender]) => ({
  id, language, languageName, gender, displayName: id.split("_")[1].replace(/^./, (c) => c.toUpperCase()), packId: "kokoro-82m-v1.0-voices", kind: "pack",
}));

const KOKORO_CAPS = { speed: true, minSpeed: 0.5, maxSpeed: 2, style: false, cloning: false, blending: true, seed: false, requiresGpu: false, languages: [] as string[] };
const QWEN_LANGS = ["auto", "english", "chinese", "japanese", "korean", "german", "french", "russian", "portuguese", "spanish", "italian"];
const QWEN_CAPS = { speed: false, minSpeed: 1, maxSpeed: 1, style: true, cloning: false, blending: false, seed: true, requiresGpu: true, languages: QWEN_LANGS };
const qwenVoices = [["aiden", "Aiden", "male", "english"], ["ryan", "Ryan", "male", "english"], ["vivian", "Vivian", "female", "chinese"], ["ono_anna", "Ono Anna", "female", "japanese"]]
  .map(([id, displayName, gender, language]) => ({ id, displayName, gender, language, languageName: language[0].toUpperCase() + language.slice(1), packId: "built-in", kind: "built-in",
    description: "Mock built-in Qwen speaker." }));
const models = [
  { reference: "kokoro-82m@1.0", id: "kokoro-82m", displayName: "Kokoro 82M v1.0", capabilities: KOKORO_CAPS, isDefault: true, engineReady: true },
  { reference: "qwen3-tts-1.7b-customvoice@1.0", id: "qwen3-tts-1.7b-customvoice", displayName: "Qwen3-TTS 1.7B CustomVoice", capabilities: QWEN_CAPS, isDefault: false, engineReady: true },
  { reference: "qwen3-tts-1.7b-base@1.0", id: "qwen3-tts-1.7b-base", displayName: "Qwen3-TTS 1.7B Base (voice cloning)", capabilities: { ...QWEN_CAPS, style: false, cloning: true }, isDefault: false, engineReady: true },
];
const consentStatements = { "own-voice": "This recording is my own voice.", "speaker-permission": "I have the speaker's permission to create and use a synthetic copy of their voice." };
let kokoroProvider = "cpu";

let theme = "nocturne";
let stopOnExit = true;
let idle = 900;
let worker: WorkerStatus | null = null;
let cpu = 0;

function wav(seconds: number): string {
  const rate = 24000, n = Math.floor(seconds * rate);
  const buf = new ArrayBuffer(44 + n * 2), v = new DataView(buf);
  const s = (o: number, t: string) => [...t].forEach((c, i) => v.setUint8(o + i, c.charCodeAt(0)));
  s(0, "RIFF"); v.setUint32(4, 36 + n * 2, true); s(8, "WAVE"); s(12, "fmt "); v.setUint32(16, 16, true);
  v.setUint16(20, 1, true); v.setUint16(22, 1, true); v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true);
  v.setUint16(32, 2, true); v.setUint16(34, 16, true); s(36, "data"); v.setUint32(40, n * 2, true);
  for (let i = 0; i < n; i++) {
    const t = i / rate, env = Math.max(0, Math.sin(t * 3.1)) ** 2 * (0.4 + 0.6 * Math.abs(Math.sin(t * 0.7)));
    v.setInt16(44 + i * 2, Math.sin(t * 2 * Math.PI * (140 + 40 * Math.sin(t * 5))) * env * 12000 * (Math.random() * 0.5 + 0.5), true);
  }
  return URL.createObjectURL(new Blob([buf], { type: "audio/wav" }));
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms));

export async function mockCall(method: string, p: Record<string, any>): Promise<unknown> {
  await delay(120);
  switch (method) {
    case "app.info":
      return { version: "0.1.0-dev (mock)", processorCount: 16, firstRun: false, hasModel: true,
        settings: { theme, stopOnExit, idleTimeoutSeconds: idle, defaultVoice: null, kokoroProvider },
        home: { path: "X:\\Mock\\NoorVox", mode: "default", settingsFile: "X:\\Mock\\NoorVox\\settings.json" },
        workerExecutable: { path: "X:\\Mock\\NoorVox.Worker.exe", exists: true } };
    case "storage.get":
      return { home: "X:\\Mock", settingsFile: "X:\\Mock\\settings.json", paths: ["models", "cache", "temp", "projects", "exports", "webview"].map((kind) => ({
        kind, path: `X:\\Mock\\NoorVox\\${kind[0].toUpperCase()}${kind.slice(1)}`, source: "configured", drive: "X:\\", exists: true, freeBytes: 1.3e12 })) };
    case "voices.list":
      if (p.model?.startsWith("qwen3-tts-1.7b-base"))
        return { model: models[2].reference, modelName: models[2].displayName, defaultVoice: null, capabilities: models[2].capabilities, engineReady: true, voices: [], models, consentStatements };
      return p.model?.startsWith("qwen")
        ? { model: models[1].reference, modelName: models[1].displayName, defaultVoice: "aiden", capabilities: QWEN_CAPS, engineReady: true, voices: qwenVoices, models, consentStatements }
        : { model: models[0].reference, modelName: models[0].displayName, defaultVoice: "af_heart", capabilities: KOKORO_CAPS, engineReady: true, voices, models, consentStatements };
    case "voices.chooseReference":
      return { cancelled: false, token: "mock-token", fileName: "my-voice.wav", durationMs: 9400, sampleRate: 48000 };
    case "engines.list":
      return { engines: [
        { id: "qwen-tts", version: "1.0", displayName: "Qwen3-TTS engine (PyTorch + CUDA)", description: "Mock engine pack.", kind: "python", state: "installed",
          path: "X:\\Mock\\Models\\engines\\qwen-tts\\1.0", approxInstallBytes: 6e9, license: "mixed", source: "mock", requires: [], usedBy: ["qwen3-tts-1.7b-customvoice"],
          verified: { torch: "2.11.0+cu128", cuda: true, cudaVersion: "12.8", device: "Mock GPU" } },
        { id: "onnxruntime-cuda", version: "1.22.0", displayName: "ONNX Runtime CUDA provider (Kokoro on GPU)", description: "Mock.", kind: "native", state: "notInstalled",
          path: "X:\\Mock\\Models\\engines\\onnxruntime-cuda\\1.22.0", approxInstallBytes: 0, license: "MIT", source: "mock", requires: ["qwen-tts"], usedBy: ["kokoro-82m (GPU option)"], verified: null },
      ] };
    case "settings.set":
      if (p.theme) theme = p.theme;
      if (p.stopOnExit !== undefined) stopOnExit = p.stopOnExit;
      if (p.idleTimeoutSeconds !== undefined) idle = p.idleTimeoutSeconds;
      if (p.kokoroProvider) kokoroProvider = p.kokoroProvider;
      return mockCall("app.info", {});
    case "workers.list":
      if (worker) { cpu += worker.state === "generating" ? 900 : 5; worker = { ...worker, uptimeSeconds: (worker.uptimeSeconds ?? 0) + 2,
        resources: { ...worker.resources!, cpuTimeMs: cpu, sampledAt: new Date().toISOString() } }; }
      return { workers: worker ? [worker] : [], sampledAt: new Date().toISOString(), processorCount: 16 };
    case "workers.start":
      worker = { workerId: "studio", state: "ready", managed: true, pid: 24088, launchedBy: "studio", boundToLauncher: stopOnExit, uptimeSeconds: 4,
        backend: "kokoro-onnx", backendVersion: "KokoroSharp 0.8.4; ONNX Runtime 1.22.0", executionProvider: "CPU", jobsCompleted: 0, idleTimeoutSeconds: idle,
        loadedModel: { id: "kokoro-82m", version: "1.0", modelSha256: "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05", directory: "X:\\Mock\\Models\\kokoro-82m\\1.0", loadedAt: new Date().toISOString(), loadMs: 4005 },
        resources: { workingSetBytes: 600e6, privateBytes: 560e6, cpuTimeMs: cpu, sampledAt: new Date().toISOString(), source: "mock", gpu: "unavailable: CPU execution provider, no GPU in use" } };
      return worker;
    case "workers.unload":
      if (worker) worker = { ...worker, pid: 30112, state: "idle", loadedModel: undefined, resources: { ...worker.resources!, workingSetBytes: 48e6 } };
      return worker;
    case "workers.stop": case "workers.stopAll":
      worker = null; return [];
    case "narration.generate": {
      if (!worker) await mockCall("workers.start", {});
      emitLocal("narration.progress", { message: "Synthesizing…" });
      worker = { ...worker!, state: "generating", currentJob: { requestId: "r1", kind: "synthesize", startedAt: new Date().toISOString(), textChars: p.text.length, voiceId: p.voice } };
      const secs = Math.max(1.2, p.text.length / 15 / (p.speed || 1));
      await delay(900);
      worker = { ...worker!, state: "ready", currentJob: undefined, jobsCompleted: (worker!.jobsCompleted ?? 0) + 1 };
      return { mediaUrl: wav(secs), result: { outputPath: `X:\\Mock\\Temp\\studio\\previews\\${Date.now()}_${p.voice}.wav`, durationMs: Math.round(secs * 1000), durationSeconds: secs,
        sampleRate: 24000, channels: 1, bitsPerSample: 16, samples: Math.round(secs * 24000), bytes: Math.round(secs * 48000) + 44,
        text: { chars: p.text.length, sha256: "mock" }, voice: p.voice, speed: p.speed,
        model: { id: "kokoro-82m", version: "1.0", modelSha256: "0cfd5e79…", license: "Apache-2.0" }, backend: { id: "kokoro-onnx", executionProvider: "CPU" },
        worker: { id: "studio", reused: true, startedForRequest: false, stoppedAfter: false, stateAfter: "ready" },
        timings: { synthesisMs: 900, totalMs: 1010, realTimeFactor: 0.17 }, createdAt: new Date().toISOString(), generator: "NoorVox mock",
        style: p.style ?? null, seed: p.model?.startsWith("qwen") ? (p.seed ?? Math.floor(Math.random() * 2 ** 31)) : null, voiceKind: "pack" } };
    }
    case "narration.export":
      return { cancelled: false, path: "X:\\Mock\\Exports\\narration.wav", manifestPath: "X:\\Mock\\Exports\\narration.noorvox.json" };
    case "models.overview":
      return { modelsDir: "X:\\Mock\\Models", cacheDir: "X:\\Mock\\Cache", catalogVersion: "2026.11.20",
        installed: [{ id: "kokoro-82m", version: "1.0", reference: "kokoro-82m@1.0", displayName: "Kokoro 82M v1.0", license: "Apache-2.0",
          licenseUrl: "https://huggingface.co/hexgrad/Kokoro-82M", source: "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/tag/v2.0.0", origin: "download",
          installedAt: new Date().toISOString(), path: "X:\\Mock\\Models\\kokoro-82m\\1.0", modelSha256: "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05",
          sizeBytes: 354e6, voices: 54, capabilities: KOKORO_CAPS, engine: null, backend: "kokoro-onnx", customVoices: [], voicePacks: [{ id: "kokoro-82m-v1.0-voices", version: "1.0", displayName: "Kokoro v1.0 standard voices",
            description: "All 54 official Kokoro v1.0 voices.", voices: 54, downloadBytes: 65.8e6, license: "Apache-2.0", installed: true }] }],
        catalog: [{ id: "kokoro-82m", version: "1.0", displayName: "Kokoro 82M v1.0", description: "Recommended. Natural English narration plus 8 other languages. Runs well on CPU.",
          recommended: true, license: "Apache-2.0", licenseUrl: "https://huggingface.co/hexgrad/Kokoro-82M", downloadBytes: 391e6, installed: true, updateAvailable: false,
          capabilities: KOKORO_CAPS, engine: null, minVramGb: null, family: "kokoro", engineInstalled: true, engineBytes: 0 },
          { id: "qwen3-tts-1.7b-base", version: "1.0", displayName: "Qwen3-TTS 1.7B Base (voice cloning)", description: "Mock: speaks in a voice cloned from a short recording.",
          recommended: false, license: "Apache-2.0", licenseUrl: "https://huggingface.co/Qwen", downloadBytes: 4.5e9, installed: false, updateAvailable: false,
          capabilities: { ...QWEN_CAPS, style: false, cloning: true }, engine: "qwen-tts", minVramGb: 6, family: "qwen3-tts", engineInstalled: false, engineBytes: 6e9 }] };
    case "models.install": case "voices.installPack": case "models.verify":
      for (let i = 0; i <= 10; i++) { emitLocal("install.progress", { target: p.id, stage: "downloading", item: p.id, file: "kokoro.onnx", done: i * 32e6, total: 320e6 }); await delay(150); }
      emitLocal("install.progress", { target: p.id, stage: "done" });
      return { status: "installed" };
    default:
      return {};
  }
}
