import { Cdp } from "./cdp.ts";
import { inspectRun } from "./inspect.ts";
import {
  normalizeRawWithWarnings,
  profileDirFromSidecar,
  qpcFrequencyFromSidecar,
  requestedContentFromSidecar,
  transformFromSidecar,
  windowBoundsFromSidecar,
} from "./normalize.ts";
import { aggregatePlanExitCode, shouldAbortPlan } from "./plan_policy.ts";
import { persistentProfileDirectory } from "./profiles.ts";
import { recordRaw } from "./record.ts";
import { playScenario } from "./runtime.ts";
import { mapWithConcurrency } from "./scheduler.ts";
import type { FailurePolicy, PlanNode, Point, RunResult } from "./types.ts";
import { loadYaml, planFrom, saveYaml, scenarioFrom } from "./yaml.ts";
const [command, file, ...args] = Deno.args;
const option = (name: string) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const configuredChromePath = () => option("--chrome") ?? Deno.env.get("CRER_CHROME");
const chromePath = () => {
  const path = configuredChromePath();
  if (!path) throw new Error("Chrome for Testing must be specified with --chrome or CRER_CHROME");
  return path;
};
const inputDllPath = () => {
  const configured = option("--dll") ?? Deno.env.get("CRER_INPUT_DLL");
  if (configured) return configured;
  if (Deno.build.standalone) {
    const directory = Deno.execPath().replace(/[\\/][^\\/]+$/, "");
    return `${directory}\\crer-win-input.dll`;
  }
  return "native/bin/Release/net10.0/win-x64/publish/crer-win-input.dll";
};
const parentDirectory = (path: string) => path.replace(/[\\/][^\\/]+$/, "");
async function chromeDiagnostic(configured: string | undefined) {
  if (!configured) return { path: "not configured", exists: false };
  let resolved: string;
  try {
    resolved = await Deno.realPath(configured);
  } catch {
    return { path: configured, exists: false };
  }
  let version: string | undefined;
  let sha256: string | undefined;
  try {
    const escapedPath = resolved.replaceAll("'", "''");
    const output = await within(
      new Deno.Command("pwsh.exe", {
        args: [
          "-NoProfile",
          "-NonInteractive",
          "-Command",
          "$item = Get-Item -LiteralPath '" + escapedPath
          + "'; $hash = (Get-FileHash -LiteralPath '" + escapedPath
          + "' -Algorithm SHA256).Hash.ToLowerInvariant(); @{ version = $item.VersionInfo.ProductVersion; sha256 = $hash } | ConvertTo-Json -Compress",
        ],
        stdout: "piped",
        stderr: "piped",
      }).output(),
      15_000,
    );
    if (output.code === 0) {
      const probe = JSON.parse(new TextDecoder().decode(output.stdout)) as {
        version?: unknown;
        sha256?: unknown;
      };
      if (typeof probe.version === "string" && probe.version) version = probe.version;
      if (typeof probe.sha256 === "string" && probe.sha256) sha256 = probe.sha256;
    }
  } catch {
    // The executable existence check above is still useful when version probing is blocked.
  }
  let manifest:
    | {
      path: string;
      requested?: string;
      installed?: string;
      sha256?: string;
      matchesChrome: boolean;
      matchesSha256: boolean | "unverified";
    }
    | undefined;
  const candidates: string[] = [];
  const configuredManifest = Deno.env.get("CRER_CHROME_MANIFEST");
  if (configuredManifest) candidates.push(configuredManifest);
  candidates.push(`${Deno.cwd()}\\.crer\\browsers\\crer-chrome.json`);
  let directory = parentDirectory(resolved);
  for (let i = 0; i < 8 && directory; i++) {
    candidates.push(`${directory}\\crer-chrome.json`);
    directory = parentDirectory(directory);
  }
  for (const candidate of candidates) {
    try {
      const raw = JSON.parse(await Deno.readTextFile(candidate)) as {
        requested?: unknown;
        installed?: unknown;
        chrome?: unknown;
        sha256?: unknown;
      };
      const manifestChrome = typeof raw.chrome === "string" ? raw.chrome : undefined;
      const manifestSha256 = typeof raw.sha256 === "string" ? raw.sha256.toLowerCase() : undefined;
      manifest = {
        path: candidate,
        ...(typeof raw.requested === "string" ? { requested: raw.requested } : {}),
        ...(typeof raw.installed === "string" ? { installed: raw.installed } : {}),
        ...(manifestSha256 ? { sha256: manifestSha256 } : {}),
        matchesChrome: manifestChrome?.toLowerCase() === resolved.toLowerCase(),
        matchesSha256: manifestSha256 && sha256
          ? manifestSha256 === sha256.toLowerCase()
          : "unverified",
      };
      break;
    } catch {
      // Try the next configured or ancestor manifest.
    }
  }
  return {
    path: resolved,
    exists: true,
    version: version ?? "unavailable",
    sha256: sha256 ?? "unavailable",
    isChromeForTesting: !manifest
      ? "unverified"
      : manifest.matchesChrome && manifest.matchesSha256 === true
      ? true
      : manifest.matchesChrome && manifest.matchesSha256 === "unverified"
      ? "unverified"
      : false,
    manifest: manifest ?? "not found",
  };
}
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
function stopOnTerminalEnter(controller: AbortController): () => void {
  const reader = Deno.stdin.readable.getReader();
  const cancel = () => {
    void reader.cancel().catch(() => {});
  };
  controller.signal.addEventListener("abort", cancel, { once: true });
  void (async () => {
    try {
      const result = await reader.read();
      if (!result.done) controller.abort();
    } catch {
      // Ctrl+C and teardown can cancel the terminal read; the normal stop path handles it.
    } finally {
      controller.signal.removeEventListener("abort", cancel);
      reader.releaseLock();
    }
  })();
  return cancel;
}
async function within<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<T>((_, reject) => {
        timer = setTimeout(() => reject(new Error("timed out")), timeoutMs);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}
type RecordingPage = {
  viewport: Point;
  windowBounds?: { left: number; top: number };
  markerClick: () => Promise<Point | undefined>;
  readViewport: () => Promise<Point>;
  close: () => void;
};
async function recordingPage(
  port: number,
  contentSize: Point,
  position?: Point,
): Promise<RecordingPage | undefined> {
  const deadline = Date.now() + 10_000;
  const cdpWaitMs = 2_000;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      const version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json() as {
        webSocketDebuggerUrl: string;
      };
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as Array<{
        id: string;
        type: string;
      }>;
      const target = targets.find((candidate) => candidate.type === "page");
      if (!target) throw new Error("no page target");
      const cdp = new Cdp(version.webSocketDebuggerUrl);
      await cdp.open(cdpWaitMs);
      const attached = await within(
        cdp.call<{ sessionId: string }>("Target.attachToTarget", {
          targetId: target.id,
          flatten: true,
        }),
        cdpWaitMs,
      );
      const window = await within(
        cdp.call<{ windowId: number }>("Browser.getWindowForTarget", { targetId: target.id }),
        cdpWaitMs,
      );
      const initialBounds = await within(
        cdp.call<{ bounds: { width?: number; height?: number } }>("Browser.getWindowBounds", {
          windowId: window.windowId,
        }),
        cdpWaitMs,
      );
      if (
        Number.isFinite(initialBounds.bounds.width) && Number.isFinite(initialBounds.bounds.height)
      ) {
        await within(
          cdp.call("Browser.setWindowBounds", {
            windowId: window.windowId,
            bounds: {
              windowState: "normal",
              width: initialBounds.bounds.width,
              height: initialBounds.bounds.height,
            },
          }),
          cdpWaitMs,
        );
      }
      if (position) {
        await within(
          cdp.call("Browser.setWindowBounds", {
            windowId: window.windowId,
            bounds: { left: position.x, top: position.y },
          }),
          cdpWaitMs,
        );
      }
      await within(
        cdp.call("Browser.setContentsSize", {
          windowId: window.windowId,
          width: contentSize.x,
          height: contentSize.y,
        }),
        cdpWaitMs,
      );
      await within(cdp.call("Page.enable", {}, attached.sessionId), cdpWaitMs);
      const readyDeadline = Date.now() + 10_000;
      while (true) {
        const ready = await within(
          cdp.call<{ result: { value?: string } }>("Runtime.evaluate", {
            expression: "document.readyState",
            returnByValue: true,
          }, attached.sessionId),
          cdpWaitMs,
        );
        if (ready.result.value !== "loading") break;
        if (Date.now() >= readyDeadline) throw new Error("CfT page did not finish loading");
        await sleep(50);
      }
      const readViewport = async () => {
        const metrics = await within(
          cdp.call<
            { cssVisualViewport?: { clientWidth: number; clientHeight: number } }
          >(
            "Page.getLayoutMetrics",
            {},
            attached.sessionId,
          ),
          cdpWaitMs,
        );
        const viewport = metrics.cssVisualViewport;
        if (!viewport || viewport.clientWidth <= 0 || viewport.clientHeight <= 0) {
          throw new Error("CfT recording viewport was unavailable");
        }
        return { x: viewport.clientWidth, y: viewport.clientHeight };
      };
      const waitForStableViewport = async () => {
        const deadline = Date.now() + 10_000;
        let previous = await readViewport();
        let stableSince = Date.now();
        while (Date.now() < deadline) {
          await sleep(100);
          const current = await readViewport();
          if (current.x === previous.x && current.y === previous.y) {
            if (Date.now() - stableSince >= 1_000) return current;
          } else {
            previous = current;
            stableSince = Date.now();
          }
        }
        throw new Error("CfT recording viewport did not stabilize before calibration");
      };
      // Sites can add scrollbars after document.readyState becomes complete. Wait for that initial
      // layout to settle, then apply the requested viewport and only then expose the marker.
      await waitForStableViewport();
      let viewport: Point | undefined;
      for (let attempt = 0; attempt < 10; attempt++) {
        await within(
          cdp.call("Browser.setContentsSize", {
            windowId: window.windowId,
            width: contentSize.x,
            height: contentSize.y,
          }),
          cdpWaitMs,
        );
        await sleep(100);
        const actual = await readViewport();
        // setContentsSize controls the host area. A page scrollbar can make its CSS viewport
        // smaller; that effective viewport is the coordinate space we must record.
        viewport = actual;
        if (actual.x === contentSize.x && actual.y === contentSize.y) break;
      }
      if (!viewport) {
        throw new Error("CfT recording viewport was unavailable after setting the content size");
      }
      if (viewport.x !== contentSize.x || viewport.y !== contentSize.y) {
        console.error(
          `Note: page scrollbar reduced the CSS viewport to ${viewport.x}x${viewport.y}; recording that effective coordinate space.`,
        );
      }
      const currentBounds = await within(
        cdp.call<{ bounds: { left?: number; top?: number } }>("Browser.getWindowBounds", {
          windowId: window.windowId,
        }),
        cdpWaitMs,
      );
      const windowBounds = Number.isFinite(currentBounds.bounds.left)
          && Number.isFinite(currentBounds.bounds.top)
        ? { left: currentBounds.bounds.left!, top: currentBounds.bounds.top! }
        : undefined;
      await within(
        cdp.call("Runtime.evaluate", {
          expression: `(() => {
            const id = "__crer_record_calibration_marker__";
            document.getElementById(id)?.remove();
            globalThis.__crerCalibrationPoint = undefined;
            const marker = document.createElement("div");
            marker.id = id;
            marker.setAttribute("aria-hidden", "true");
            marker.style.cssText = "all:initial;display:block!important;position:fixed!important;left:0!important;top:0!important;width:64px!important;height:64px!important;margin:0!important;padding:0!important;border:0!important;background:#ff00ff!important;z-index:2147483647!important;pointer-events:auto!important;cursor:crosshair!important;";
            marker.addEventListener("pointerup", (event) => {
              globalThis.__crerCalibrationPoint = { x: event.clientX, y: event.clientY };
              marker.remove();
            }, { once: true });
            document.documentElement.append(marker);
          })()`,
          returnByValue: true,
        }, attached.sessionId),
        cdpWaitMs,
      );
      return {
        viewport,
        windowBounds,
        markerClick: async () => {
          const result = await within(
            cdp.call<{
              result: { value?: Point };
            }>("Runtime.evaluate", {
              expression: "globalThis.__crerCalibrationPoint",
              returnByValue: true,
            }, attached.sessionId),
            cdpWaitMs,
          );
          const point = result.result.value;
          return point && Number.isFinite(point.x) && Number.isFinite(point.y) ? point : undefined;
        },
        readViewport,
        close: () => cdp.close(),
      };
    } catch (error) {
      lastError = error;
      await sleep(50);
    }
  }
  if (lastError) console.error(`Warning: recording page setup failed: ${lastError}`);
  return undefined;
}
async function closeRecordingBrowser(port: number, chrome: Deno.ChildProcess): Promise<void> {
  let cdp: Cdp | undefined;
  try {
    const version = await within(
      (async () => await (await fetch(`http://127.0.0.1:${port}/json/version`)).json())(),
      500,
    ) as { webSocketDebuggerUrl: string };
    cdp = new Cdp(version.webSocketDebuggerUrl);
    await cdp.open(500);
    await within(cdp.call("Browser.close"), 1_000);
  } catch {
    try {
      chrome.kill("SIGTERM");
    } catch {
      // Chrome may already be closed by the user.
    }
  } finally {
    cdp?.close();
  }
}
const pointOption = (name: string): Point | undefined => {
  const value = option(name);
  if (!value) return undefined;
  const [x, y] = value.split(",").map(Number);
  if (!Number.isFinite(x) || !Number.isFinite(y)) throw new Error(`${name} must be x,y`);
  return { x, y };
};
const contentSizeOption = (): Point => {
  const size = pointOption("--content-size") ?? { x: 860, y: 560 };
  if (!Number.isInteger(size.x) || !Number.isInteger(size.y) || size.x < 32 || size.y < 32) {
    throw new Error("--content-size must be integer width,height with both values at least 32");
  }
  return size;
};
const positionOption = (): Point | undefined => {
  const position = pointOption("--position");
  if (
    position
    && (!Number.isInteger(position.x) || !Number.isInteger(position.y))
  ) {
    throw new Error("--position must be integer left,top");
  }
  return position;
};
const profileDirOption = () => option("--profile-dir");
async function runNode(
  node: PlanNode,
  base: string,
  maxParallel: number,
  workerMs?: number,
  onFailure?: Record<string, FailurePolicy | undefined>,
  ignoreViewportMismatch = false,
  profileDir?: string,
): Promise<RunResult[]> {
  if ("scenario" in node) {
    const controller = new AbortController();
    let timedOut = false;
    const timer = workerMs && workerMs > 0
      ? setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, workerMs)
      : undefined;
    try {
      const scenarioFile = `${base}/${node.scenario}`;
      const scenario = scenarioFrom(await loadYaml(scenarioFile));
      if (maxParallel > 1 && (profileDir || scenario.browser.profile?.startsWith("persistent:"))) {
        throw new Error("persistent profile cannot be used with run max_parallel greater than 1");
      }
      const result = await playScenario(scenario, {
        chromePath: chromePath(),
        inputDllPath: inputDllPath(),
        signal: controller.signal,
        ignoreViewportMismatch,
        profileDir,
        templateBaseDir: scenarioFile.replace(/[\\/][^\\/]+$/, ""),
      });
      return timedOut
        ? [{ ...result, code: 4, failures: [...result.failures, "plan:timeout:worker_ms"] }]
        : [result];
    } finally {
      if (timer) clearTimeout(timer);
    }
  }
  if ("serial" in node) {
    const out: RunResult[] = [];
    for (const child of node.serial) {
      const results = await runNode(
        child,
        base,
        maxParallel,
        workerMs,
        onFailure,
        ignoreViewportMismatch,
        profileDir,
      );
      out.push(...results);
      if (shouldAbortPlan(results, onFailure)) break;
    }
    return out;
  }
  const results = await mapWithConcurrency(
    node.parallel.jobs,
    maxParallel,
    (child) =>
      runNode(child, base, maxParallel, workerMs, onFailure, ignoreViewportMismatch, profileDir),
    (result) =>
      node.parallel.fail_fast
        ? result.some((run) => run.code !== 0)
        : shouldAbortPlan(result, onFailure),
  );
  return results.flat();
}
async function main() {
  if (!command || command === "help") {
    console.log("crer <doctor|inspect|validate|play|run|record|normalize> <file> [options]");
    return;
  }
  if (command === "doctor") {
    const configured = configuredChromePath();
    const ffi = inputDllPath();
    const chrome = await chromeDiagnostic(configured);
    console.log(
      JSON.stringify(
        {
          deno: Deno.version.deno,
          os: Deno.build,
          chrome,
          ffi,
          ffiExists: await Deno.stat(ffi).then((info) => info.isFile).catch(() => false),
        },
        null,
        2,
      ),
    );
    return;
  }
  if (command === "inspect") {
    if (!file) throw new Error("artifact directory is required");
    console.log(JSON.stringify(await inspectRun(file), null, 2));
    return;
  }
  if (!file) throw new Error("scenario or plan path is required");
  if (command === "validate") {
    const v = await loadYaml(file);
    try {
      scenarioFrom(v);
    } catch {
      planFrom(v);
    }
    console.log(`${file}: valid`);
    return;
  }
  if (command === "play") {
    const stepDelay = option("--step-delay-ms");
    const stepDelayMs = stepDelay === undefined ? undefined : Number(stepDelay);
    if (stepDelayMs !== undefined && (!Number.isFinite(stepDelayMs) || stepDelayMs < 0)) {
      throw new Error("--step-delay-ms must be a non-negative number");
    }
    const r = await playScenario(scenarioFrom(await loadYaml(file)), {
      chromePath: chromePath(),
      inputDllPath: inputDllPath(),
      seed: option("--seed"),
      keepArtifacts: args.includes("--keep-artifacts"),
      stepDelayMs,
      ignoreViewportMismatch: args.includes("--ignore-viewport-mismatch"),
      profileDir: profileDirOption(),
      templateBaseDir: file.replace(/[\\/][^\\/]+$/, ""),
    });
    console.log(JSON.stringify(r, null, 2));
    Deno.exitCode = r.code;
    return;
  }
  if (command === "run") {
    const p = planFrom(await loadYaml(file));
    const profileDir = profileDirOption();
    if (profileDir && (p.max_parallel ?? 1) > 1) {
      throw new Error("--profile-dir cannot be used with run max_parallel greater than 1");
    }
    const results = await runNode(
      p.run,
      file.replace(/[\\/][^\\/]+$/, ""),
      p.max_parallel ?? 1,
      p.timeouts?.worker_ms,
      p.on_failure,
      args.includes("--ignore-viewport-mismatch"),
      profileDir,
    );
    const code = aggregatePlanExitCode(results);
    console.log(JSON.stringify(results, null, 2));
    Deno.exitCode = code;
    return;
  }
  if (command === "record") {
    const runDir = `.crer/runs/${crypto.randomUUID()}`;
    await Deno.mkdir(runDir, { recursive: true });
    const configuredProfile = profileDirOption();
    const profile = configuredProfile
      ? await persistentProfileDirectory(configuredProfile)
      : `${await Deno.realPath(runDir)}/profile`;
    const url = option("--url") ?? "about:blank";
    const contentSize = contentSizeOption();
    const position = positionOption();
    const reservation = Deno.listen({ hostname: "127.0.0.1", port: 0 });
    const port = (reservation.addr as Deno.NetAddr).port;
    reservation.close();
    await Deno.mkdir(`${profile}/Default`, { recursive: true });
    const preferences = `${profile}/Default/Preferences`;
    if (!await Deno.stat(preferences).then(() => true).catch(() => false)) {
      await Deno.writeTextFile(preferences, JSON.stringify({ translate: { enabled: false } }));
    }
    const chrome = new Deno.Command(chromePath(), {
      args: [
        `--remote-debugging-port=${port}`,
        "--remote-debugging-address=127.0.0.1",
        `--user-data-dir=${profile}`,
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-sync",
        "--disable-infobars",
        "--force-device-scale-factor=1",
        "--disable-features=Translate,TranslateUI",
        `--app=${url}`,
      ],
      stdout: "null",
      stderr: "null",
    }).spawn();
    const controller = new AbortController();
    const onInterrupt = () => controller.abort();
    Deno.addSignalListener("SIGINT", onInterrupt);
    const duration = option("--duration-ms");
    const timer = duration ? setTimeout(() => controller.abort(), Number(duration)) : undefined;
    const stopFile = option("--stop-file");
    const stopFileTimer = stopFile
      ? setInterval(async () => {
        try {
          await Deno.stat(stopFile);
          controller.abort();
        } catch (error) {
          if (!(error instanceof Deno.errors.NotFound)) {
            console.error(`Warning: could not check stop file: ${error}`);
          }
        }
      }, 100)
      : undefined;
    // The fixture script owns its terminal and uses --stop-file. A direct interactive invocation
    // can instead finish naturally with Enter, without requiring a second shell or Ctrl+C.
    const cancelTerminalEnter = !stopFile && duration === undefined && Deno.stdin.isTerminal()
      ? stopOnTerminalEnter(controller)
      : undefined;
    if (cancelTerminalEnter) console.error("Recording. Press Enter or Ctrl+C to stop.");
    let page: RecordingPage | undefined;
    try {
      page = await recordingPage(port, contentSize, position);
      const useMarkerCalibration = duration === undefined;
      if (useMarkerCalibration && !page) {
        throw new Error("could not inject the recording calibration marker into the CfT page");
      }
      await recordRaw(
        inputDllPath(),
        chrome.pid,
        file,
        controller.signal,
        page?.viewport,
        useMarkerCalibration ? page?.markerClick : undefined,
        page?.readViewport,
        page?.windowBounds,
        contentSize,
        configuredProfile ? profile : undefined,
      );
    } finally {
      Deno.removeSignalListener("SIGINT", onInterrupt);
      if (timer) clearTimeout(timer);
      if (stopFileTimer) clearInterval(stopFileTimer);
      cancelTerminalEnter?.();
      page?.close();
      await closeRecordingBrowser(port, chrome);
    }
    return;
  }
  if (command === "normalize") {
    const output = option("--output");
    const url = option("--url");
    if (!output || !url) {
      throw new Error("normalize requires --output <scenario.crer.yaml> and --url <URL>");
    }
    const origin = pointOption("--client-origin");
    const clientSize = pointOption("--client-size");
    const viewport = pointOption("--viewport");
    if ((origin || clientSize || viewport) && !(origin && clientSize && viewport)) {
      throw new Error(
        "coordinate conversion requires --client-origin, --client-size, and --viewport",
      );
    }
    const sidecarTransform = origin ? undefined : await transformFromSidecar(file);
    const qpcFrequencyHz = origin ? undefined : await qpcFrequencyFromSidecar(file);
    const windowBounds = origin ? undefined : await windowBoundsFromSidecar(file);
    const requestedContent = origin ? undefined : await requestedContentFromSidecar(file);
    const profileDir = origin ? undefined : await profileDirFromSidecar(file);
    if (!origin && !sidecarTransform) {
      console.error(
        "Warning: recording metadata is unavailable; output coordinates remain physical screen pixels.",
      );
    }
    const normalized = await normalizeRawWithWarnings(
      file,
      url,
      option("--name") ?? "recorded-scenario",
      origin
        ? { clientOrigin: origin, clientSize: clientSize!, viewport: viewport! }
        : sidecarTransform,
      qpcFrequencyHz,
      requestedContent,
      profileDir,
    );
    if (windowBounds) {
      normalized.scenario.browser.window = {
        ...(normalized.scenario.browser.window ?? {}),
        bounds: windowBounds,
      };
    }
    await saveYaml(output, normalized.scenario);
    for (const warning of normalized.warnings) console.error(`Warning: ${warning}`);
    console.log(`Wrote ${output}`);
    return;
  }
  throw new Error(`unknown command: ${command}`);
}
await main();
