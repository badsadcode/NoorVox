import { isHosted } from "./bridge";
import { StateBadge } from "./components/Common";
import { Box, Gear, Logo, Mic, Pulse } from "./components/Icons";
import { Models } from "./pages/Models";
import { Monitor } from "./pages/Monitor";
import { Narrate } from "./pages/Narrate";
import { Settings } from "./pages/Settings";
import { Setup } from "./pages/Setup";
import { useStudio, type Page } from "./studio";

const nav: { id: Page; label: string; icon: typeof Mic }[] = [
  { id: "narrate", label: "Narrate", icon: Mic },
  { id: "models", label: "Models & voices", icon: Box },
  { id: "monitor", label: "Monitor", icon: Pulse },
  { id: "settings", label: "Settings", icon: Gear },
];

export function App() {
  const { page, go, info, workers } = useStudio();
  const engine = workers.find((w) => w.workerId === "studio") ?? workers[0];

  return (
    <div className="shell">
      <nav className="rail" aria-label="Main">
        <div className="brand">
          <Logo />
          <div><b>NoorVox</b><span>Local AI Voice Studio</span></div>
        </div>
        {nav.map(({ id, label, icon: Icon }) => (
          <button key={id} className="nav-btn" aria-current={page === id ? "page" : undefined} onClick={() => go(id)}>
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
        {page === "narrate" && <Narrate />}
        {page === "models" && <Models />}
        {page === "monitor" && <Monitor />}
        {page === "settings" && <Settings />}
      </main>

      <footer className="statusbar" aria-label="Status">
        <button className="btn ghost small" onClick={() => go("monitor")} aria-label="Voice engine status, open monitor">
          <StateBadge state={engine?.state ?? "stopped"} />
        </button>
        <span className="sep" />
        <span>{engine?.loadedModel ? `Kokoro ${engine.loadedModel.version} · ${engine.executionProvider}` : engine ? "No model loaded · loads when you generate" : "Engine starts when you generate"}</span>
        <span className="sep" />
        <span>{workers.length} managed worker{workers.length === 1 ? "" : "s"}</span>
        <span className="grow" />
        <span>Offline synthesis</span>
        <span className="sep" />
        <span>NoorVox {info?.version ?? ""}{isHosted ? "" : " · browser preview (mock data)"}</span>
      </footer>
    </div>
  );
}
