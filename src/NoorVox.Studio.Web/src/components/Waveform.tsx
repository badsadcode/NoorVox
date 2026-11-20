import { useEffect, useRef, useState } from "react";

/** Peaks from a PCM16 WAV (the format NoorVox writes). Computed once per file, never per keystroke. */
async function loadPeaks(url: string, buckets: number): Promise<Float32Array> {
  const buf = await (await fetch(url)).arrayBuffer();
  const v = new DataView(buf);
  let off = 12, dataOff = -1, dataLen = 0, channels = 1;
  while (off + 8 <= buf.byteLength) {
    const id = String.fromCharCode(v.getUint8(off), v.getUint8(off + 1), v.getUint8(off + 2), v.getUint8(off + 3));
    const size = v.getUint32(off + 4, true);
    if (id === "fmt ") channels = v.getUint16(off + 10, true);
    if (id === "data") { dataOff = off + 8; dataLen = Math.min(size, buf.byteLength - dataOff); break; }
    off += 8 + size + (size & 1);
  }
  const peaks = new Float32Array(buckets);
  if (dataOff < 0) return peaks;
  const frames = Math.floor(dataLen / (2 * channels));
  const per = Math.max(1, Math.floor(frames / buckets));
  for (let b = 0; b < buckets; b++) {
    let peak = 0;
    const start = b * per;
    for (let i = start; i < Math.min(frames, start + per); i += 4) {
      const s = Math.abs(v.getInt16(dataOff + i * 2 * channels, true));
      if (s > peak) peak = s;
    }
    peaks[b] = peak / 32768;
  }
  return peaks;
}

export function Waveform({ url, progress, onSeek, label }: { url: string; progress: number; onSeek: (fraction: number) => void; label: string }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [peaks, setPeaks] = useState<Float32Array | null>(null);
  const [theme, setTheme] = useState(document.documentElement.dataset.theme);

  // Redraw with the new palette when the theme changes.
  useEffect(() => {
    const mo = new MutationObserver(() => setTheme(document.documentElement.dataset.theme));
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => mo.disconnect();
  }, []);

  useEffect(() => {
    let alive = true;
    loadPeaks(url, 240).then((p) => alive && setPeaks(p), () => alive && setPeaks(new Float32Array(240)));
    return () => { alive = false; };
  }, [url]);

  useEffect(() => {
    const c = canvas.current;
    if (!c || !peaks) return;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth, h = c.clientHeight;
    c.width = w * dpr; c.height = h * dpr;
    const g = c.getContext("2d")!;
    g.scale(dpr, dpr);
    const css = getComputedStyle(document.documentElement);
    const on = css.getPropertyValue("--wave").trim(), off = css.getPropertyValue("--wave-dim").trim();
    const max = Math.max(0.05, ...peaks);
    const bw = w / peaks.length;
    peaks.forEach((p, i) => {
      const bh = Math.max(2, (p / max) * (h - 4));
      g.fillStyle = i / peaks.length <= progress ? on : off;
      g.fillRect(i * bw, (h - bh) / 2, Math.max(1, bw - 1), bh);
    });
  }, [peaks, progress, theme]);

  return (
    <canvas ref={canvas} className="wave" role="slider" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(progress * 100)} tabIndex={0}
      onClick={(e) => { const r = e.currentTarget.getBoundingClientRect(); onSeek((e.clientX - r.left) / r.width); }}
      onKeyDown={(e) => { if (e.key === "ArrowRight") onSeek(Math.min(1, progress + 0.05)); if (e.key === "ArrowLeft") onSeek(Math.max(0, progress - 0.05)); }} />
  );
}
