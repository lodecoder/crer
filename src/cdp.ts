import { EnvironmentError } from "./errors.ts";

type Reply = {
  id?: number;
  method?: string;
  params?: unknown;
  sessionId?: string;
  result?: unknown;
  error?: { message: string };
};

export type CdpEvent = { params: unknown; sessionId?: string };
export type CdpSocket = {
  readyState: number;
  onopen: (() => void) | null;
  onerror: (() => void) | null;
  onclose: (() => void) | null;
  onmessage: ((event: { data: string }) => void) | null;
  send(data: string): void;
  close(): void;
};

export class CdpConnectionError extends EnvironmentError {
  override name = "CdpConnectionError";
}

export class Cdp {
  #ws: CdpSocket;
  #next = 1;
  #state: "connecting" | "open" | "closed" = "connecting";
  #opening?: { timer: ReturnType<typeof setTimeout>; reject: (error: Error) => void };
  #pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  #listeners = new Map<string, Set<(event: CdpEvent) => void>>();

  constructor(
    url: string,
    socketFactory: (value: string) => CdpSocket = (value) =>
      new WebSocket(value) as unknown as CdpSocket,
  ) {
    this.#ws = socketFactory(url);
  }

  async open(timeoutMs = 10_000) {
    if (this.#state !== "connecting") throw new CdpConnectionError("CDP connection is not new");
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        const error = new CdpConnectionError("CDP WebSocket connection timed out");
        this.#fail(error);
        this.#ws.close();
      }, timeoutMs);
      this.#opening = { timer, reject };
      this.#ws.onopen = () => {
        clearTimeout(timer);
        this.#opening = undefined;
        this.#state = "open";
        resolve();
      };
      this.#ws.onerror = () => {
        this.#fail(new CdpConnectionError("CDP WebSocket connection failed"));
      };
      this.#ws.onclose = () => {
        this.#fail(new CdpConnectionError("CDP WebSocket connection closed"));
      };
      this.#ws.onmessage = (event) => this.#receive(event.data);
    });
  }

  #receive(data: string) {
    let message: Reply;
    try {
      message = JSON.parse(data) as Reply;
    } catch {
      this.#fail(new CdpConnectionError("CDP WebSocket returned invalid JSON"));
      this.#ws.close();
      return;
    }
    if (message.method) {
      for (const listener of this.#listeners.get(message.method) ?? []) {
        listener({ params: message.params, sessionId: message.sessionId });
      }
      return;
    }
    if (!message.id) return;
    const pending = this.#pending.get(message.id);
    if (!pending) return;
    this.#pending.delete(message.id);
    message.error
      ? pending.reject(new Error(message.error.message))
      : pending.resolve(message.result);
  }

  #fail(error: CdpConnectionError) {
    if (this.#state === "closed") return;
    this.#state = "closed";
    if (this.#opening) {
      clearTimeout(this.#opening.timer);
      this.#opening.reject(error);
      this.#opening = undefined;
    }
    for (const pending of this.#pending.values()) pending.reject(error);
    this.#pending.clear();
    this.#listeners.clear();
    this.#ws.onopen = null;
    this.#ws.onerror = null;
    this.#ws.onclose = null;
    this.#ws.onmessage = null;
  }

  on(method: string, listener: (event: CdpEvent) => void): () => void {
    if (this.#state === "closed") throw new CdpConnectionError("CDP connection is closed");
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
    if (this.#state !== "open") {
      return Promise.reject(new CdpConnectionError("CDP connection is not open"));
    }
    const id = this.#next++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new CdpConnectionError(`CDP call timed out: ${method}`));
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
      try {
        this.#ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
      } catch (error) {
        this.#pending.delete(id);
        clearTimeout(timer);
        reject(new CdpConnectionError(`CDP send failed: ${error}`));
      }
    });
  }

  close() {
    this.#fail(new CdpConnectionError("CDP connection closed by client"));
    this.#ws.close();
  }
}
