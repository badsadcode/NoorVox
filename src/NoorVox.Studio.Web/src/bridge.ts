// Typed request/response/event bridge to NoorVox.Studio (C#). In a plain browser (`npm run dev`) a mock answers instead.
import { mockCall } from "./mock";

type Pending = { resolve: (v: unknown) => void; reject: (e: unknown) => void };
type HostMessage =
  | { id: string; ok: true; result: unknown }
  | { id: string; ok: false; error: { code: string; message: string; details?: unknown } }
  | { event: string; data: unknown };

interface WebViewHost {
  postMessage(message: string): void;
  addEventListener(type: "message", listener: (e: MessageEvent<HostMessage>) => void): void;
}

const host: WebViewHost | undefined = (window as unknown as { chrome?: { webview?: WebViewHost } }).chrome?.webview;

/** True inside NoorVox Studio; false in a dev browser (mock data). */
export const isHosted = !!host;

export class BridgeError extends Error {
  constructor(public code: string, message: string, public details?: unknown) {
    super(message);
  }
}

const pending = new Map<string, Pending>();
const listeners = new Map<string, Set<(data: any) => void>>();
let seq = 0;

export function emitLocal(event: string, data: unknown) {
  listeners.get(event)?.forEach((cb) => cb(data));
}

host?.addEventListener("message", (e) => {
  const m = e.data;
  if ("event" in m) {
    emitLocal(m.event, m.data);
    return;
  }
  const p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  if (m.ok) p.resolve(m.result);
  else p.reject(new BridgeError(m.error.code, m.error.message, m.error.details));
});

export function call<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  if (!host) return mockCall(method, params) as Promise<T>;
  const id = `r${++seq}`;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
    host.postMessage(JSON.stringify({ id, method, params }));
  });
}

export function on<T = any>(event: string, cb: (data: T) => void): () => void {
  let set = listeners.get(event);
  if (!set) listeners.set(event, (set = new Set()));
  set.add(cb);
  return () => set!.delete(cb);
}

export function errorText(e: unknown): string {
  if (e instanceof BridgeError) return e.message;
  if (e instanceof Error) return e.message;
  return String(e);
}
