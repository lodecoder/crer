import { ExecutionAbortedError } from "./errors.ts";

export async function mapWithConcurrency<T, R>(
  values: readonly T[],
  maxParallel: number,
  action: (value: T) => Promise<R>,
  shouldStop?: (result: R) => boolean,
): Promise<R[]> {
  const results = new Array<R>(values.length);
  let next = 0;
  let stopped = false;
  const worker = async () => {
    while (true) {
      if (stopped) return;
      const index = next++;
      if (index >= values.length) return;
      const result = await action(values[index]);
      results[index] = result;
      if (shouldStop?.(result)) stopped = true;
    }
  };
  await Promise.all(
    Array.from({ length: Math.min(Math.max(1, maxParallel), values.length) }, worker),
  );
  return results.filter((result): result is R => result !== undefined);
}

type Waiter = { signal?: AbortSignal; resolve: (acquired: boolean) => void };

/** A shared leaf-work gate whose limit applies across nested schedulers. */
export class Semaphore {
  #active = 0;
  #waiters: Waiter[] = [];

  constructor(private readonly limit: number) {
    if (!Number.isInteger(limit) || limit < 1) throw new Error("semaphore limit must be positive");
  }

  async run<T>(action: () => Promise<T>, signal?: AbortSignal): Promise<T | undefined> {
    if (!await this.#acquire(signal)) return undefined;
    try {
      return await action();
    } finally {
      this.#release();
    }
  }

  #acquire(signal?: AbortSignal): Promise<boolean> {
    if (signal?.aborted) return Promise.resolve(false);
    if (this.#active < this.limit) {
      this.#active++;
      return Promise.resolve(true);
    }
    return new Promise((resolve) => {
      const waiter: Waiter = { signal, resolve };
      this.#waiters.push(waiter);
      if (signal) {
        signal.addEventListener("abort", () => {
          const index = this.#waiters.indexOf(waiter);
          if (index >= 0) this.#waiters.splice(index, 1);
          resolve(false);
        }, { once: true });
      }
    });
  }

  #release() {
    while (this.#waiters.length) {
      const waiter = this.#waiters.shift()!;
      if (waiter.signal?.aborted) continue;
      waiter.resolve(true);
      return;
    }
    this.#active--;
  }
}

type KeyedEntry = { gate: Semaphore; users: number };

/** Serializes work that targets the same persistent resource without blocking other keys. */
export class KeyedLock {
  #entries = new Map<string, KeyedEntry>();

  async run<T>(
    key: string,
    action: () => Promise<T>,
    signal?: AbortSignal,
  ): Promise<T | undefined> {
    const entry = this.#entries.get(key) ?? { gate: new Semaphore(1), users: 0 };
    entry.users++;
    this.#entries.set(key, entry);
    try {
      return await entry.gate.run(action, signal);
    } finally {
      entry.users--;
      if (entry.users === 0) this.#entries.delete(key);
    }
  }
}

export async function mapWithCancellation<T, R>(
  values: readonly T[],
  maxParallel: number,
  action: (value: T, signal: AbortSignal) => Promise<R>,
  shouldAbort: (result: R) => boolean,
  parentSignal?: AbortSignal,
): Promise<R[]> {
  const controller = new AbortController();
  const signal = parentSignal
    ? AbortSignal.any([parentSignal, controller.signal])
    : controller.signal;
  return await mapWithConcurrency(
    values,
    maxParallel,
    async (value) => {
      const result = await action(value, signal);
      if (shouldAbort(result) && !controller.signal.aborted) {
        controller.abort(new ExecutionAbortedError("parallel group cancelled"));
      }
      return result;
    },
    () => signal.aborted,
  );
}
