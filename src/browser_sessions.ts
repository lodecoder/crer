import {
  closeSharedBrowserSession,
  createSharedBrowserSession,
  type SharedBrowserSession,
} from "./runtime.ts";

/** Plan-owned sessions. Call forProfile only while holding that profile's execution lock. */
export class BrowserSessions {
  #sessions = new Map<string, SharedBrowserSession>();

  constructor(private focusPolicy: "once" | "before-step") {}

  forProfile(resolvedProfile: string): SharedBrowserSession {
    const key = resolvedProfile.replaceAll("/", "\\").toLowerCase();
    let session = this.#sessions.get(key);
    if (!session) {
      session = createSharedBrowserSession(this.focusPolicy);
      this.#sessions.set(key, session);
    }
    return session;
  }

  async close(): Promise<void> {
    const errors: unknown[] = [];
    // Reverse acquisition order also unwinds saved foreground windows where possible.
    for (const session of [...this.#sessions.values()].reverse()) {
      try {
        await closeSharedBrowserSession(session);
      } catch (error) {
        errors.push(error);
      }
    }
    this.#sessions.clear();
    if (errors.length) throw new AggregateError(errors, "Could not close plan browser sessions");
  }
}
