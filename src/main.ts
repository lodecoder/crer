import { Cdp } from "./cdp.ts";
import {
  normalizeRawWithWarnings,
  qpcFrequencyFromSidecar,
  transformFromSidecar,
} from "./normalize.ts";
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
const inputDllPath = () =>
  option("--dll")
    ?? "native/bin/Release/net10.0/win-x64/publish/crer-win-input.dll";
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
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
  viewport?: Point;
  markerWasClicked: () => Promise<boolean>;
  close: () => void;
};
async function recordingPage(port: number): Promise<RecordingPage | undefined> {
  const deadline = Date.now() + 2_000;
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
      await cdp.open(250);
      const attached = await within(
        cdp.call<{ sessionId: string }>("Target.attachToTarget", {
          targetId: target.id,
          flatten: true,
        }),
        500,
      );
      const metrics = await within(
        cdp.call<
          { cssVisualViewport?: { clientWidth: number; clientHeight: number } }
        >(
          "Page.getLayoutMetrics",
          {},
          attached.sessionId,
        ),
        500,
      );
      const viewport = metrics.cssVisualViewport;
      await within(
        cdp.call("Runtime.evaluate", {
          expression: `(() => {
            const id = "__crer_record_calibration_marker__";
            document.getElementById(id)?.remove();
            globalThis.__crerCalibrationClicked = false;
            const marker = document.createElement("div");
            marker.id = id;
            marker.setAttribute("aria-hidden", "true");
            marker.style.cssText = "position:fixed;left:0;top:0;width:2px;height:2px;margin:0;padding:0;border:0;background:#ff00ff;z-index:2147483647;pointer-events:auto;";
            marker.addEventListener("pointerup", () => {
              globalThis.__crerCalibrationClicked = true;
              marker.remove();
            }, { once: true });
            document.documentElement.append(marker);
          })()`,
          returnByValue: true,
        }, attached.sessionId),
        500,
      );
      return {
        viewport: viewport ? { x: viewport.clientWidth, y: viewport.clientHeight } : undefined,
        markerWasClicked: async () => {
          const result = await within(
            cdp.call<{
              result: { value?: boolean };
            }>("Runtime.evaluate", {
              expression: "Boolean(globalThis.__crerCalibrationClicked)",
              returnByValue: true,
            }, attached.sessionId),
            500,
          );
          return result.result.value === true;
        },
        close: () => cdp.close(),
      };
    } catch {
      await sleep(50);
    }
  }
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
function shouldAbortPlan(
  results: RunResult[],
  onFailure?: Record<string, FailurePolicy | undefined>,
): boolean {
  if (!results.some((result) => result.code !== 0)) return false;
  const timeout = results.some((result) =>
    result.failures.some((failure) => failure.includes(":timeout:"))
  );
  const environment = results.some((result) => result.code === 3);
  const kind = timeout ? "timeout" : environment ? "environment" : "scenario_failure";
  return (onFailure?.[kind] ?? onFailure?.default ?? "abort") === "abort";
}
async function runNode(
  node: PlanNode,
  base: string,
  maxParallel: number,
  workerMs?: number,
  onFailure?: Record<string, FailurePolicy | undefined>,
): Promise<RunResult[]> {
  if ("scenario" in node) {
    const controller = new AbortController();
    const timer = workerMs ? setTimeout(() => controller.abort(), workerMs) : undefined;
    try {
      return [
        await playScenario(scenarioFrom(await loadYaml(`${base}/${node.scenario}`)), {
          chromePath: chromePath(),
          signal: controller.signal,
        }),
      ];
    } finally {
      if (timer) clearTimeout(timer);
    }
  }
  if ("serial" in node) {
    const out: RunResult[] = [];
    for (const child of node.serial) {
      const results = await runNode(child, base, maxParallel, workerMs, onFailure);
      out.push(...results);
      if (shouldAbortPlan(results, onFailure)) break;
    }
    return out;
  }
  const results = await mapWithConcurrency(
    node.parallel.jobs,
    maxParallel,
    (child) => runNode(child, base, maxParallel, workerMs, onFailure),
    (result) =>
      node.parallel.fail_fast
        ? result.some((run) => run.code !== 0)
        : shouldAbortPlan(result, onFailure),
  );
  return results.flat();
}
async function main() {
  if (!command || command === "help") {
    console.log("crer <doctor|validate|play|run|record|normalize> <file> [options]");
    return;
  }
  if (command === "doctor") {
    const configured = configuredChromePath();
    const ffi = inputDllPath();
    let chromeExists = false;
    if (configured) {
      try {
        chromeExists = (await Deno.stat(configured)).isFile;
      } catch {
        // Report the unavailable path below instead of failing the diagnostic command.
      }
    }
    console.log(
      JSON.stringify(
        {
          deno: Deno.version.deno,
          os: Deno.build,
          chrome: configured ?? "not configured",
          chromeExists,
          ffi,
          ffiExists: await Deno.stat(ffi).then((info) => info.isFile).catch(() => false),
        },
        null,
        2,
      ),
    );
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
      seed: option("--seed"),
      keepArtifacts: args.includes("--keep-artifacts"),
      stepDelayMs,
    });
    console.log(JSON.stringify(r, null, 2));
    Deno.exitCode = r.code;
    return;
  }
  if (command === "run") {
    const p = planFrom(await loadYaml(file));
    const results = await runNode(
      p.run,
      file.replace(/[\\/][^\\/]+$/, ""),
      p.max_parallel ?? 1,
      p.timeouts?.worker_ms,
      p.on_failure,
    );
    const code = results.some((r) => r.code === 3) ? 3 : results.some((r) => r.code !== 0) ? 4 : 0;
    console.log(JSON.stringify(results, null, 2));
    Deno.exitCode = code;
    return;
  }
  if (command === "record") {
    const runDir = `.crer/runs/${crypto.randomUUID()}`;
    await Deno.mkdir(runDir, { recursive: true });
    const profile = `${await Deno.realPath(runDir)}/profile`;
    const url = option("--url") ?? "about:blank";
    const reservation = Deno.listen({ hostname: "127.0.0.1", port: 0 });
    const port = (reservation.addr as Deno.NetAddr).port;
    reservation.close();
    await Deno.mkdir(`${profile}/Default`, { recursive: true });
    await Deno.writeTextFile(
      `${profile}/Default/Preferences`,
      JSON.stringify({ translate: { enabled: false } }),
    );
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
        "--window-size=900,700",
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
    let page: RecordingPage | undefined;
    try {
      page = await recordingPage(port);
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
        useMarkerCalibration ? page?.markerWasClicked : undefined,
      );
    } finally {
      Deno.removeSignalListener("SIGINT", onInterrupt);
      if (timer) clearTimeout(timer);
      if (stopFileTimer) clearInterval(stopFileTimer);
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
    );
    await saveYaml(output, normalized.scenario);
    for (const warning of normalized.warnings) console.error(`Warning: ${warning}`);
    console.log(`Wrote ${output}`);
    return;
  }
  throw new Error(`unknown command: ${command}`);
}
await main();
