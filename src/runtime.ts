import { Cdp } from "./cdp.ts";
import { jitter, Random, randomSeed } from "./prng.ts";
import { persistentProfileDirectory, prepareChromeProfile } from "./profiles.ts";
import {
  matchTemplate,
  matchTemplates,
  randomPointInMatch,
  type TemplateMatch,
} from "./template.ts";
import type { FailureKind, Jitter, RunResult, Scenario, Step } from "./types.ts";

const decoder = new TextDecoder();
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
async function fetchWithin(url: string, timeoutMs: number) {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      fetch(url, { signal: AbortSignal.timeout(timeoutMs) }),
      new Promise<Response>((_, reject) => {
        timer = setTimeout(() => reject(new Error(`HTTP request timed out: ${url}`)), timeoutMs);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}
async function sleepInterruptibly(ms: number, signal?: AbortSignal) {
  if (!signal) return await sleep(ms);
  await Promise.race([
    sleep(ms),
    new Promise<void>((_, reject) =>
      signal.addEventListener("abort", () => reject(new Error("worker timed out")), { once: true })
    ),
  ]);
}
export type PlayOptions = {
  chromePath: string;
  inputDllPath?: string;
  position?: { left: number; top: number };
  seed?: string;
  keepArtifacts?: boolean;
  stepDelayMs?: number;
  ignoreViewportMismatch?: boolean;
  /** Mute audio from the dedicated CfT process only. */
  muteAudio?: boolean;
  profileDir?: string;
  templateBaseDir?: string;
  signal?: AbortSignal;
};
type ForegroundGuard = {
  original: bigint;
  foregroundProcess: (pid: number) => number;
  restore: () => number;
  close: () => void;
};
const windowHandleText = (handle: bigint) => `0x${handle.toString(16)}`;
function captureForeground(dllPath: string): ForegroundGuard {
  const lib = Deno.dlopen(dllPath, {
    crer_input_get_foreground_window: { parameters: [], result: "usize" },
    crer_input_foreground_process_window: { parameters: ["u32"], result: "i32" },
    crer_input_restore_foreground_window: { parameters: ["usize"], result: "i32" },
  });
  const original = lib.symbols.crer_input_get_foreground_window();
  return {
    original,
    foregroundProcess: (pid) => lib.symbols.crer_input_foreground_process_window(pid),
    restore: () => lib.symbols.crer_input_restore_foreground_window(original),
    close: () => lib.close(),
  };
}
type BrowserSession = {
  cdp: Cdp;
  sessionId: string;
  pageDebuggerUrl: string;
  windowId: number;
  process: Deno.ChildProcess;
  runDir: string;
  viewport: { x: number; y: number };
  network: NetworkTracker;
};

function scenarioProfileDir(s: Scenario): string | undefined {
  const profile = s.browser.profile;
  if (!profile?.startsWith("persistent:")) return undefined;
  const directory = profile.slice("persistent:".length).trim();
  if (!directory) throw new Error("browser.profile persistent: requires a directory");
  return directory;
}

class NetworkTracker {
  #requests = new Set<string>();
  #lastActivity = Date.now();
  constructor(cdp: Cdp, sessionId: string) {
    const sameSession = (event: { sessionId?: string }) => event.sessionId === sessionId;
    cdp.on("Network.requestWillBeSent", (event) => {
      if (!sameSession(event)) return;
      const requestId = (event.params as { requestId?: string }).requestId;
      if (requestId) this.#requests.add(requestId);
      this.#lastActivity = Date.now();
    });
    for (const method of ["Network.loadingFinished", "Network.loadingFailed"]) {
      cdp.on(method, (event) => {
        if (!sameSession(event)) return;
        const requestId = (event.params as { requestId?: string }).requestId;
        if (requestId) this.#requests.delete(requestId);
        this.#lastActivity = Date.now();
      });
    }
  }
  idleFor(ms: number) {
    return this.#requests.size === 0 && Date.now() - this.#lastActivity >= ms;
  }
}

type ViewportInfo = { dpr: number; scale: number; width: number; height: number };
async function readViewport(cdp: Cdp, sessionId: string): Promise<ViewportInfo | undefined> {
  const [layout, display] = await Promise.all([
    cdp.call<{ cssVisualViewport?: { clientWidth: number; clientHeight: number } }>(
      "Page.getLayoutMetrics",
      {},
      sessionId,
    ),
    cdp.call<{ result: { value?: { dpr: number; scale: number } } }>(
      "Runtime.evaluate",
      {
        expression: "({dpr:devicePixelRatio,scale:visualViewport?.scale ?? 1})",
        returnByValue: true,
      },
      sessionId,
    ),
  ]);
  const viewport = layout.cssVisualViewport;
  const metrics = display.result.value;
  if (!viewport || !metrics) return undefined;
  return {
    dpr: metrics.dpr,
    scale: metrics.scale,
    width: viewport.clientWidth,
    height: viewport.clientHeight,
  };
}
async function setContentSize(
  cdp: Cdp,
  sessionId: string,
  windowId: number,
  content: { width: number; height: number },
) {
  for (let attempt = 0; attempt < 10; attempt++) {
    await cdp.call("Browser.setContentsSize", { windowId, ...content });
    await sleep(100);
    const actual = await readViewport(cdp, sessionId);
    if (actual?.width === content.width && actual.height === content.height) return;
  }
}

async function waitForStableViewport(
  cdp: Cdp,
  sessionId: string,
): Promise<ViewportInfo | undefined> {
  const deadline = Date.now() + 10_000;
  let previous = await readViewport(cdp, sessionId);
  let stableSince = Date.now();
  while (Date.now() < deadline) {
    await sleep(100);
    const current = await readViewport(cdp, sessionId);
    if (!current) continue;
    if (previous && current.width === previous.width && current.height === previous.height) {
      if (Date.now() - stableSince >= 1_000) return current;
    } else {
      previous = current;
      stableSince = Date.now();
    }
  }
  return previous;
}

async function waitForExpectedViewport(
  cdp: Cdp,
  sessionId: string,
  expected: { width: number; height: number },
): Promise<ViewportInfo | undefined> {
  const deadline = Date.now() + 30_000;
  let matchSince: number | undefined;
  let last: ViewportInfo | undefined;
  while (Date.now() < deadline) {
    const current = await readViewport(cdp, sessionId);
    if (current) {
      last = current;
      if (current.width === expected.width && current.height === expected.height) {
        matchSince ??= Date.now();
        if (Date.now() - matchSince >= 1_000) return current;
      } else {
        matchSince = undefined;
      }
    }
    await sleep(100);
  }
  return last;
}

async function waitForDocumentReady(cdp: Cdp, sessionId: string) {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    const state = await cdp.call<{ result: { value?: string } }>("Runtime.evaluate", {
      expression: "document.readyState",
      returnByValue: true,
    }, sessionId);
    if (state.result.value !== "loading") return;
    await sleep(50);
  }
  throw new Error("CfT page did not finish loading");
}

async function waitEndpoint(port: number): Promise<{ webSocketDebuggerUrl: string }> {
  for (let i = 0; i < 150; i++) {
    try {
      const response = await fetchWithin(`http://127.0.0.1:${port}/json/version`, 1_000);
      if (response.ok) return await response.json();
    } catch { /* wait */ }
    await sleep(100);
  }
  throw new Error(`CDP endpoint on port ${port} was not available`);
}

async function waitForProcessExit(process: Deno.ChildProcess, timeoutMs: number) {
  return await Promise.race([
    process.status.then((status) => status),
    sleep(timeoutMs).then(() => undefined),
  ]);
}

async function terminateProcess(process: Deno.ChildProcess) {
  const exited = await waitForProcessExit(process, 5_000);
  if (exited) return exited;
  try {
    process.kill("SIGTERM");
  } catch {
    // The child may have already exited.
  }
  return await waitForProcessExit(process, 2_000);
}

async function closeBrowser(browser: BrowserSession) {
  const graceful = await Promise.race([
    browser.cdp.call("Browser.close").then(() => true, () => false),
    sleep(5_000).then(() => false),
  ]);
  browser.cdp.close();
  const status = await terminateProcess(browser.process);
  await Deno.writeTextFile(
    `${browser.runDir}/shutdown.json`,
    JSON.stringify(
      {
        graceful,
        exited: status !== undefined,
        ...(status ? { code: status.code, success: status.success, signal: status.signal } : {}),
      },
      null,
      2,
    ) + "\n",
  ).catch(() => {});
}
async function validateDisplay(
  cdp: Cdp,
  sessionId: string,
  s: Scenario,
  runDir: string,
  ignoreViewportMismatch: boolean,
): Promise<{ x: number; y: number }> {
  const display = s.browser.display;
  const actual = await readViewport(cdp, sessionId);
  const expectedViewport = s.browser.window?.viewport ?? s.browser.window?.content;
  await Deno.writeTextFile(
    `${runDir}/display.json`,
    JSON.stringify(
      { actual: actual ?? {}, expectedViewport, display: display ?? {}, ignoreViewportMismatch },
      null,
      2,
    )
      + "\n",
  );
  if (!actual || actual.width <= 0 || actual.height <= 0) {
    throw new Error("CfT viewport was unavailable");
  }
  const strict = display?.zoom_check === "strict";
  const report = (message: string) => {
    if (strict) throw new Error(message);
    if (display?.zoom_check !== "off") console.warn(message);
  };
  if (
    expectedViewport
    && (actual.width !== expectedViewport.width || actual.height !== expectedViewport.height)
  ) {
    const message =
      `Viewport mismatch: expected ${expectedViewport.width}x${expectedViewport.height}, got ${actual.width}x${actual.height}`;
    if (ignoreViewportMismatch) {
      console.warn(
        `Warning: ${message}; continuing because --ignore-viewport-mismatch was specified`,
      );
    } else {
      throw new Error(message);
    }
  }
  if (!display || display.zoom_check === "off") return { x: actual.width, y: actual.height };
  if (display.expected_dpr !== undefined && actual?.dpr !== display.expected_dpr) {
    const message = `DPR mismatch: expected ${display.expected_dpr}, got ${actual?.dpr}`;
    report(message);
  }
  if (display.browser_zoom !== undefined && display.browser_zoom !== 100) {
    report(`browser_zoom ${display.browser_zoom} is not supported; v1 strictly supports 100 only`);
  }
  if (actual.scale !== 1) report(`Viewport scale mismatch: expected 1, got ${actual.scale}`);
  return { x: actual.width, y: actual.height };
}
async function launch(s: Scenario, options: PlayOptions, runDir: string): Promise<BrowserSession> {
  const configuredProfile = options.profileDir ?? scenarioProfileDir(s);
  const profile = configuredProfile
    ? await persistentProfileDirectory(configuredProfile)
    : `${await Deno.realPath(runDir)}/profile`;
  await prepareChromeProfile(profile);
  const reservation = Deno.listen({ hostname: "127.0.0.1", port: 0 });
  const port = (reservation.addr as Deno.NetAddr).port;
  reservation.close();
  const p = new Deno.Command(options.chromePath, {
    args: [
      `--remote-debugging-port=${port}`,
      "--remote-debugging-address=127.0.0.1",
      `--user-data-dir=${profile}`,
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-sync",
      "--disable-infobars",
      "--disable-save-password-bubble",
      // Keep CSS coordinates stable even when Windows uses 125%/150%/200% display scaling.
      "--force-device-scale-factor=1",
      // A translation bubble is browser UI, not page content, and can obscure coordinate replay.
      "--disable-features=Translate,TranslateUI,PasswordManagerOnboarding",
      ...(options.muteAudio ? ["--mute-audio"] : []),
      `--app=${s.browser.initial_url}`,
    ],
    stdout: "null",
    stderr: "piped",
  }).spawn();
  let cdp: Cdp | undefined;
  let stage = "chrome_started";
  const writeLaunchDiagnostic = async (error?: unknown) => {
    await Deno.writeTextFile(
      `${runDir}/launch.json`,
      JSON.stringify({ stage, ...(error ? { error: String(error) } : {}) }, null, 2) + "\n",
    );
  };
  try {
    const version = await waitEndpoint(port);
    stage = "endpoint_ready";
    await writeLaunchDiagnostic();
    cdp = new Cdp(version.webSocketDebuggerUrl);
    await cdp.open();
    stage = "browser_websocket_open";
    await writeLaunchDiagnostic();
    const targets = await (
      await fetch(`http://127.0.0.1:${port}/json/list`)
    ).json() as Array<{ id: string; type: string; webSocketDebuggerUrl: string }>;
    const target = targets.find((candidate) => candidate.type === "page");
    if (!target) throw new Error("CfT did not expose a page target");
    const attached = await cdp.call<{ sessionId: string }>("Target.attachToTarget", {
      targetId: target.id,
      flatten: true,
    });
    stage = "target_attached";
    await writeLaunchDiagnostic();
    const window = await cdp.call<{ windowId: number }>("Browser.getWindowForTarget", {
      targetId: target.id,
    });
    const bounds = { ...(s.browser.window?.bounds ?? {}), ...(options.position ?? {}) };
    if (Object.keys(bounds).length) {
      const current = await cdp.call<{ bounds: { width?: number; height?: number } }>(
        "Browser.getWindowBounds",
        { windowId: window.windowId },
      );
      if (Number.isFinite(current.bounds.width) && Number.isFinite(current.bounds.height)) {
        await cdp.call("Browser.setWindowBounds", {
          windowId: window.windowId,
          bounds: {
            windowState: "normal",
            width: current.bounds.width,
            height: current.bounds.height,
          },
        });
      }
      await cdp.call("Browser.setWindowBounds", { windowId: window.windowId, bounds });
    }
    await cdp.call("Page.enable", {}, attached.sessionId);
    await cdp.call("Runtime.enable", {}, attached.sessionId);
    if (s.browser.window?.content) {
      await setContentSize(cdp, attached.sessionId, window.windowId, s.browser.window.content);
    }
    // A scrollbar may only appear after the initial navigation. Wait for the recorded coordinate
    // space itself rather than accepting a short-lived provisional viewport.
    await waitForDocumentReady(cdp, attached.sessionId);
    const expectedViewport = s.browser.window?.viewport ?? s.browser.window?.content;
    if (expectedViewport && !options.ignoreViewportMismatch) {
      await waitForExpectedViewport(cdp, attached.sessionId, expectedViewport);
    } else {
      await waitForStableViewport(cdp, attached.sessionId);
    }
    const network = new NetworkTracker(cdp, attached.sessionId);
    await cdp.call("Network.enable", {}, attached.sessionId);
    const viewport = await validateDisplay(
      cdp,
      attached.sessionId,
      s,
      runDir,
      options.ignoreViewportMismatch ?? false,
    );
    stage = "ready";
    await writeLaunchDiagnostic();
    return {
      cdp,
      sessionId: attached.sessionId,
      pageDebuggerUrl: target.webSocketDebuggerUrl,
      windowId: window.windowId,
      process: p,
      runDir,
      viewport,
      network,
    };
  } catch (error) {
    await writeLaunchDiagnostic(error).catch(() => {});
    cdp?.close();
    await terminateProcess(p);
    throw error;
  }
}
async function capture(b: BrowserSession, name: string, required = false) {
  let lastError: unknown;
  for (let attempt = 0; attempt < 2; attempt++) {
    let pageCdp: Cdp | undefined;
    try {
      const warmupCdp = new Cdp(b.pageDebuggerUrl);
      await warmupCdp.open();
      await warmupCdp.call("Page.enable");
      warmupCdp.close();
      pageCdp = new Cdp(b.pageDebuggerUrl);
      await pageCdp.open();
      await pageCdp.call("Page.enable");
      await sleep(250);
      const r = await pageCdp.call<{ data: string }>("Page.captureScreenshot", { format: "png" });
      await Deno.writeFile(
        `${b.runDir}/${name}.png`,
        Uint8Array.from(atob(r.data), (x) => x.charCodeAt(0)),
      );
      return;
    } catch (error) {
      lastError = error;
      await sleep(250);
    } finally {
      pageCdp?.close();
    }
  }
  if (required) throw lastError;
}
function failureFor(step: Step, error: unknown): FailureKind {
  const e = String(error);
  if (e.includes("template")) return "template";
  if (e.includes("jitter")) return "jitter_bounds";
  if (step.do === "assert" || step.do === "wait_for") {
    return e.includes("timed out")
      ? "timeout"
      : "assertion";
  }
  if (step.do === "navigate") return "navigation";
  return "action";
}
async function waitFor(b: BrowserSession, step: Step, timeout: number, signal?: AbortSignal) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (signal?.aborted) throw new Error("worker timed out");
    const hint = step.locator_hint;
    const expression = `(() => {
      const hint = ${JSON.stringify(hint ?? {})};
      const elements = Array.from(document.querySelectorAll('[role]'));
      const found = !hint.role && !hint.name && !hint.text || elements.some((element) => {
        const role = element.getAttribute('role');
        const name = element.getAttribute('aria-label') || element.textContent?.trim() || '';
        const text = element.textContent?.trim() || '';
        return (!hint.role || role === hint.role) && (!hint.name || name === hint.name) && (!hint.text || text === hint.text) &&
          !!(element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden');
      });
      return {url: location.href, state: document.readyState, found};
    })()`;
    const value = await b.cdp.call<
      { result: { value?: { url: string; state: string; found: boolean } } }
    >(
      "Runtime.evaluate",
      { expression, returnByValue: true },
      b.sessionId,
    );
    const state = value.result.value;
    const urlOk = !step.url
      || new RegExp("^" + step.url.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace("*", ".*") + "$")
        .test(state?.url ?? "");
    const stateOk = step.state === "network_idle"
      ? state?.state === "complete" && b.network.idleFor(500)
      : step.state === "visible"
      ? state?.found === true
      : !step.state || state?.state === "complete";
    if (urlOk && stateOk && (state?.found ?? false)) return;
    await sleep(100);
  }
  throw new Error("wait_for timed out");
}
async function assertState(b: BrowserSession, step: Step) {
  const hint = step.locator_hint;
  const expression = `(() => {
    const hint = ${JSON.stringify(hint ?? {})};
    const elements = Array.from(document.querySelectorAll('[role]'));
    const found = !hint.role && !hint.name && !hint.text || elements.some((element) => {
      const role = element.getAttribute('role');
      const name = element.getAttribute('aria-label') || element.textContent?.trim() || '';
      const text = element.textContent?.trim() || '';
      return (!hint.role || role === hint.role) && (!hint.name || name === hint.name) && (!hint.text || text === hint.text) &&
        !!(element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden');
    });
    return {url: location.href, state: document.readyState, found};
  })()`;
  const value = await b.cdp.call<
    { result: { value?: { url: string; state: string; found: boolean } } }
  >(
    "Runtime.evaluate",
    { expression, returnByValue: true },
    b.sessionId,
  );
  const state = value.result.value;
  if (step.url) {
    const matches = new RegExp(
      "^" + step.url.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace("*", ".*") + "$",
    ).test(state?.url ?? "");
    if (!matches) throw new Error(`assert URL failed: ${state?.url ?? "unknown"}`);
  }
  if (step.state && state?.state !== step.state) {
    throw new Error(`assert ready state failed: ${state?.state ?? "unknown"}`);
  }
  if (!state?.found) {
    throw new Error("assert locator failed");
  }
}
type KeyInfo = { key: string; vk: number; code: string; text?: string };
const keys: Record<string, KeyInfo> = {
  Enter: { key: "Enter", vk: 13, code: "Enter", text: "\r" },
  Tab: { key: "Tab", vk: 9, code: "Tab" },
  Escape: { key: "Escape", vk: 27, code: "Escape" },
  Backspace: { key: "Backspace", vk: 8, code: "Backspace" },
  Delete: { key: "Delete", vk: 46, code: "Delete" },
  ArrowDown: { key: "ArrowDown", vk: 40, code: "ArrowDown" },
  ArrowUp: { key: "ArrowUp", vk: 38, code: "ArrowUp" },
  ArrowLeft: { key: "ArrowLeft", vk: 37, code: "ArrowLeft" },
  ArrowRight: { key: "ArrowRight", vk: 39, code: "ArrowRight" },
  Control: { key: "Control", vk: 17, code: "ControlLeft" },
  Alt: { key: "Alt", vk: 18, code: "AltLeft" },
  Shift: { key: "Shift", vk: 16, code: "ShiftLeft" },
  Meta: { key: "Meta", vk: 91, code: "MetaLeft" },
};
const modifierBits: Record<string, number> = { Alt: 1, Control: 2, Meta: 4, Shift: 8 };
const keyInfo = (key: string): KeyInfo =>
  keys[key] ?? {
    key,
    vk: key.length === 1 ? key.toUpperCase().charCodeAt(0) : 0,
    code: key.length === 1 && /^[a-z]$/i.test(key)
      ? `Key${key.toUpperCase()}`
      : key.length === 1 && /^\d$/.test(key)
      ? `Digit${key}`
      : key,
  };
