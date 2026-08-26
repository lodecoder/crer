type Reply = {
  id?: number;
  method?: string;
  params?: unknown;
  sessionId?: string;
  result?: unknown;
  error?: { message: string };
};
export type CdpEvent = { params: unknown; sessionId?: string };
export class Cdp {
  #ws: WebSocket;
  #next = 1;
  #pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  #listeners = new Map<string, Set<(event: CdpEvent) => void>>();
  constructor(url: string) {
    this.#ws = new WebSocket(url);
  }
  async open(timeoutMs = 10_000) {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(
        () => reject(new Error("CDP WebSocket connection timed out")),
        timeoutMs,
      );
      this.#ws.onopen = () => {
        clearTimeout(timer);
        resolve();
      };
      this.#ws.onerror = () => {
        clearTimeout(timer);
        reject(new Error("CDP WebSocket connection failed"));
      };
    });
    this.#ws.onmessage = (e) => {
      const m = JSON.parse(e.data) as Reply;
      if (m.method) {
        for (const listener of this.#listeners.get(m.method) ?? []) {
          listener({ params: m.params, sessionId: m.sessionId });
        }
        return;
      }
      if (!m.id) return;
      const p = this.#pending.get(m.id);
      if (!p) return;
      this.#pending.delete(m.id);
      m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
    };
  }
  on(method: string, listener: (event: CdpEvent) => void): () => void {
    const listeners = this.#listeners.get(method) ?? new Set();
    listeners.add(listener);
    this.#listeners.set(method, listeners);
    return () => {
      listeners.delete(listener);
      if (listeners.size === 0) this.#listeners.delete(method);
    };
  }
  call<T = Record<string, unknown>>(
    method: string,
    params: Record<string, unknown> = {},
    sessionId?: string,
  ): Promise<T> {
    const id = this.#next++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`CDP call timed out: ${method}`));
      }, 10_000);
      this.#pending.set(id, {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value as T);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
      this.#ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
  }
  close() {
    this.#ws.close();
  }
}
