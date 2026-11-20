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

// ---------------------------------------------------------------- projects (Phase 3) — mirrors Core/Projects

export interface ProjectKind {
  id: string; displayName: string; description: string; sectionLabel: string; sceneLabel: string; segmentLabel: string;
  sectionPrefix: string; usesSpeakers: boolean;
}

export type SegmentStatusId = "empty" | "missing" | "generated" | "approved" | "stale";

export interface SegmentView {
  segmentId: string; text: string; speakerId: string | null; voiceOverride: string | null; speedOverride: number | null;
  effectiveVoice: string; effectiveSpeed: number; spokenText: string; status: SegmentStatusId; takes: number;
  approvedTakeId: string | null; latestTakeId: string | null; durationMs: number | null; notes: string | null;
}
export interface SceneView { sceneId: string; title: string; segments: SegmentView[] }
export interface SectionView { sectionId: string; title: string; scenes: SceneView[]; counts: Partial<Record<SegmentStatusId, number>> }
export interface Speaker { speakerId: string; name: string; voiceId: string | null; speed: number | null }

export interface ProjectFile {
  projectId: string; title: string; kind: string; createdAt: string; modifiedAt: string;
  defaults: { model: string | null; voiceId: string | null; speed: number };
  speakers: Speaker[];
}

export interface JobItem { segmentId: string; state: string; takeId?: string; durationMs?: number; error?: string }
export interface GenerationJob {
  jobId: string; state: string; policy: string; autoApprove: boolean; createdAt: string; finishedAt?: string;
  items: JobItem[]; total: number; done: number; failedCount: number; remaining: number;
}

export interface ProjectOverview {
  root: string; project: ProjectFile; kind: ProjectKind; model: string | null; sections: SectionView[];
  counts: Partial<Record<SegmentStatusId, number>>; approvedDurationMs: number; segmentCount: number;
  recentJobs: GenerationJob[]; warnings: string[];
}

export interface RecentProject { path: string; exists: boolean; title: string; kind: string; sections: number; modifiedAt: string | null }

export interface TakeView {
  takeId: string; durationMs: number; voiceId: string; speed: number; createdAt: string; spokenText: string;
  approved: boolean; matchesCurrent: boolean; mediaUrl: string;
}

export interface JobProgressEvent {
  path: string; jobId: string; state: string; done: number; total: number; failed: number; currentSegmentId: string | null; lastItem: JobItem | null;
}
