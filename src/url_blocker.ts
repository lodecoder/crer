import type { Cdp } from "./cdp.ts";
import { EnvironmentError } from "./errors.ts";

/** Compile once during validation/setup, never once per request. */
export function compileBlockUrls(value: unknown): RegExp[] {
  if (value === undefined) return [];
  if (!Array.isArray(value)) {
    throw new Error("browser.block_urls must be an array of regex strings");
  }
  return value.map((pattern, index) => {
    const label = `browser.block_urls[${index}]`;
    if (typeof pattern !== "string" || !pattern.trim()) {
      throw new Error(`${label} must be a non-empty regex string`);
    }
    try {
      return new RegExp(pattern);
    } catch (error) {
      throw new Error(`${label} must be a valid regular expression: ${error}`);
    }
  });
}

/** Owns interception for the playback page for the lifetime of its CDP connection. */
export class UrlBlocker {
  #patterns: RegExp[] = [];
  #enabled = false;
  #error?: EnvironmentError;

  constructor(private cdp: Pick<Cdp, "call" | "on" | "close">, private sessionId: string) {
    cdp.on("Fetch.requestPaused", (event) => {
      if (event.sessionId !== sessionId) return;
      const { requestId, request } = event.params as {
        requestId: string;
        request: { url: string };
      };
      const blocked = this.#patterns.some((pattern) => pattern.test(request.url));
      const response = blocked
        ? cdp.call("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" }, sessionId)
        : cdp.call("Fetch.continueRequest", { requestId }, sessionId);
      // An interception failure must not leave requests paused indefinitely or fail open.
      void response.catch((error) => {
        this.#error ??= new EnvironmentError(`URL blocking failed: ${error}`);
        cdp.close();
      });
    });
  }

  assertHealthy() {
    if (this.#error) throw this.#error;
  }

  async configure(patterns?: string[]) {
    this.assertHealthy();
    this.#patterns = compileBlockUrls(patterns);
    const enabled = this.#patterns.length > 0;
    if (enabled === this.#enabled) return;
    // Service Worker responses and cached resources must not bypass URL filtering.
    await this.cdp.call("Network.setBypassServiceWorker", { bypass: enabled }, this.sessionId);
    await this.cdp.call("Network.setCacheDisabled", { cacheDisabled: enabled }, this.sessionId);
    await this.cdp.call(
      enabled ? "Fetch.enable" : "Fetch.disable",
      enabled ? { patterns: [{ urlPattern: "*", requestStage: "Request" }] } : {},
      this.sessionId,
    );
    this.#enabled = enabled;
    this.assertHealthy();
  }
}
