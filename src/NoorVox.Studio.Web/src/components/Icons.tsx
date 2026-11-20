// Small inline icon set (no icon font, no network). 20px grid, currentColor.
import type { SVGProps } from "react";

type P = SVGProps<SVGSVGElement> & { size?: number };
const base = (size = 18) => ({ width: size, height: size, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 1.8,
  strokeLinecap: "round" as const, strokeLinejoin: "round" as const, "aria-hidden": true });

export const Logo = ({ size = 30 }: { size?: number }) => (
  <svg width={size} height={size} viewBox="0 0 30 30" aria-hidden="true"><g fill="currentColor">
    <rect x="2" y="12" width="3" height="6" rx="1.5" /><rect x="7" y="8" width="3" height="14" rx="1.5" /><rect x="12" y="3" width="3" height="24" rx="1.5" />
    <rect x="17" y="7" width="3" height="16" rx="1.5" /><rect x="22" y="10" width="3" height="10" rx="1.5" /><rect x="27" y="13" width="2" height="4" rx="1" /></g></svg>
);
export const Mic = ({ size, ...p }: P) => <svg {...base(size)} {...p}><rect x="9" y="3" width="6" height="11" rx="3" /><path d="M5 11a7 7 0 0 0 14 0M12 18v3" /></svg>;
export const Box = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M3 7l9-4 9 4-9 4-9-4zM3 7v10l9 4 9-4V7M12 11v10" /></svg>;
export const Pulse = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M3 12h4l2-6 4 12 2-6h6" /></svg>;
export const Gear = ({ size, ...p }: P) => <svg {...base(size)} {...p}><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" /></svg>;
export const Play = ({ size, ...p }: P) => <svg {...base(size)} {...p} fill="currentColor" stroke="none"><path d="M8 5.5v13l11-6.5z" /></svg>;
export const Pause = ({ size, ...p }: P) => <svg {...base(size)} {...p} fill="currentColor" stroke="none"><rect x="6" y="5" width="4" height="14" rx="1" /><rect x="14" y="5" width="4" height="14" rx="1" /></svg>;
export const Download = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M12 3v12M7 10l5 5 5-5M4 20h16" /></svg>;
export const Folder = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" /></svg>;
export const Stop = ({ size, ...p }: P) => <svg {...base(size)} {...p}><rect x="6" y="6" width="12" height="12" rx="2" /></svg>;
export const Refresh = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4" /></svg>;
export const Wave = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M3 12h2M7 8v8M11 5v14M15 9v6M19 7v10M21 12h0" /></svg>;
export const Check = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M5 12l5 5 9-10" /></svg>;
export const Alert = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M12 3l9.5 17h-19zM12 10v4M12 17.5v.01" /></svg>;
export const Spinner = ({ size, ...p }: P) => <svg {...base(size)} {...p} className="spin"><path d="M12 3a9 9 0 1 0 9 9" /></svg>;
export const Shield = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M12 3l8 3v6c0 4.5-3.4 8.2-8 9-4.6-.8-8-4.5-8-9V6z" /><path d="M8.5 12l2.5 2.5 4.5-5" /></svg>;
export const Drive = ({ size, ...p }: P) => <svg {...base(size)} {...p}><rect x="3" y="13" width="18" height="7" rx="2" /><path d="M5 13l2.5-8h9L19 13M7 16.5h.01" /></svg>;
export const Upload = ({ size, ...p }: P) => <svg {...base(size)} {...p}><path d="M12 15V3M7 8l5-5 5 5M4 20h16" /></svg>;
