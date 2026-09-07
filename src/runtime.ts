import { executeAtomicAction } from "./actions.ts";
import { Cdp } from "./cdp.ts";
import { EnvironmentError, ExecutionAbortedError, InterruptedError } from "./errors.ts";
import { cleanupErrors } from "./input_guard.ts";
import { jitter, Random, randomSeed } from "./prng.ts";
import { persistentProfileDirectory, prepareChromeProfile } from "./profiles.ts";
import {
  matchTemplate,
  matchTemplates,
  randomPointInMatch,
  type TemplateMatch,
  TemplateMatchError,
} from "./template.ts";
import type {
  FailureKind,
  Jitter,
  RunResult,
  Scenario,
  Step,
  TemplateScreenshotPolicy,
  WindowBounds,
} from "./types.ts";

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
class ForEachBreak {}
async function collectText(
  stream: ReadableStream<Uint8Array>,
  limit = 64 * 1024,
): Promise<string> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let text = "";
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      text += decoder.decode(value, { stream: true });
      if (text.length > limit) text = text.slice(-limit);
    }
    text += decoder.decode();
    return text;
  } finally {
    reader.releaseLock();
  }
}
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
async function sleepInterruptibly(ms: number, signal?: AbortSignal): Promise<void> {
  if (!signal) return await sleep(ms);
  const interrupted = () =>
    signal.reason instanceof Error ? signal.reason : new InterruptedError("interrupted");
  if (signal.aborted) throw interrupted();
  let onAbort: (() => void) | undefined;
  try {
    await Promise.race([
      sleep(ms),
      new Promise<void>((_, reject) => {
        onAbort = () => reject(interrupted());
        signal.addEventListener("abort", onAbort, { once: true });
      }),
    ]);
  } finally {
    if (onAbort) signal.removeEventListener("abort", onAbort);
  }
}
export type PlayOptions = {
  chromePath: string;
  inputDllPath?: string;
  position?: { left: number; top: number };
  /** run-only override that replaces, rather than merges with, scenario window bounds. */
  boundsOverride?: WindowBounds;
  seed?: string;
  keepArtifacts?: boolean;
  stepDelayMs?: number;
  ignoreViewportMismatch?: boolean;
  /** Mute audio from the dedicated CfT process only. */
  muteAudio?: boolean;
  profileDir?: string;
  templateBaseDir?: string;
  templateScreenshots?: TemplateScreenshotPolicy;
  signal?: AbortSignal;
  sharedSession?: SharedBrowserSession;
};
type ForegroundGuard = {
  original: bigint;
  setProcessTopmost: (pid: number, enabled: boolean) => number;
  foregroundProcess: (pid: number) => number;
  restore: () => number;
  close: () => void;
};
const windowHandleText = (handle: bigint) => `0x${handle.toString(16)}`;
function captureForeground(dllPath: string): ForegroundGuard {
  const lib = Deno.dlopen(dllPath, {
    crer_input_get_foreground_window: { parameters: [], result: "usize" },
    crer_input_set_process_topmost_only: { parameters: ["u32", "i32"], result: "i32" },
    crer_input_foreground_process_window: { parameters: ["u32"], result: "i32" },
    crer_input_restore_foreground_window: { parameters: ["usize"], result: "i32" },
  });
  const original = lib.symbols.crer_input_get_foreground_window();
  return {
    original,
    setProcessTopmost: (pid, enabled) =>
      lib.symbols.crer_input_set_process_topmost_only(pid, enabled ? 1 : 0),
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
  stderr: Promise<string>;
  runDir: string;
  viewport: { x: number; y: number };
  network: NetworkTracker;
};

export type SharedBrowserSession = {
  focusPolicy: "once" | "before-step";
  browser?: BrowserSession;
  profileDir?: string;
  foreground?: ForegroundGuard;
  topmostTimer?: ReturnType<typeof setInterval>;
  topmostRequested: boolean;
  topmostAttempts: number;
  topmostStatus?: number;
  foregroundStatus?: number;
  focusedOnce: boolean;
  lastWarnedTopmostStatus?: number;
  lastWarnedForegroundStatus?: number;
};

export function createSharedBrowserSession(
  focusPolicy: "once" | "before-step" = "once",
): SharedBrowserSession {
  return {
    focusPolicy,
    topmostRequested: false,
    topmostAttempts: 0,
    focusedOnce: false,
  };
}

export function scenarioProfileDirectory(s: Scenario): string | undefined {
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
  reset() {
    this.#requests.clear();
    this.#lastActivity = Date.now();
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

export async function awaitChromeEndpoint<T>(
  endpoint: Promise<T>,
  processStatus: Promise<{ code: number; signal?: string | null }>,
): Promise<T> {
  const outcome = await Promise.race([
    endpoint.then((value) => ({ value })),
    processStatus.then((status) => ({ status })),
  ]);
  if ("status" in outcome) {
    const signal = outcome.status.signal ? `, signal ${outcome.status.signal}` : "";
    throw new EnvironmentError(
      `Chrome exited before the CDP endpoint was ready (code ${outcome.status.code}${signal})`,
    );
  }
  return outcome.value;
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
  const stderr = await browser.stderr.catch((error) => `could not read Chrome stderr: ${error}`);
  await Deno.writeTextFile(
    `${browser.runDir}/shutdown.json`,
    JSON.stringify(
      {
        graceful,
        exited: status !== undefined,
        ...(stderr ? { stderr } : {}),
        ...(status ? { code: status.code, success: status.success, signal: status.signal } : {}),
      },
      null,
      2,
    ) + "\n",
  ).catch(() => {});
}

function stopTopmostMonitor(shared: SharedBrowserSession) {
  if (shared.topmostTimer !== undefined) clearInterval(shared.topmostTimer);
  shared.topmostTimer = undefined;
}

function applySharedTopmost(shared: SharedBrowserSession): number {
  if (!shared.browser || !shared.foreground) return 1168;
  let status: number;
  try {
    status = shared.foreground.setProcessTopmost(shared.browser.process.pid, true);
  } catch (error) {
    console.warn(`Warning: could not keep CfT topmost (${error})`);
    status = 1;
  }
  shared.topmostAttempts++;
  shared.topmostStatus = status;
  if (status !== 0 && status !== shared.lastWarnedTopmostStatus) {
    console.warn(`Warning: could not keep CfT topmost (Win32 status ${status})`);
    shared.lastWarnedTopmostStatus = status;
  }
  return status;
}

function focusSharedBrowser(shared: SharedBrowserSession): number {
  if (!shared.browser || !shared.foreground) return 1168;
  const status = shared.foreground.foregroundProcess(shared.browser.process.pid);
  shared.foregroundStatus = status;
  if (status !== 0 && status !== shared.lastWarnedForegroundStatus) {
    console.warn(`Warning: Windows did not grant CfT foreground focus (Win32 status ${status})`);
    shared.lastWarnedForegroundStatus = status;
  }
  shared.focusedOnce = true;
  return status;
}

function startTopmostMonitor(shared: SharedBrowserSession) {
  if (shared.topmostTimer !== undefined) return;
  shared.topmostTimer = setInterval(() => {
    if (!shared.topmostRequested || !shared.browser || !shared.foreground) return;
    applySharedTopmost(shared);
  }, 250);
}

async function writeSharedForeground(shared: SharedBrowserSession, runDir: string) {
  await Deno.writeTextFile(
    `${runDir}/foreground.json`,
    JSON.stringify(
      {
        before: shared.foreground ? windowHandleText(shared.foreground.original) : undefined,
        requested: shared.topmostRequested,
        sharedSession: true,
        topmostAttempts: shared.topmostAttempts,
        topmostStatus: shared.topmostStatus,
        foregroundStatus: shared.foregroundStatus,
        focusPolicy: shared.focusPolicy,
      },
      null,
      2,
    ) + "\n",
  );
}

export async function closeSharedBrowserSession(shared: SharedBrowserSession): Promise<void> {
  stopTopmostMonitor(shared);
  const browser = shared.browser;
  const foreground = shared.foreground;
  let clearTopmostStatus: number | undefined;
  if (browser && foreground && shared.topmostRequested) {
    clearTopmostStatus = foreground.setProcessTopmost(browser.process.pid, false);
  }
  if (browser) await closeBrowser(browser);
  const restoreStatus = shared.focusedOnce ? foreground?.restore() : undefined;
  if (browser) {
    await Deno.writeTextFile(
      `${browser.runDir}/foreground.json`,
      JSON.stringify(
        {
          before: foreground ? windowHandleText(foreground.original) : undefined,
          requested: shared.topmostRequested,
          sharedSession: true,
          topmostAttempts: shared.topmostAttempts,
          topmostStatus: shared.topmostStatus,
          foregroundStatus: shared.foregroundStatus,
          focusPolicy: shared.focusPolicy,
          clearTopmostStatus,
          restoreStatus,
        },
        null,
        2,
      ) + "\n",
    );
  }
  foreground?.close();
  shared.browser = undefined;
  shared.profileDir = undefined;
  shared.foreground = undefined;
  shared.topmostRequested = false;
  shared.focusedOnce = false;
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
  const configuredProfile = options.profileDir ?? scenarioProfileDirectory(s);
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
  const stderr = collectText(p.stderr);
  let cdp: Cdp | undefined;
  let stage = "chrome_started";
  const writeLaunchDiagnostic = async (error?: unknown, chromeStderr?: string) => {
    await Deno.writeTextFile(
      `${runDir}/launch.json`,
      JSON.stringify(
        {
          stage,
          ...(error ? { error: String(error) } : {}),
          ...(chromeStderr ? { stderr: chromeStderr } : {}),
        },
        null,
        2,
      ) + "\n",
    );
  };
  try {
    const version = await awaitChromeEndpoint(waitEndpoint(port), p.status);
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
    const bounds = options.boundsOverride ?? {
      ...(s.browser.window?.bounds ?? {}),
      ...(options.position ?? {}),
    };
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
      stderr,
      runDir,
      viewport,
      network,
    };
  } catch (error) {
    cdp?.close();
    await terminateProcess(p);
    await writeLaunchDiagnostic(error, await stderr.catch(() => "")).catch(() => {});
    throw error;
  }
}

async function prepareReusedBrowser(
  browser: BrowserSession,
  s: Scenario,
  options: PlayOptions,
  runDir: string,
) {
  browser.runDir = runDir;
  browser.network.reset();
  await Deno.writeTextFile(
    `${runDir}/launch.json`,
    JSON.stringify({ stage: "reusing_browser_session", profile: options.profileDir }, null, 2)
      + "\n",
  );
  await browser.cdp.call("Page.navigate", { url: s.browser.initial_url }, browser.sessionId);
  const bounds = options.boundsOverride ?? {
    ...(s.browser.window?.bounds ?? {}),
    ...(options.position ?? {}),
  };
  if (Object.keys(bounds).length) {
    await browser.cdp.call("Browser.setWindowBounds", {
      windowId: browser.windowId,
      bounds: { windowState: "normal", ...bounds },
    });
  }
  if (s.browser.window?.content) {
    await setContentSize(
      browser.cdp,
      browser.sessionId,
      browser.windowId,
      s.browser.window.content,
    );
  }
  await waitForDocumentReady(browser.cdp, browser.sessionId);
  const expectedViewport = s.browser.window?.viewport ?? s.browser.window?.content;
  if (expectedViewport && !options.ignoreViewportMismatch) {
    await waitForExpectedViewport(browser.cdp, browser.sessionId, expectedViewport);
  } else {
    await waitForStableViewport(browser.cdp, browser.sessionId);
  }
  browser.viewport = await validateDisplay(
    browser.cdp,
    browser.sessionId,
    s,
    runDir,
    options.ignoreViewportMismatch ?? false,
  );
  await Deno.writeTextFile(
    `${runDir}/launch.json`,
    JSON.stringify({ stage: "ready", reused: true, profile: options.profileDir }, null, 2) + "\n",
  );
}

async function acquireSharedBrowser(
  shared: SharedBrowserSession,
  s: Scenario,
  options: PlayOptions,
  runDir: string,
  profileDir: string,
): Promise<{ browser: BrowserSession; reused: boolean }> {
  if (
    shared.browser && shared.profileDir
    && shared.profileDir.toLowerCase() === profileDir.toLowerCase()
  ) {
    await prepareReusedBrowser(shared.browser, s, { ...options, profileDir }, runDir);
    return { browser: shared.browser, reused: true };
  }
  if (shared.browser || shared.foreground) await closeSharedBrowserSession(shared);
  if (options.inputDllPath) {
    try {
      shared.foreground = captureForeground(options.inputDllPath);
    } catch (error) {
      await Deno.writeTextFile(
        `${runDir}/foreground.json`,
        JSON.stringify({ captureError: String(error), sharedSession: true }, null, 2) + "\n",
      );
    }
  }
  try {
    const browser = await launch(s, { ...options, profileDir }, runDir);
    shared.browser = browser;
    shared.profileDir = profileDir;
    shared.topmostAttempts = 0;
    shared.topmostStatus = undefined;
    shared.foregroundStatus = undefined;
    shared.focusedOnce = false;
    shared.lastWarnedTopmostStatus = undefined;
    shared.lastWarnedForegroundStatus = undefined;
    return { browser, reused: false };
  } catch (error) {
    shared.foreground?.close();
    shared.foreground = undefined;
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

async function writeBase64Png(path: string, base64: string) {
  await Deno.writeFile(path, Uint8Array.from(atob(base64), (x) => x.charCodeAt(0)));
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
    if (signal?.aborted) {
      throw signal.reason instanceof Error ? signal.reason : new InterruptedError("interrupted");
    }
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
async function executeLeafStep(
  b: BrowserSession,
  step: Step,
  at: { x: number; y: number } | undefined,
  j: Jitter | undefined,
  rng: Random,
  timeout: number,
  signal?: AbortSignal,
) {
  const call = (m: string, p: Record<string, unknown>) => b.cdp.call(m, p, b.sessionId);
  return await executeAtomicAction(
    {
      call,
      viewport: b.viewport,
      waitFor: (candidate, limit, abort) => waitFor(b, candidate, limit, abort),
      assertState: (candidate) => assertState(b, candidate),
      capture: (name) => capture(b, name, true),
      sleep: sleepInterruptibly,
    },
    step,
    at,
    j,
    rng,
    timeout,
    signal,
  );
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
  const templateScreenshots = options.templateScreenshots
    ?? s.playback?.artifacts?.template_screenshots
    ?? "all";
  await Deno.writeTextFile(
    `${runDir}/run.json`,
    JSON.stringify(
      {
        scenario: s.name,
        seed,
        startedAt: new Date().toISOString(),
        templateScreenshots,
        ...(options.boundsOverride ? { windowBoundsOverride: options.boundsOverride } : {}),
      },
      null,
      2,
    ),
  );
  let b: BrowserSession | undefined;
  let foreground: ForegroundGuard | undefined;
  const requireForeground = s.browser.window?.foreground === true;
  let topmostStatus: number | undefined;
  let foregroundStatus: number | undefined;
  let topmostAttempts = 0;
  let lastWarnedForegroundStatus: number | undefined;
  let sharedManaged = false;
  let discardShared = false;
  const failures: string[] = [];
  const abortBrowser = () => {
    if (b) void b.cdp.call("Browser.close").catch(() => {});
  };
  options.signal?.addEventListener("abort", abortBrowser, { once: true });
  try {
    const configuredProfile = options.profileDir ?? scenarioProfileDirectory(s);
    if (options.sharedSession && configuredProfile) {
      sharedManaged = true;
      const profileDir = await persistentProfileDirectory(configuredProfile);
      const acquired = await acquireSharedBrowser(
        options.sharedSession,
        s,
        options,
        runDir,
        profileDir,
      );
      b = acquired.browser;
      foreground = options.sharedSession.foreground;
      await Deno.writeTextFile(
        `${runDir}/run.json`,
        JSON.stringify(
          {
            scenario: s.name,
            seed,
            startedAt: new Date().toISOString(),
            templateScreenshots,
            browserSession: { reused: acquired.reused, profile: profileDir },
            ...(options.boundsOverride ? { windowBoundsOverride: options.boundsOverride } : {}),
          },
          null,
          2,
        ),
      );
    } else {
      // Reuse is deliberately limited to consecutive scenarios with the same persistent
      // profile. An ephemeral scenario forms a session boundary and closes any retained CfT.
      if (options.sharedSession?.browser || options.sharedSession?.foreground) {
        await closeSharedBrowserSession(options.sharedSession);
      }
      if (options.inputDllPath) {
        try {
          foreground = captureForeground(options.inputDllPath);
          await Deno.writeTextFile(
            `${runDir}/foreground.json`,
            JSON.stringify({ before: windowHandleText(foreground.original) }, null, 2) + "\n",
          );
        } catch (error) {
          await Deno.writeTextFile(
            `${runDir}/foreground.json`,
            JSON.stringify({ captureError: String(error) }, null, 2) + "\n",
          );
        }
      }
      b = await launch(s, options, runDir);
    }
    if (requireForeground && !foreground) {
      throw new Error("browser.window.foreground requires the crer-win-input.dll native DLL");
    }
    if (sharedManaged) {
      const shared = options.sharedSession!;
      if (requireForeground) {
        shared.topmostRequested = true;
        topmostStatus = applySharedTopmost(shared);
        if (!shared.focusedOnce) foregroundStatus = focusSharedBrowser(shared);
        startTopmostMonitor(shared);
      } else if (shared.topmostRequested && foreground && b) {
        stopTopmostMonitor(shared);
        topmostStatus = foreground.setProcessTopmost(b.process.pid, false);
        shared.topmostRequested = false;
      }
      await writeSharedForeground(shared, runDir);
    } else if (foreground && requireForeground) {
      topmostStatus = foreground.setProcessTopmost(b!.process.pid, true);
      foregroundStatus = foreground.foregroundProcess(b!.process.pid);
      topmostAttempts++;
      await Deno.writeTextFile(
        `${runDir}/foreground.json`,
        JSON.stringify(
          {
            before: windowHandleText(foreground.original),
            requested: true,
            topmostStatus,
            foregroundStatus,
            topmostAttempts,
          },
          null,
          2,
        ) + "\n",
      );
      if (topmostStatus !== 0) {
        console.warn(
          `Warning: initial CfT topmost request failed (Win32 status ${topmostStatus}); retrying before steps`,
        );
      }
      if (foregroundStatus !== 0) {
        console.warn(
          `Warning: CfT is topmost, but Windows did not grant foreground focus (Win32 status ${foregroundStatus})`,
        );
        lastWarnedForegroundStatus = foregroundStatus;
      }
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
      screenshot: string;
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
        let templateFailureScreenshot: string | undefined;
        let succeeded = false;
        let delayHandled = false;
        let stepDelayHandled = false;
        try {
          if (options.signal?.aborted) {
            throw options.signal.reason instanceof Error
              ? options.signal.reason
              : new InterruptedError("interrupted");
          }
          if (foreground && requireForeground) {
            if (sharedManaged) {
              const shared = options.sharedSession!;
              topmostStatus = applySharedTopmost(shared);
              foregroundStatus = shared.foregroundStatus;
              if (shared.focusPolicy === "before-step") {
                foregroundStatus = focusSharedBrowser(shared);
              }
            } else {
              topmostStatus = foreground.setProcessTopmost(browser.process.pid, true);
              foregroundStatus = foreground.foregroundProcess(browser.process.pid);
              topmostAttempts++;
            }
            if (topmostStatus !== 0) {
              throw new EnvironmentError(
                `could not make CfT topmost before step ${i} (Win32 status ${topmostStatus})`,
              );
            }
            if (
              !sharedManaged && foregroundStatus !== 0
              && foregroundStatus !== lastWarnedForegroundStatus
            ) {
              console.warn(
                `Warning: CfT is topmost before step ${i}, but Windows did not grant foreground focus (Win32 status ${foregroundStatus})`,
              );
              lastWarnedForegroundStatus = foregroundStatus;
            }
          }
          if (step.do === "break") {
            await appendStepLog({
              index: i,
              do: step.do,
              startedAt,
              completedAt: new Date().toISOString(),
              url: await currentUrl(browser),
              status: "ok",
              control: "break",
            });
            throw new ForEachBreak();
          } else if (step.do === "call") {
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
              if (templateScreenshots === "all") {
                await writeBase64Png(
                  `${runDir}/template-${i}.attempt-${attempts}.png`,
                  found.screenshot,
                );
              }
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
                templateFailureScreenshot = found.screenshot;
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
            if (templateScreenshots === "all") {
              await writeBase64Png(`${runDir}/template-${i}.png`, found.screenshot);
            }
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
                templateFailureScreenshot = found.screenshot;
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
                try {
                  await executeSteps(body, `${i}.${matchIndex}`);
                } catch (error) {
                  if (error instanceof ForEachBreak) break;
                  throw error;
                }
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
            let screenshot: string;
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
            if (templateScreenshots === "all") {
              await writeBase64Png(`${runDir}/template-${i}.png`, screenshot);
            }
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
                templateFailureScreenshot = screenshot;
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
                await executeLeafStep(
                  browser,
                  step,
                  at,
                  s.playback?.jitter,
                  rng,
                  timeout,
                  options.signal,
                );
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
              await executeLeafStep(
                browser,
                step,
                at,
                s.playback?.jitter,
                rng,
                timeout,
                options.signal,
              );
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
          if (e instanceof ForEachBreak) throw e;
          if (
            options.signal?.aborted
            && (options.signal.reason instanceof InterruptedError
              || options.signal.reason instanceof ExecutionAbortedError)
          ) throw options.signal.reason;
          if (
            e instanceof EnvironmentError || e instanceof InterruptedError
            || e instanceof ExecutionAbortedError
          ) throw e;
          if (e instanceof TemplateMatchError) templateFailureScreenshot ??= e.screenshot;
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
            ...(cleanupErrors(e) ? { cleanupErrors: cleanupErrors(e) } : {}),
          });
          if (kind === "template" && templateFailureScreenshot) {
            await writeBase64Png(
              `${runDir}/failure-${i}.png`,
              templateFailureScreenshot,
            ).catch(() => capture(browser, `failure-${i}`));
          } else {
            await capture(browser, `failure-${i}`);
          }
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
    discardShared = sharedManaged;
    const terminal = options.signal?.aborted && options.signal.reason instanceof Error
      ? options.signal.reason
      : e;
    const code = terminal instanceof InterruptedError
      ? 5
      : terminal instanceof ExecutionAbortedError
      ? 4
      : 3;
    return { code, failures: [String(terminal)], runDir };
  } finally {
    options.signal?.removeEventListener("abort", abortBrowser);
    if (sharedManaged) {
      const shared = options.sharedSession!;
      await writeSharedForeground(shared, runDir).catch(() => {});
      if (discardShared) await closeSharedBrowserSession(shared);
    } else {
      let clearTopmostStatus: number | undefined;
      if (b && foreground && requireForeground) {
        clearTopmostStatus = foreground.setProcessTopmost(b.process.pid, false);
      }
      if (b) await closeBrowser(b);
      if (foreground && requireForeground) {
        const restoreStatus = foreground.restore();
        await Deno.writeTextFile(
          `${runDir}/foreground.json`,
          JSON.stringify(
            {
              before: windowHandleText(foreground.original),
              requested: true,
              topmostStatus,
              foregroundStatus,
              topmostAttempts,
              clearTopmostStatus,
              restoreStatus,
            },
            null,
            2,
          ) + "\n",
        );
      }
      foreground?.close();
    }
    if (
      !options.keepArtifacts && !options.profileDir && !scenarioProfileDirectory(s)
    ) {
      /* run metadata and diagnostics stay; only browser profile is disposable */ try {
        await Deno.remove(`${runDir}/profile`, { recursive: true });
      } catch { /* ignored */ }
    }
  }
}
