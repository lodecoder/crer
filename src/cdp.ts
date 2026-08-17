type Reply = { id?: number; result?: unknown; error?: { message: string } };
export class Cdp {
  #ws: WebSocket;
  #next = 1;
  #pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  constructor(url: string) {
    this.#ws = new WebSocket(url);
  }
  async open() {
    await new Promise<void>((resolve, reject) => {
      this.#ws.onopen = () => resolve();
      this.#ws.onerror = () => reject(new Error("CDP WebSocket connection failed"));
    });
    this.#ws.onmessage = (e) => {
      const m = JSON.parse(e.data) as Reply;
      if (!m.id) return;
      const p = this.#pending.get(m.id);
      if (!p) return;
      this.#pending.delete(m.id);
      m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
    };
  }
  call<T = Record<string, unknown>>(
    method: string,
    params: Record<string, unknown> = {},
    sessionId?: string,
  ): Promise<T> {
    const id = this.#next++;
    this.#ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    return new Promise((resolve, reject) =>
      this.#pending.set(id, { resolve: resolve as (v: unknown) => void, reject })
    );
  }
  close() {
    this.#ws.close();
  }
}
