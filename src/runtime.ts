import { Cdp } from "./cdp.ts";
import { jitter, Random, randomSeed } from "./prng.ts";
import type { FailureKind, Jitter, RunResult, Scenario, Step } from "./types.ts";

const decoder = new TextDecoder();
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
export type PlayOptions = {
  chromePath: string;
  position?: { left: number; top: number };
  seed?: string;
  keepArtifacts?: boolean;
  signal?: AbortSignal;
};
type BrowserSession = {
  cdp: Cdp;
  sessionId: string;
  pageDebuggerUrl: string;
  windowId: number;
  process: Deno.ChildProcess;
  runDir: string;
  viewport: { x: number; y: number };
};

async function waitEndpoint(port: number): Promise<{ webSocketDebuggerUrl: string }> {
  for (let i = 0; i < 150; i++) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (response.ok) return await response.json();
    } catch { /* wait */ }
    await sleep(100);
  }
  throw new Error(`CDP endpoint on port ${port} was not available`);
}
async function launch(s: Scenario, options: PlayOptions, runDir: string): Promise<BrowserSession> {
  const profile = `${runDir}/profile`;
  await Deno.mkdir(profile, { recursive: true });
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
      "--new-window",
      s.browser.initial_url,
    ],
    stdout: "null",
    stderr: "piped",
  }).spawn();
  let version: { webSocketDebuggerUrl: string };
  try {
    version = await waitEndpoint(port);
  } catch (error) {
    try {
      p.kill("SIGTERM");
    } catch {
      // The child may have already exited.
    }
    throw error;
  }
  const cdp = new Cdp(version.webSocketDebuggerUrl);
  await cdp.open();
  const targets = await (
    await fetch(`http://127.0.0.1:${port}/json/list`)
  ).json() as Array<{ id: string; type: string; webSocketDebuggerUrl: string }>;
  const target = targets.find((candidate) => candidate.type === "page");
  if (!target) {
    p.kill("SIGTERM");
    cdp.close();
    throw new Error("CfT did not expose a page target");
  }
  const attached = await cdp.call<{ sessionId: string }>("Target.attachToTarget", {
    targetId: target.id,
    flatten: true,
  });
  const window = await cdp.call<{ windowId: number }>("Browser.getWindowForTarget", {
    targetId: target.id,
  });
  const bounds = { ...(s.browser.window?.bounds ?? {}), ...(options.position ?? {}) };
  if (Object.keys(bounds).length) {
    await cdp.call("Browser.setWindowBounds", { windowId: window.windowId, bounds });
  }
  if (s.browser.window?.content) {
    await cdp.call("Browser.setContentsSize", {
      windowId: window.windowId,
      ...s.browser.window.content,
    });
  }
  await cdp.call("Page.enable", {}, attached.sessionId);
  await cdp.call("Runtime.enable", {}, attached.sessionId);
  const viewport = s.browser.window?.content ?? { width: 1280, height: 720 };
  return {
    cdp,
    sessionId: attached.sessionId,
    pageDebuggerUrl: target.webSocketDebuggerUrl,
    windowId: window.windowId,
    process: p,
    runDir,
    viewport: { x: viewport.width, y: viewport.height },
  };
}
async function capture(b: BrowserSession, name: string, required = false) {
  let lastError: unknown;
  for (let attempt = 0; attempt < 2; attempt++) {
    let pageCdp: Cdp | undefined;
    try {
      await b.cdp.call("Page.bringToFront", {}, b.sessionId);
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
  if (e.includes("jitter")) return "jitter_bounds";
  if (step.do === "assert" || step.do === "wait_for") {
    return e.includes("timed out")
      ? "timeout"
      : "assertion";
  }
  if (step.do === "navigate") return "navigation";
  return "action";
}
async function waitFor(b: BrowserSession, step: Step, timeout: number) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const hint = step.locator_hint;
    const expression = `(() => {
      const hint = ${JSON.stringify(hint ?? {})};
      const elements = Array.from(document.querySelectorAll('[role]'));
      const found = !hint.role && !hint.name || elements.some((element) => {
        const role = element.getAttribute('role');
        const name = element.getAttribute('aria-label') || element.textContent?.trim() || '';
        return (!hint.role || role === hint.role) && (!hint.name || name === hint.name) &&
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
    const stateOk = !step.state || step.state === "network_idle"
      ? state?.state === "complete"
      : true;
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
    const found = !hint.role && !hint.name || elements.some((element) => {
      const role = element.getAttribute('role');
      const name = element.getAttribute('aria-label') || element.textContent?.trim() || '';
      return (!hint.role || role === hint.role) && (!hint.name || name === hint.name) &&
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
const keys: Record<string, [string, number]> = {
  Enter: ["Enter", 13],
  Tab: ["Tab", 9],
  Escape: ["Escape", 27],
  Backspace: ["Backspace", 8],
  Delete: ["Delete", 46],
  ArrowDown: ["ArrowDown", 40],
  ArrowUp: ["ArrowUp", 38],
  ArrowLeft: ["ArrowLeft", 37],
  ArrowRight: ["ArrowRight", 39],
  Control: ["Control", 17],
  Alt: ["Alt", 18],
  Shift: ["Shift", 16],
  Meta: ["Meta", 91],
};
const modifierBits: Record<string, number> = { Alt: 1, Control: 2, Meta: 4, Shift: 8 };
const keyInfo = (key: string): [string, number] =>
  keys[key] ?? [key, key.length === 1 ? key.toUpperCase().charCodeAt(0) : 0];
async function act(
  b: BrowserSession,
  step: Step,
  j: Jitter | undefined,
  rng: Random,
  timeout: number,
) {
  const at = step.at ? jitter(step.at, step.jitter ?? j, rng, b.viewport) : undefined;
  if (step.at && !at) throw new Error("jitter bounds failure");
  const call = (m: string, p: Record<string, unknown>) => b.cdp.call(m, p, b.sessionId);
  switch (step.do) {
    case "navigate":
      return await call("Page.navigate", { url: step.url });
    case "wait_for":
      return await waitFor(b, step, timeout);
    case "assert":
      return await assertState(b, step);
    case "click":
    case "double_click": {
      const n = step.do === "click" ? 1 : 2;
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
        clickCount: 1,
      });
      for (let i = 1; i <= 10; i++) {
        const ratio = i / 10;
        await call("Input.dispatchMouseEvent", {
          type: "mouseMoved",
          x: jitteredFrom.x + (jitteredTo.x - jitteredFrom.x) * ratio,
          y: jitteredFrom.y + (jitteredTo.y - jitteredFrom.y) * ratio,
          buttons: 1,
        });
      }
      return await call("Input.dispatchMouseEvent", {
        type: "mouseReleased",
        x: jitteredTo.x,
        y: jitteredTo.y,
        button: "left",
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
      const [key, vk] = keyInfo(step.key ?? "");
      await call("Input.dispatchKeyEvent", { type: "keyDown", key, windowsVirtualKeyCode: vk });
      return await call("Input.dispatchKeyEvent", {
        type: "keyUp",
        key,
        windowsVirtualKeyCode: vk,
      });
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
        const [key, vk] = keyInfo(modifier);
        await call("Input.dispatchKeyEvent", {
          type: "keyDown",
          key,
          windowsVirtualKeyCode: vk,
          modifiers: mask,
        });
        mask |= modifierBits[modifier];
      }
      const [key, vk] = keyInfo(chord.at(-1)! as string);
      await call("Input.dispatchKeyEvent", {
        type: "keyDown",
        key,
        windowsVirtualKeyCode: vk,
        modifiers: mask,
      });
      await call("Input.dispatchKeyEvent", {
        type: "keyUp",
        key,
        windowsVirtualKeyCode: vk,
        modifiers: mask,
      });
      for (const modifier of modifiers.toReversed()) {
        mask &= ~modifierBits[modifier];
        const [modifierKey, modifierVk] = keyInfo(modifier);
        await call("Input.dispatchKeyEvent", {
          type: "keyUp",
          key: modifierKey,
          windowsVirtualKeyCode: modifierVk,
          modifiers: mask,
        });
      }
      return;
    }
    case "sleep":
      return await sleep(Number(step.ms ?? 0));
    case "screenshot":
      return await capture(b, String(step.name ?? "screenshot"), true);
    default:
      throw new Error(`unsupported step: ${step.do}`);
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
  const failures: string[] = [];
  try {
    b = await launch(s, options, runDir);
    const rng = new Random(BigInt(seed));
    const timeout = s.playback?.timeouts?.action_ms ?? 10_000;
    for (const [i, step] of s.steps.entries()) {
      try {
        if (options.signal?.aborted) throw new Error("worker timed out");
        await act(b, step, s.playback?.jitter, rng, timeout);
      } catch (e) {
        const kind = failureFor(step, e);
        await capture(b, `failure-${i}`);
        failures.push(`${i}:${kind}:${e}`);
        const policy = s.playback?.on_failure?.[kind] ?? s.playback?.on_failure?.default ?? "abort";
        if (policy === "abort") break;
      }
    }
    return { code: failures.length ? 4 : 0, failures, runDir };
  } catch (e) {
    return { code: 3, failures: [String(e)], runDir };
  } finally {
    if (b) {
      try {
        await b.cdp.call("Browser.close");
      } catch {
        b.process.kill("SIGTERM");
      }
      b.cdp.close();
    }
    if (
      !options.keepArtifacts
    ) {
      /* run metadata and diagnostics stay; only browser profile is disposable */ try {
        await Deno.remove(`${runDir}/profile`, { recursive: true });
      } catch { /* ignored */ }
    }
  }
}