// Match Chromium's keyboard protocol: non-text physical keys use rawKeyDown;
// Enter carries CR text and must use keyDown so its native button activation runs.
const keyEvent = (type: "rawKeyDown" | "keyUp", info: KeyInfo, modifiers?: number) => ({
  type,
  key: info.key,
  code: info.code,
  windowsVirtualKeyCode: info.vk,
  ...(modifiers === undefined ? {} : { modifiers }),
});
const keyDownEvent = (info: KeyInfo, modifiers = 0) =>
  info.text && modifiers === 0
    ? {
      type: "keyDown",
      key: info.key,
      code: info.code,
      windowsVirtualKeyCode: info.vk,
      text: info.text,
      unmodifiedText: info.text,
      modifiers,
    }
    : keyEvent("rawKeyDown", info, modifiers);
async function act(
  b: BrowserSession,
  step: Step,
  at: { x: number; y: number } | undefined,
  j: Jitter | undefined,
  rng: Random,
  timeout: number,
  signal?: AbortSignal,
) {
  const call = (m: string, p: Record<string, unknown>) => b.cdp.call(m, p, b.sessionId);
  switch (step.do) {
    case "navigate":
      return await call("Page.navigate", { url: step.url });
    case "wait_for":
      return await waitFor(b, step, timeout, signal);
    case "assert":
      return await assertState(b, step);
    case "click":
    case "double_click": {
      const n = step.do === "double_click" ? 2 : 1;
      for (let i = 1; i <= n; i++) {
        await call("Input.dispatchMouseEvent", { type: "mouseMoved", x: at!.x, y: at!.y });
        await call("Input.dispatchMouseEvent", {
          type: "mousePressed",
          x: at!.x,
          y: at!.y,
          button: "left",
          clickCount: i,
        });
        await call("Input.dispatchMouseEvent", {
          type: "mouseReleased",
          x: at!.x,
          y: at!.y,
          button: "left",
          clickCount: i,
        });
      }
      return;
    }
    case "mouse_move":
      return await call("Input.dispatchMouseEvent", { type: "mouseMoved", x: at!.x, y: at!.y });
    case "drag": {
      const from = step.from as { x?: number; y?: number } | undefined;
      const to = step.to as { x?: number; y?: number } | undefined;
      if (
        !from || !to || !Number.isFinite(from.x) || !Number.isFinite(from.y)
        || !Number.isFinite(to.x) || !Number.isFinite(to.y)
      ) {
        throw new Error("drag requires from and to points");
      }
      const fromPoint = { x: from.x!, y: from.y! };
      const toPoint = { x: to.x!, y: to.y! };
      const jitteredFrom = jitter(fromPoint, j, rng, b.viewport);
      if (!jitteredFrom) throw new Error("jitter bounds failure");
      const offset = { x: jitteredFrom.x - fromPoint.x, y: jitteredFrom.y - fromPoint.y };
      const jitteredTo = { x: toPoint.x + offset.x, y: toPoint.y + offset.y };
      if (
        jitteredTo.x < 0 || jitteredTo.y < 0 || jitteredTo.x > b.viewport.x
        || jitteredTo.y > b.viewport.y
      ) {
        throw new Error("jitter bounds failure");
      }
      await call("Input.dispatchMouseEvent", {
        type: "mouseMoved",
        x: jitteredFrom.x,
        y: jitteredFrom.y,
      });
      await call("Input.dispatchMouseEvent", {
        type: "mousePressed",
        x: jitteredFrom.x,
        y: jitteredFrom.y,
        button: "left",
        buttons: 1,
        clickCount: 1,
      });
      for (let i = 1; i <= 10; i++) {
        const ratio = i / 10;
        await call("Input.dispatchMouseEvent", {
          type: "mouseMoved",
          x: jitteredFrom.x + (jitteredTo.x - jitteredFrom.x) * ratio,
          y: jitteredFrom.y + (jitteredTo.y - jitteredFrom.y) * ratio,
          button: "left",
          buttons: 1,
        });
      }
      return await call("Input.dispatchMouseEvent", {
        type: "mouseReleased",
        x: jitteredTo.x,
        y: jitteredTo.y,
        button: "left",
        buttons: 0,
        clickCount: 1,
      });
    }
    case "scroll":
      return await call("Input.dispatchMouseEvent", {
        type: "mouseWheel",
        x: at!.x,
        y: at!.y,
        deltaX: step.delta?.x ?? 0,
        deltaY: step.delta?.y ?? 0,
      });
    case "text":
      return await call("Input.insertText", { text: step.value ?? "" });
    case "key": {
      const info = keyInfo(step.key ?? "");
      await call("Input.dispatchKeyEvent", keyDownEvent(info));
      return await call("Input.dispatchKeyEvent", keyEvent("keyUp", info));
    }
    case "key_chord": {
      const chord = step.keys;
      if (
        !Array.isArray(chord) || chord.length < 2 || !chord.every((key) => typeof key === "string")
      ) {
        throw new Error("key_chord requires keys with one or more modifiers and a final key");
      }
      const modifiers = chord.slice(0, -1) as string[];
      if (!modifiers.every((key) => key in modifierBits)) {
        throw new Error("key_chord modifiers must be Alt, Control, Meta, or Shift");
      }
      let mask = 0;
      for (const modifier of modifiers) {
        await call("Input.dispatchKeyEvent", keyDownEvent(keyInfo(modifier), mask));
        mask |= modifierBits[modifier];
      }
      const info = keyInfo(chord.at(-1)! as string);
      await call("Input.dispatchKeyEvent", keyDownEvent(info, mask));
      await call("Input.dispatchKeyEvent", keyEvent("keyUp", info, mask));
      for (const modifier of modifiers.toReversed()) {
        mask &= ~modifierBits[modifier];
        await call("Input.dispatchKeyEvent", keyEvent("keyUp", keyInfo(modifier), mask));
      }
      return;
    }
    case "sleep": {
      const ms = Number(step.ms ?? 0);
      if (!Number.isFinite(ms) || ms < 0) {
        throw new Error("sleep ms must be a non-negative number after argument expansion");
      }
      return await sleepInterruptibly(ms, signal);
    }
    case "log":
      console.log(`[crer] ${step.message}`);
      return;
    case "screenshot":
      return await capture(b, String(step.name ?? "screenshot"), true);
    default:
      throw new Error(`unsupported step: ${step.do}`);
  }
}
async function currentUrl(b: BrowserSession): Promise<string | undefined> {
  try {
    const result = await b.cdp.call<{ result: { value?: string } }>(
      "Runtime.evaluate",
      { expression: "location.href", returnByValue: true },
      b.sessionId,
    );
    return result.result.value;
  } catch {
    return undefined;
  }
}
export async function playScenario(s: Scenario, options: PlayOptions): Promise<RunResult> {
  const runDir = `.crer/runs/${crypto.randomUUID()}`;
  await Deno.mkdir(runDir, { recursive: true });
  const seed = options.seed ?? s.playback?.seed ?? randomSeed();
  await Deno.writeTextFile(
    `${runDir}/run.json`,
    JSON.stringify({ scenario: s.name, seed, startedAt: new Date().toISOString() }, null, 2),
  );
  let b: BrowserSession | undefined;
  let foreground: ForegroundGuard | undefined;
  let foregroundCaptureError: string | undefined;
  const requireForeground = s.browser.window?.foreground === true;
  let foregroundAttemptCount = 0;
  let foregroundLastStatus: number | undefined;
  const failures: string[] = [];
  try {
    if (options.inputDllPath) {
      try {
        foreground = captureForeground(options.inputDllPath);
        await Deno.writeTextFile(
          `${runDir}/foreground.json`,
          JSON.stringify({ before: windowHandleText(foreground.original) }, null, 2) + "\n",
        );
      } catch (error) {
        foregroundCaptureError = String(error);
        await Deno.writeTextFile(
          `${runDir}/foreground.json`,
          JSON.stringify({ captureError: String(error) }, null, 2) + "\n",
        );
      }
    }
    b = await launch(s, options, runDir);
    if (requireForeground && !foreground) {
      throw new Error(
        "browser.window.foreground requires the current crer-win-input.dll native DLL; "
          + "run .\\scripts\\build-native.ps1"
          + (foregroundCaptureError ? ` (${foregroundCaptureError})` : ""),
      );
    }
    if (foreground && requireForeground) {
      foregroundLastStatus = foreground.foregroundProcess(b.process.pid);
      foregroundAttemptCount++;
      if (foregroundLastStatus !== 0) {
        throw new Error(
          `could not bring CfT to the foreground (Win32 status ${foregroundLastStatus})`,
        );
      }
      await Deno.writeTextFile(
        `${runDir}/foreground.json`,
        JSON.stringify(
          {
            before: windowHandleText(foreground.original),
            requested: true,
            initialStatus: foregroundLastStatus,
            attempts: foregroundAttemptCount,
          },
          null,
          2,
        ) + "\n",
      );
    } else if (foreground) {
      const restoreStatus = foreground.restore();
      await Deno.writeTextFile(
        `${runDir}/foreground.json`,
        JSON.stringify({ before: windowHandleText(foreground.original), restoreStatus }, null, 2)
          + "\n",
      );
    }
    const rng = new Random(BigInt(seed));
    const timeout = s.playback?.timeouts?.action_ms ?? 10_000;
    const stepDelayMs = options.stepDelayMs ?? s.playback?.step_delay_ms ?? 0;
    if (!Number.isFinite(stepDelayMs) || stepDelayMs < 0) {
      throw new Error("step_delay_ms must be a non-negative number");
    }
    const appendStepLog = (entry: Record<string, unknown>) =>
      Deno.writeTextFile(`${runDir}/steps.ndjson`, JSON.stringify(entry) + "\n", { append: true });
    const browser = b!;
    const functionDefinition = (name: string) => {
      const raw = s.functions?.[name];
      if (!raw) return undefined;
      return Array.isArray(raw)
        ? { params: [], steps: raw }
        : { params: raw.params ?? [], steps: raw.steps };
    };
    const expandArguments = (value: unknown, args: Record<string, string | number>): unknown => {
      if (typeof value === "string") {
        const wholeReference = /^\$\{([A-Za-z_][A-Za-z0-9_-]*)\}$/.exec(value);
        if (wholeReference && args[wholeReference[1]] !== undefined) return args[wholeReference[1]];
        return value.replace(
          /\$\{([A-Za-z_][A-Za-z0-9_-]*)\}/g,
          (all, name: string) => args[name] === undefined ? all : String(args[name]),
        );
      }
      if (Array.isArray(value)) return value.map((item) => expandArguments(item, args));
      if (value && typeof value === "object") {
        return Object.fromEntries(
          Object.entries(value as Record<string, unknown>).map((
            [key, item],
          ) => [key, expandArguments(item, args)]),
        );
      }
      return value;
    };
    let stopped = false;
    const weekday = (timeZone?: string) => {
      const name = new Intl.DateTimeFormat("en-US", {
        weekday: "short",
        ...(timeZone ? { timeZone } : {}),
      }).format(new Date()).toLowerCase();
      return ({
        mon: "mon",
        tue: "tue",
        wed: "wed",
        thu: "thu",
        fri: "fri",
        sat: "sat",
        sun: "sun",
      } as Record<string, string>)[name];
    };
    const callStack: string[] = [];
    type CachedTemplate = {
      path: string;
      threshold: number;
      match: TemplateMatch;
      screenshot: Uint8Array;
    };
    const executeSteps = async (
      steps: Step[],
      parentIndex = "",
      initialTemplateCache?: CachedTemplate,
    ): Promise<void> => {
      let templateCache = initialTemplateCache;
      for (const [offset, step] of steps.entries()) {
        if (stopped) return;
        // A condition's screenshot is valid only for its immediately following child step.
        const cachedTemplate = templateCache;
        templateCache = undefined;
        const i = parentIndex ? `${parentIndex}.${offset}` : String(offset);
        const startedAt = new Date().toISOString();
        let at: { x: number; y: number } | undefined;
        let jitterOffset: { x: number; y: number } | undefined;
        let templateMatch: TemplateMatch | undefined;
        let succeeded = false;
        let delayHandled = false;
        let stepDelayHandled = false;
        try {
          if (options.signal?.aborted) throw new Error("worker timed out");
          if (requireForeground) {
            foregroundLastStatus = foreground!.foregroundProcess(browser.process.pid);
            foregroundAttemptCount++;
            if (foregroundLastStatus !== 0) {
              throw new Error(
                `could not bring CfT to the foreground before step ${i} (Win32 status ${foregroundLastStatus})`,
              );
            }
          }
          if (step.do === "call") {
            const name = step.function!;
            const definition = functionDefinition(name);
            if (!definition) throw new Error(`undefined function: ${name}`);
            if (callStack.includes(name)) {
              throw new Error(`recursive function call: ${[...callStack, name].join(" -> ")}`);
            }
            await appendStepLog({
              index: i,
              do: step.do,
              startedAt,
              completedAt: new Date().toISOString(),
              function: name,
              url: await currentUrl(browser),
              status: "ok",
            });
            callStack.push(name);
            try {
              const body = expandArguments(definition.steps, step.args ?? {}) as Step[];
              await executeSteps(body, `${i}.${name}`);
            } finally {
              callStack.pop();
            }
            succeeded = true;
          } else if (step.do === "repeat") {
            const count = Number(step.count);
            if (!Number.isInteger(count) || count < 0) {
              throw new Error(
                "repeat count must be a non-negative integer after argument expansion",
              );
            }
            await appendStepLog({
              index: i,
              do: step.do,
              startedAt,
              completedAt: new Date().toISOString(),
              count,
              url: await currentUrl(browser),
              status: "ok",
            });
            for (let iteration = 0; iteration < count; iteration++) {
              await executeSteps(step.steps ?? [], `${i}.${iteration}`);
              if (stopped) break;
            }
            succeeded = true;
          } else if (step.do === "repeat_until") {
            const template = step.template as {
              path: string;
              min_similarity?: number;
              random_inset_px?: number;
              on_missing?: "fail" | "skip";
            };
            const defaults = s.playback?.template;
            const threshold = template.min_similarity ?? defaults?.min_similarity ?? 0.8;
            const desiredVisible = step.state === "visible";
            const maxAttempts = Number(step.max_attempts);
            if (!Number.isInteger(maxAttempts) || maxAttempts < 1) {
              throw new Error(
                "repeat_until max_attempts must be a positive integer after argument expansion",
              );
            }
            let attempts = 0;
            while (true) {
              const found = await matchTemplate(
                (method, params) => browser.cdp.call(method, params, browser.sessionId),
                template,
                options.templateBaseDir,
              );
              await Deno.writeFile(
                `${runDir}/template-${i}.attempt-${attempts}.png`,
                found.screenshot,
              );
              templateMatch = found.match;
              console.log(
                `[crer] template: ${template.path}, similarity: ${
                  templateMatch.similarity.toFixed(4)
                }, threshold: ${threshold}`,
              );
              const visible = templateMatch.similarity >= threshold;
              if (visible === desiredVisible) {
                await appendStepLog({
                  index: i,
                  do: step.do,
                  startedAt,
                  completedAt: new Date().toISOString(),
                  templateMatch,
                  url: await currentUrl(browser),
                  status: "ok",
                  state: step.state,
                  attempts,
                });
                succeeded = true;
                break;
              }
              if (attempts >= maxAttempts) {
                const error =
                  `repeat_until limit reached: template ${template.path} did not become ${step.state} after ${maxAttempts} attempts (similarity ${
                    templateMatch.similarity.toFixed(4)
                  }, threshold ${threshold})`;
                if (step.on_limit === "continue") {
                  await appendStepLog({
                    index: i,
                    do: step.do,
                    startedAt,
                    completedAt: new Date().toISOString(),
                    templateMatch,
                    url: await currentUrl(browser),
                    status: "skipped",
                    kind: "template",
                    reason: error,
                    state: step.state,
                    attempts,
                  });
                  succeeded = true;
                  break;
                }
                throw new Error(error);
              }
              attempts++;
              await executeSteps(
                step.steps ?? [],
                `${i}.${attempts - 1}`,
                {
                  path: template.path,
                  threshold,
                  match: found.match,
                  screenshot: found.screenshot,
                },
              );
              if (stopped) {
                succeeded = true;
                break;
              }
            }
          } else if (step.do === "for_each_template") {
            const template = step.template as {
              path: string;
              min_similarity?: number;
              random_inset_px?: number;
              on_missing?: "fail" | "skip";
            };
            const defaults = s.playback?.template;
            const threshold = template.min_similarity ?? defaults?.min_similarity ?? 0.8;
            const maxMatches = Number(step.max_matches);
            if (!Number.isInteger(maxMatches) || maxMatches < 1 || maxMatches > 100) {
              throw new Error(
                "for_each_template max_matches must be an integer from 1 through 100 after argument expansion",
              );
            }
            const found = await matchTemplates(
              (method, params) => browser.cdp.call(method, params, browser.sessionId),
              template,
              options.templateBaseDir,
              threshold,
              maxMatches,
            );
            await Deno.writeFile(`${runDir}/template-${i}.png`, found.screenshot);
            console.log(
              `[crer] template: ${template.path}, matches: ${found.matches.length}, threshold: ${threshold}`,
            );
            if (found.matches.length === 0) {
              const error =
                `template match failed: ${template.path} has no matches at threshold ${threshold}`;
              if ((template.on_missing ?? defaults?.on_missing ?? "fail") === "skip") {
                await appendStepLog({
                  index: i,
                  do: step.do,
                  startedAt,
                  completedAt: new Date().toISOString(),
                  url: await currentUrl(browser),
                  status: "skipped",
                  kind: "template",
                  reason: error,
                  matches: 0,
                  templateMatches: [],
                });
                succeeded = true;
              } else {
                throw new Error(error);
              }
            } else {
              await appendStepLog({
                index: i,
                do: step.do,
                startedAt,
                completedAt: new Date().toISOString(),
                url: await currentUrl(browser),
                status: "ok",
                matches: found.matches.length,
                templateMatches: found.matches,
              });
              for (const [matchIndex, match] of found.matches.entries()) {
                const body = expandArguments(step.steps ?? [], {
                  match_left: match.x,
                  match_top: match.y,
                  match_width: match.width,
                  match_height: match.height,
                  match_center_x: match.x + Math.floor(match.width / 2),
                  match_center_y: match.y + Math.floor(match.height / 2),
                  match_similarity: match.similarity,
                }) as Step[];
                await executeSteps(body, `${i}.${matchIndex}`);
                if (stopped) break;
              }
              succeeded = true;
            }
          } else if (step.do === "if" && step.equals) {
            const matched = step.equals.left === step.equals.right;
            await appendStepLog({
              index: i,
              do: step.do,
              startedAt,
              completedAt: new Date().toISOString(),
              condition: { equals: step.equals },
              url: await currentUrl(browser),
              status: "ok",
              matched,
              branch: matched ? "then" : "else",
            });
            await executeSteps(matched ? step.then ?? [] : step.else ?? [], i);
            succeeded = true;
          } else if (step.do === "if" && step.weekdays) {
            const current = weekday(step.time_zone);
            const matched = step.weekdays.includes(current);
            await appendStepLog({
              index: i,
              do: step.do,
              startedAt,
              completedAt: new Date().toISOString(),
              condition: { weekdays: step.weekdays, time_zone: step.time_zone, current },
              url: await currentUrl(browser),
              status: "ok",
              matched,
              branch: matched ? "then" : "else",
            });
            await executeSteps(matched ? step.then ?? [] : step.else ?? [], i);
            succeeded = true;
          } else if (step.template) {
            const template = step.template as {
              path: string;
              min_similarity?: number;
              random_inset_px?: number;
              on_missing?: "fail" | "skip";
            };
            const defaults = s.playback?.template;
            const threshold = template.min_similarity ?? defaults?.min_similarity ?? 0.8;
            let screenshot: Uint8Array;
            const reused = cachedTemplate?.path === template.path
              && cachedTemplate.threshold === threshold;
            if (reused) {
              screenshot = cachedTemplate.screenshot;
              templateMatch = cachedTemplate.match;
            } else {
              const found = await matchTemplate(
                (method, params) => browser.cdp.call(method, params, browser.sessionId),
                template,
                options.templateBaseDir,
              );
              screenshot = found.screenshot;
              templateMatch = found.match;
            }
            await Deno.writeFile(`${runDir}/template-${i}.png`, screenshot);
            console.log(
              `[crer] template: ${template.path}, similarity: ${
                templateMatch.similarity.toFixed(4)
              }, threshold: ${threshold}${reused ? " (reused)" : ""}`,
            );
            const matchError = `template match failed: ${template.path} similarity ${
              templateMatch.similarity.toFixed(4)
            } is below ${threshold}`;
            if (step.do === "if") {
              const matched = templateMatch.similarity >= threshold;
              await appendStepLog({
                index: i,
                do: step.do,
                startedAt,
                completedAt: new Date().toISOString(),
                templateMatch,
                url: await currentUrl(browser),
                status: "ok",
                matched,
                branch: matched ? "then" : "else",
                ...(matched ? {} : { reason: matchError }),
              });
              await executeSteps(
                matched ? step.then ?? [] : step.else ?? [],
                i,
                { path: template.path, threshold, match: templateMatch, screenshot },
              );
              succeeded = true;
            }
            if (templateMatch.similarity < threshold) {
              if (step.do === "click" && step.then) {
                await appendStepLog({
                  index: i,
                  do: step.do,
                  startedAt,
                  completedAt: new Date().toISOString(),
                  templateMatch,
                  url: await currentUrl(browser),
                  status: "skipped",
                  matched: false,
                  reason: matchError,
                });
                succeeded = true;
                delayHandled = true;
              } else if (
                step.do !== "if"
                && (template.on_missing ?? defaults?.on_missing ?? "fail") === "skip"
              ) {
                await appendStepLog({
                  index: i,
                  do: step.do,
                  startedAt,
                  completedAt: new Date().toISOString(),
                  templateMatch,
                  url: await currentUrl(browser),
                  status: "skipped",
                  kind: "template",
                  reason: matchError,
                });
                succeeded = true;
              } else if (step.do !== "if") {
                throw new Error(matchError);
              }
            }
            if (!succeeded) {
              at = randomPointInMatch(
                templateMatch,
                template.random_inset_px ?? defaults?.random_inset_px ?? 0,
                () => rng.next(),
              );
            }
          } else if (step.at) {
            at = jitter(step.at, step.jitter ?? s.playback?.jitter, rng, browser.viewport);
            if (!at) throw new Error("jitter bounds failure");
            jitterOffset = { x: at.x - step.at.x, y: at.y - step.at.y };
          }
          if (!succeeded) {
            if (step.do === "click") {
              const count = Number(step.count ?? 1);
              if (!Number.isInteger(count) || count < 1) {
                throw new Error("click count must be a positive integer after argument expansion");
              }
              for (let iteration = 0; iteration < count; iteration++) {
                await act(browser, step, at, s.playback?.jitter, rng, timeout, options.signal);
                if (iteration === count - 1) {
                  await appendStepLog({
                    index: i,
                    do: step.do,
                    startedAt,
                    completedAt: new Date().toISOString(),
                    ...(at ? { at } : {}),
                    ...(jitterOffset ? { jitterOffset } : {}),
                    ...(templateMatch ? { templateMatch } : {}),
                    ...(count === 1 ? {} : { count }),
                    url: await currentUrl(browser),
                    status: "ok",
                  });
                }
                if (step.delay_ms !== undefined) {
                  const delay = Number(step.delay_ms);
                  if (!Number.isFinite(delay) || delay < 0) {
                    throw new Error(
                      "delay_ms must be a non-negative number after argument expansion",
                    );
                  }
                  await sleepInterruptibly(delay, options.signal);
                }
                if (stepDelayMs > 0) await sleepInterruptibly(stepDelayMs, options.signal);
              }
              succeeded = true;
              delayHandled = true;
              stepDelayHandled = true;
              if (step.template && step.then) {
                await executeSteps(step.then, i);
              }
            } else {
              await act(browser, step, at, s.playback?.jitter, rng, timeout, options.signal);
              await appendStepLog({
                index: i,
                do: step.do,
                startedAt,
                completedAt: new Date().toISOString(),
                ...(at ? { at } : {}),
                ...(jitterOffset ? { jitterOffset } : {}),
                ...(templateMatch ? { templateMatch } : {}),
                url: await currentUrl(browser),
                status: "ok",
              });
              succeeded = true;
            }
          }
        } catch (e) {
          const kind = failureFor(step, e);
          await appendStepLog({
            index: i,
            do: step.do,
            startedAt,
            completedAt: new Date().toISOString(),
            ...(at ? { at } : {}),
            ...(jitterOffset ? { jitterOffset } : {}),
            ...(templateMatch ? { templateMatch } : {}),
            ...(step.do === "log" ? { message: step.message } : {}),
            url: await currentUrl(browser),
            status: "failed",
            kind,
            error: String(e),
          });
          await capture(browser, `failure-${i}`);
          failures.push(`${i}:${kind}:${e}`);
          const policy = s.playback?.on_failure?.[kind] ?? s.playback?.on_failure?.default
            ?? "abort";
          if (policy === "abort") {
            stopped = true;
            return;
          }
        }
        if (stopped) return;
        if (succeeded && step.delay_ms !== undefined && !delayHandled) {
          const delay = Number(step.delay_ms);
          if (!Number.isFinite(delay) || delay < 0) {
            throw new Error("delay_ms must be a non-negative number after argument expansion");
          }
          await sleepInterruptibly(delay, options.signal);
        }
        if (stepDelayMs > 0 && !stepDelayHandled) {
          await sleepInterruptibly(stepDelayMs, options.signal);
        }
      }
    };
    await executeSteps(s.steps);
    return { code: failures.length ? 4 : 0, failures, runDir };
  } catch (e) {
    return { code: 3, failures: [String(e)], runDir };
  } finally {
    if (b) {
      await closeBrowser(b);
    }
    if (foreground && requireForeground) {
      const restoreStatus = foreground.restore();
      await Deno.writeTextFile(
        `${runDir}/foreground.json`,
        JSON.stringify(
          {
            before: windowHandleText(foreground.original),
            requested: true,
            attempts: foregroundAttemptCount,
            lastStatus: foregroundLastStatus,
            restoreStatus,
          },
          null,
          2,
        ) + "\n",
      );
    }
    if (
      !options.keepArtifacts && !options.profileDir && !scenarioProfileDir(s)
    ) {
      /* run metadata and diagnostics stay; only browser profile is disposable */ try {
        await Deno.remove(`${runDir}/profile`, { recursive: true });
      } catch { /* ignored */ }
    }
    foreground?.close();
  }
}
