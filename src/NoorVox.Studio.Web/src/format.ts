export const fmtBytes = (b: number | null | undefined) => {
  if (b == null) return "—";
  if (b >= 1e12) return `${(b / 1e12).toFixed(1)} TB`;
  if (b >= 1e9) return `${(b / 1e9).toFixed(1)} GB`;
  if (b >= 1e6) return `${(b / 1e6).toFixed(0)} MB`;
  if (b >= 1e3) return `${(b / 1e3).toFixed(0)} KB`;
  return `${b} B`;
};

export const fmtDuration = (ms: number) => {
  const total = Math.max(0, ms) / 1000;
  const m = Math.floor(total / 60);
  const s = total - m * 60;
  return m > 0 ? `${m}:${s.toFixed(1).padStart(4, "0")}` : `${s.toFixed(1)} s`;
};

export const fmtClock = (seconds: number) => {
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${m}:${String(s).padStart(2, "0")}`;
};

export const fmtUptime = (seconds?: number) => {
  if (seconds == null) return "—";
  if (seconds < 60) return `${Math.floor(seconds)} s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min`;
  return `${Math.floor(seconds / 3600)} h ${Math.floor((seconds % 3600) / 60)} min`;
};

export const shortHash = (h: string) => (h.length > 12 ? `${h.slice(0, 8)}…${h.slice(-4)}` : h);

/** Pre-generation estimate only (≈150 words/min at 1.0×). Real durations always come from the generated audio. */
export const estimateSeconds = (words: number, speed: number) => (words / 150) * 60 / speed;

export const wordCount = (t: string) => (t.trim() ? t.trim().split(/\s+/).length : 0);

/** "1 episode", "6 segments" (English; labels come from the project template). */
export const plural = (n: number, word: string) => `${n} ${word.toLowerCase()}${n === 1 ? "" : "s"}`;
