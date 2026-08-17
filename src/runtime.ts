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
};
type BrowserSession = {
  cdp: Cdp;
  sessionId: string;
  windowId: number;
  process: Deno.ChildProcess;
  runDir: string;
  viewport: { x: number; y: number };
};

async function waitPort(file: string): Promise<[number, string]> {
  for (let i = 0; i < 150; i++) {
    try {
      const [port, path] = decoder.decode(await Deno.readFile(file)).trim().split(/\r?\n/);
      if (port && path) return [Number(port), path];
    } catch { /* wait */ }
    await sleep(100);
  }
  throw new Error("DevToolsActivePort was not created");
}
async function launch(s: Scenario, options: PlayOptions, runDir: string): Promise<BrowserSession> {
  const profile = `${runDir}/profile`;
  await Deno.mkdir(profile, { recursive: true });
  const p = new Deno.Command(options.chromePath, {
    args: [
      "--remote-debugging-port=0",
      "--remote-debugging-address=127.0.0.1",
      `--user-data-dir=${profile}`,
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-sync",
      "--new-window",
      "about:blank",
    ],
    stdout: "null",
    stderr: "piped",
  }).spawn();
  const [port] = await waitPort(`${profile}/DevToolsActivePort`);
  const version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json() as {
    webSocketDebuggerUrl: string;
  };
  const cdp = new Cdp(version.webSocketDebuggerUrl);
  await cdp.open();
  const target = await cdp.call<{ targetId: string }>("Target.createTarget", {
    url: s.browser.initial_url,
    newWindow: true,
  });
  const attached = await cdp.call<{ sessionId: string }>("Target.attachToTarget", {
    targetId: target.targetId,
    flatten: true,
  });
  const window = await cdp.call<{ windowId: number }>("Browser.getWindowForTarget", {
    targetId: target.targetId,
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
    windowId: window.windowId,
    process: p,
    runDir,
    viewport: { x: viewport.width, y: viewport.height },
  };
}
async function capture(b: BrowserSession, name: string) {
  try {
    const r = await b.cdp.call<{ data: string }>(
      "Page.captureScreenshot",
      { format: "png" },
      b.sessionId,
    );
    await Deno.writeFile(
      `${b.runDir}/${name}.png`,
      Uint8Array.from(atob(r.data), (x) => x.charCodeAt(0)),
    );
  } catch { /* diagnostics must not mask original failure */ }
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
    const value = await b.cdp.call<{ result: { value?: { url: string; state: string } } }>(
      "Runtime.evaluate",
      { expression: "({url:location.href,state:document.readyState})", returnByValue: true },
      b.sessionId,
    );
    const state = value.result.value;
    const urlOk = !step.url
      || new RegExp("^" + step.url.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace("*", ".*") + "$")
        .test(state?.url ?? "");
    const stateOk = !step.state || step.state === "network_idle"
      ? state?.state === "complete"
      : true;
    if (urlOk && stateOk) return;
    await sleep(100);
  }
  throw new Error("wait_for timed out");
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
};
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
      const [key, vk] = keys[step.key ?? ""] ?? [step.key ?? "", (step.key ?? "").charCodeAt(0)];
      await call("Input.dispatchKeyEvent", { type: "keyDown", key, windowsVirtualKeyCode: vk });
      return await call("Input.dispatchKeyEvent", {
        type: "keyUp",
        key,
        windowsVirtualKeyCode: vk,
      });
    }
    case "sleep":
      return await sleep(Number(step.ms ?? 0));
    case "screenshot":
      return await capture(b, String(step.name ?? "screenshot"));
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
