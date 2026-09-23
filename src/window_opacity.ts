import { EnvironmentError } from "./errors.ts";

export function opacityAlpha(value: unknown = 1): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0 || value > 1) {
    throw new Error("browser.window.opacity must be a finite number from 0 to 1");
  }
  return Math.round(value * 255);
}

const symbols = {
  crer_input_set_process_opacity: { parameters: ["u32", "u32"], result: "i32" },
} as const;

/** A separate controller so opacity never changes focus or the topmost monitoring policy. */
export class WindowOpacity {
  #lib?: Deno.DynamicLibrary<typeof symbols>;
  #timer?: ReturnType<typeof setInterval>;

  constructor(private pid: number, private dllPath?: string) {}

  configure(value?: number) {
    const alpha = opacityAlpha(value);
    if (this.#timer !== undefined) clearInterval(this.#timer);
    this.#timer = undefined;
    // Preserve compatibility with older DLLs when opacity has never been requested.
    if (alpha === 255 && !this.#lib) return;
    if (!this.#lib) {
      if (!this.dllPath) {
        throw new EnvironmentError(
          "browser.window.opacity requires the crer-win-input.dll native DLL",
        );
      }
      try {
        this.#lib = Deno.dlopen(this.dllPath, symbols);
      } catch (error) {
        throw new EnvironmentError(
          `Could not load window opacity support; rebuild crer-win-input.dll: ${error}`,
        );
      }
    }
    const apply = () => this.#lib!.symbols.crer_input_set_process_opacity(this.pid, alpha);
    const status = apply();
    if (status !== 0) {
      throw new EnvironmentError(`could not set CfT opacity (Win32 status ${status})`);
    }
    if (alpha === 255) return;
    let lastStatus = 0;
    this.#timer = setInterval(() => {
      // Re-resolve the HWND after navigation/recreation without raising the window.
      let status: number;
      try {
        status = apply();
      } catch {
        status = 1;
      }
      if (status !== 0 && status !== lastStatus) {
        console.warn(`Warning: could not maintain CfT opacity (Win32 status ${status})`);
      }
      lastStatus = status;
    }, 250);
  }

  close() {
    if (this.#timer !== undefined) clearInterval(this.#timer);
    this.#timer = undefined;
    this.#lib?.close();
    this.#lib = undefined;
  }
}
