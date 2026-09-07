export type InputCall = (method: string, params: Record<string, unknown>) => Promise<unknown>;

type PressedKey = { key: string; code: string; windowsVirtualKeyCode: number; modifiers: number };

type CleanupAwareError = Error & { cleanupErrors?: string[] };

/** Tracks successful input-down events and releases them in reverse order on every exit path. */
export class InputGuard {
  #mouse: { x: number; y: number; clickCount: number } | undefined;
  #keys: PressedKey[] = [];

  constructor(private readonly call: InputCall) {}

  async mouseDown(x: number, y: number, clickCount = 1) {
    await this.call("Input.dispatchMouseEvent", {
      type: "mousePressed",
      x,
      y,
      button: "left",
      buttons: 1,
      clickCount,
    });
    this.#mouse = { x, y, clickCount };
  }

  async mouseUp(x: number, y: number) {
    const pressed = this.#mouse;
    if (!pressed) return;
    await this.call("Input.dispatchMouseEvent", {
      type: "mouseReleased",
      x,
      y,
      button: "left",
      buttons: 0,
      clickCount: pressed.clickCount,
    });
    this.#mouse = undefined;
  }

  async keyDown(event: PressedKey & Record<string, unknown>) {
    await this.call("Input.dispatchKeyEvent", event);
    this.#keys.push({
      key: event.key,
      code: event.code,
      windowsVirtualKeyCode: event.windowsVirtualKeyCode,
      modifiers: event.modifiers,
    });
  }

  async keyUp(event: PressedKey & Record<string, unknown>) {
    await this.call("Input.dispatchKeyEvent", event);
    const index = this.#keys.findLastIndex((pressed) => pressed.code === event.code);
    if (index >= 0) this.#keys.splice(index, 1);
  }

  async run<T>(action: (guard: InputGuard) => Promise<T>): Promise<T> {
    let result: T | undefined;
    let primary: unknown;
    try {
      result = await action(this);
    } catch (error) {
      primary = error;
    }
    const cleanupErrors = await this.#releaseAll();
    if (primary !== undefined) {
      if (cleanupErrors.length && primary instanceof Error) {
        (primary as CleanupAwareError).cleanupErrors = cleanupErrors;
      }
      throw primary;
    }
    if (cleanupErrors.length) throw new Error(`input cleanup failed: ${cleanupErrors.join("; ")}`);
    return result as T;
  }

  async #releaseAll(): Promise<string[]> {
    const errors: string[] = [];
    for (const key of this.#keys.toReversed()) {
      try {
        await this.call("Input.dispatchKeyEvent", { ...key, type: "keyUp" });
      } catch (error) {
        errors.push(String(error));
      }
    }
    this.#keys = [];
    if (this.#mouse) {
      const mouse = this.#mouse;
      try {
        await this.call("Input.dispatchMouseEvent", {
          type: "mouseReleased",
          x: mouse.x,
          y: mouse.y,
          button: "left",
          buttons: 0,
          clickCount: mouse.clickCount,
        });
      } catch (error) {
        errors.push(String(error));
      }
      this.#mouse = undefined;
    }
    return errors;
  }
}

export function cleanupErrors(error: unknown): string[] | undefined {
  return error instanceof Error ? (error as CleanupAwareError).cleanupErrors : undefined;
}
