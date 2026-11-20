import { useEffect, useState } from "react";
import { call, isHosted, on } from "../bridge";
import { Logo } from "./Icons";

/**
 * NoorVox's own title bar, drawn in the themed UI. The bar is a WebView2 non-client drag region (CSS `app-region: drag`),
 * so Windows still moves the window, maximizes on double-click and snaps; the buttons are excluded from the drag region.
 */
export function TitleBar({ subtitle }: { subtitle?: string }) {
  const [maximized, setMaximized] = useState(false);
  useEffect(() => {
    if (!isHosted) return;
    call<{ maximized: boolean }>("window.state").then((s) => setMaximized(s.maximized), () => undefined);
    return on<{ maximized: boolean }>("window.state", (s) => setMaximized(s.maximized));
  }, []);
  const act = (method: string) => () => { if (isHosted) call(method).catch(() => undefined); };

  return (
    <header className="titlebar" onDoubleClick={(e) => { if (e.target === e.currentTarget) act("window.toggleMaximize")(); }}>
      <div className="titlebar-title">
        <Logo size={18} />
        <b>NoorVox Studio</b>
        {subtitle && <span className="titlebar-sub">· {subtitle}</span>}
      </div>
      <div className="titlebar-buttons">
        <button className="tb-btn" aria-label="Minimize" title="Minimize" onClick={act("window.minimize")}>
          <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M0 5h10" stroke="currentColor" strokeWidth="1" /></svg>
        </button>
        <button className="tb-btn" aria-label={maximized ? "Restore" : "Maximize"} title={maximized ? "Restore" : "Maximize"} onClick={act("window.toggleMaximize")}>
          {maximized
            ? <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M2.5 0.5h7v7M0.5 2.5h7v7h-7z" fill="none" stroke="currentColor" strokeWidth="1" /></svg>
            : <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><rect x="0.5" y="0.5" width="9" height="9" fill="none" stroke="currentColor" strokeWidth="1" /></svg>}
        </button>
        <button className="tb-btn close" aria-label="Close" title="Close" onClick={act("window.close")}>
          <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M0.5 0.5l9 9M9.5 0.5l-9 9" stroke="currentColor" strokeWidth="1.1" /></svg>
        </button>
      </div>
    </header>
  );
}
