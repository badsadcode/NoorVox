import { isHosted } from "./bridge";
import { StateBadge } from "./components/Common";
import { Box, Folder, Gear, Logo, Mic, Pulse, Wave } from "./components/Icons";
import { TitleBar } from "./components/TitleBar";
import { VoiceLab } from "./pages/VoiceLab";
import { Models } from "./pages/Models";
import { Monitor } from "./pages/Monitor";
import { Narrate } from "./pages/Narrate";
import { Project } from "./pages/Project";
import { Projects } from "./pages/Projects";
import { Settings } from "./pages/Settings";
import { Setup } from "./pages/Setup";
import { useStudio, type Page } from "./studio";

const nav: { id: Page; label: string; icon: typeof Mic }[] = [
  { id: "projects", label: "Projects", icon: Folder },
  { id: "narrate", label: "Quick narration", icon: Mic },
  { id: "voicelab", label: "Voice lab", icon: Wave },
  { id: "models", label: "Models & voices", icon: Box },
  { id: "monitor", label: "Monitor", icon: Pulse },
  { id: "settings", label: "Settings", icon: Gear },
];

export function App() {
  const { page, go, info, workers } = useStudio();
  const engine = workers.find((w) => w.workerId === "studio") ?? workers[0];

  const current = nav.find((n) => n.id === page || (n.id === "projects" && page === "project"))?.label;
  return (
    <div className="frame">
    <TitleBar subtitle={current} />
    <div className="shell">
      <nav className="rail" aria-label="Main">
        <div className="brand">
          <Logo />
          <div><b>NoorVox</b><span>Local AI Voice Studio</span></div>
        </div>
        {nav.map(({ id, label, icon: Icon }) => (
          <button key={id} className="nav-btn" aria-label={label} title={label} aria-current={page === id || (id === "projects" && page === "project") ? "page" : undefined} onClick={() => go(id)}>
            <Icon /><span>{label}</span>
          </button>
        ))}
        <div className="rail-foot">
          <div>Write · Voice · Produce</div>
          <div>Locally.</div>
        </div>
      </nav>

      <main className="main">
        {page === "setup" && <Setup />}
        {page === "projects" && <Projects />}
        {page === "project" && <Project />}
        {page === "narrate" && <Narrate />}
        {page === "voicelab" && <VoiceLab />}
        {page === "models" && <Models />}
        {page === "monitor" && <Monitor />}
        {page === "settings" && <Settings />}
      </main>

      <footer className="statusbar" aria-label="Status">
        <button className="btn ghost small" onClick={() => go("monitor")} aria-label="Voice engine status, open monitor">
          <StateBadge state={engine?.state ?? "stopped"} />
        </button>
        <span className="sep" />
        <span>{engine?.loadedModel ? `${engine.loadedModel.id} ${engine.loadedModel.version} · ${engine.executionProvider}` : engine ? "No model loaded · loads when you generate" : "Engine starts when you generate"}</span>
        <span className="sep" />
        <span>{workers.length} managed worker{workers.length === 1 ? "" : "s"}</span>
        <span className="grow" />
        <span>Offline synthesis</span>
        <span className="sep" />
        <span>NoorVox {info?.version ?? ""}{isHosted ? "" : " · browser preview (mock data)"}</span>
      </footer>
    </div>
    </div>
  );
}
