import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { call, errorText, on } from "./bridge";
import type { AppInfo, Take, ThemeId, WorkerStatus, WorkersResponse } from "./types";

export type Page = "projects" | "project" | "narrate" | "models" | "monitor" | "settings" | "setup";

interface StudioState {
  info: AppInfo | null;
  refreshInfo: () => Promise<void>;
  page: Page;
  go: (p: Page) => void;
  workers: WorkerStatus[];
  /** CPU % per worker id, computed from two consecutive cumulative cpuTimeMs samples. */
  cpu: Record<string, number>;
  refreshWorkers: () => Promise<void>;
  setTheme: (t: ThemeId) => Promise<void>;
  notify: (text: string, isError?: boolean) => void;
  /** Session takes live here, not in the Narrate page, so navigating away never loses them. */
  takes: Take[];
  setTakes: React.Dispatch<React.SetStateAction<Take[]>>;
  /** The project open in the workspace (a path from the recent-projects allow-list). */
  projectPath: string | null;
  openProject: (path: string | null) => void;
}

const Ctx = createContext<StudioState>(null!);
export const useStudio = () => useContext(Ctx);

const pageFromHash = (): Page | null => {
  const h = location.hash.replace(/^#\/?/, "");
  return (["projects", "project", "narrate", "models", "monitor", "settings", "setup"] as Page[]).includes(h as Page) ? (h as Page) : null;
};

export function StudioProvider({ children }: { children: ReactNode }) {
  const [info, setInfo] = useState<AppInfo | null>(null);
  const [page, setPage] = useState<Page>(pageFromHash() ?? "narrate");
  const [workers, setWorkers] = useState<WorkerStatus[]>([]);
  const [cpu, setCpu] = useState<Record<string, number>>({});
  const [takes, setTakes] = useState<Take[]>([]);
  const [projectPath, setProjectPath] = useState<string | null>(() => { try { return localStorage.getItem("noorvox.project"); } catch { return null; } });
  const [toast, setToast] = useState<{ text: string; err: boolean } | null>(null);
  const [closing, setClosing] = useState<string | null>(null);
  const prev = useRef(new Map<string, { cpu: number; t: number }>());

  const notify = useCallback((text: string, err = false) => {
    setToast({ text, err });
    window.setTimeout(() => setToast((t) => (t?.text === text ? null : t)), err ? 7000 : 3500);
  }, []);

  const refreshInfo = useCallback(async () => {
    const i = await call<AppInfo>("app.info");
    setInfo(i);
    document.documentElement.dataset.theme = i.settings.theme;
  }, []);

  const refreshWorkers = useCallback(async () => {
    try {
      const r = await call<WorkersResponse>("workers.list");
      const next: Record<string, number> = {};
      for (const w of r.workers) {
        if (!w.resources) continue;
        const t = Date.parse(w.resources.sampledAt);
        const p = prev.current.get(w.workerId + w.pid);
        if (p && t > p.t) next[w.workerId] = Math.max(0, ((w.resources.cpuTimeMs - p.cpu) / (t - p.t)) * 100 / r.processorCount);
        prev.current.set(w.workerId + w.pid, { cpu: w.resources.cpuTimeMs, t });
      }
      setCpu(next);
      setWorkers(r.workers);
    } catch {
      /* polling must never spam errors */
    }
  }, []);

  const go = useCallback((p: Page) => {
    setPage(p);
    history.replaceState(null, "", `#/${p}`);
  }, []);

  const setTheme = useCallback(async (t: ThemeId) => {
    document.documentElement.dataset.theme = t;
    try { setInfo(await call<AppInfo>("settings.set", { theme: t })); } catch (e) { notify(errorText(e), true); }
  }, [notify]);

  useEffect(() => {
    refreshInfo().then(() => undefined, (e) => notify(errorText(e), true));
  }, [refreshInfo, notify]);

  useEffect(() => {
    if (info?.firstRun && !pageFromHash()) setPage("setup");
  }, [info?.firstRun]);

  useEffect(() => {
    refreshWorkers();
    const id = window.setInterval(() => { if (!document.hidden) refreshWorkers(); }, 2000);
    return () => window.clearInterval(id);
  }, [refreshWorkers]);

  useEffect(() => on<{ message: string }>("app.closing", (d) => setClosing(d.message)), []);

  useEffect(() => {
    const onHash = () => { const p = pageFromHash(); if (p) setPage(p); };
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  return (
    <Ctx.Provider value={{ info, refreshInfo, page, go, workers, cpu, refreshWorkers, setTheme, notify, takes, setTakes, projectPath,
      openProject: (path: string | null) => {
        setProjectPath(path);
        try { if (path) localStorage.setItem("noorvox.project", path); else localStorage.removeItem("noorvox.project"); } catch { /* ignore */ }
        if (path) go("project");
      } }}>
      {children}
      {toast && <div className={`toast ${toast.err ? "err" : ""}`} role="status">{toast.text}</div>}
      {closing && <div className="overlay"><div className="modal"><h3>Closing NoorVox Studio</h3><p className="muted">{closing}</p></div></div>}
    </Ctx.Provider>
  );
}
