// Shapes returned by NoorVox.Studio's bridge (camelCase JSON from C#). Keep in sync with Bridge/StudioApi.cs.

export type ThemeId = "nocturne" | "daylight" | "tape" | "thalassa" | "graphite" | "lavender";

export interface AppInfo {
  version: string;
  processorCount: number;
  firstRun: boolean;
  hasModel: boolean;
  settings: { theme: ThemeId; stopOnExit: boolean; idleTimeoutSeconds: number; defaultVoice: string | null };
  home: { path: string; mode: string; settingsFile: string };
  workerExecutable: { path: string; exists: boolean };
}

export interface StoragePath {
  kind: "models" | "cache" | "temp" | "projects" | "exports" | "webview";
  path: string;
  source: string;
  drive: string;
  exists: boolean;
  freeBytes: number | null;
}

export interface Voice {
  id: string;
  language: string;
  languageName: string;
  gender: "female" | "male" | "unknown";
  displayName: string;
  packId: string;
}

export interface VoicesResponse {
  model: string | null;
  modelName?: string;
  defaultVoice: string | null;
  capabilities?: { speed: boolean; minSpeed: number; maxSpeed: number; pitch: boolean; style: boolean; emotion: boolean };
  voices: Voice[];
}

export interface LoadedModel { id: string; version: string; modelSha256: string; directory: string; loadedAt: string; loadMs: number }
export interface CurrentJob { requestId: string; kind: string; startedAt: string; textChars: number; voiceId: string | null }

export type WorkerState =
  | "stopped" | "starting" | "idle" | "loading" | "ready" | "generating" | "stopping" | "error" | "unresponsive";

export interface WorkerStatus {
  workerId: string;
  state: WorkerState;
  managed: boolean;
  pid?: number;
  launchedBy?: string;
  boundToLauncher?: boolean;
  startedAt?: string;
  uptimeSeconds?: number;
  lastHeartbeatAt?: string;
  backend?: string;
  backendVersion?: string;
  executionProvider?: string;
  loadedModel?: LoadedModel;
  currentJob?: CurrentJob;
  jobsCompleted?: number;
  resources?: { workingSetBytes: number; privateBytes: number; cpuTimeMs: number; sampledAt: string; source: string; gpu: string };
  idleTimeoutSeconds?: number;
  logPath?: string;
  error?: string;
}

export interface WorkersResponse { workers: WorkerStatus[]; sampledAt: string; processorCount: number }

export interface NarrationResult {
  outputPath: string;
  durationMs: number;
  durationSeconds: number;
  sampleRate: number;
  channels: number;
  bitsPerSample: number;
  samples: number;
  bytes: number;
  text: { chars: number; sha256: string };
  voice: string;
  speed: number;
  model: { id: string; version: string; modelSha256: string; license: string };
  backend: { id: string; executionProvider: string };
  worker: { id: string; reused: boolean; startedForRequest: boolean; stoppedAfter: boolean; stateAfter: string };
  timings: { synthesisMs: number; totalMs: number; realTimeFactor: number };
  createdAt: string;
  generator: string;
  manifestPath?: string;
}

export interface GenerateResponse { result: NarrationResult; mediaUrl: string }

export interface VoicePackInfo {
  id: string; version: string; displayName: string; description: string; voices: number; downloadBytes: number; license: string; installed: boolean;
}

export interface InstalledModelInfo {
  id: string; version: string; reference: string; displayName: string; license: string; licenseUrl: string; source: string;
  origin: string; installedAt: string; path: string; modelSha256: string; sizeBytes: number; voices: number; voicePacks: VoicePackInfo[];
}

export interface CatalogModelInfo {
  id: string; version: string; displayName: string; description: string; recommended: boolean; license: string; licenseUrl: string;
  downloadBytes: number; installed: boolean; updateAvailable: boolean;
}

export interface ModelsOverview {
  modelsDir: string; cacheDir: string; catalogVersion: string; installed: InstalledModelInfo[]; catalog: CatalogModelInfo[];
}

export interface InstallProgress { target: string; stage: string; item?: string; file?: string; done?: number; total?: number }

export interface Take {
  key: string;
  text: string;
  voice: Voice | undefined;
  mediaUrl: string;
  result: NarrationResult;
  exportedPath?: string;
}
