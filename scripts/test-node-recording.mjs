import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";

const { values } = parseArgs({
  options: {
    exe: { type: "string", default: "dist/win-x64/crer.exe" },
    chrome: { type: "string" },
    output: { type: "string", default: ".crer/nodejs-migration/recording" },
  },
});
const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const executable = resolve(root, values.exe);
const chrome = values.chrome ?? process.env.CRER_TEST_CHROME ?? process.env.CRER_CHROME;
const runId = `${new Date().toISOString().replaceAll(/[:.]/g, "-")}-${randomUUID().slice(0, 8)}`;
const directory = resolve(root, values.output, runId);
const evidencePath = resolve(directory, "evidence.json");
const evidence = {
  startedAt: new Date().toISOString(),
  node: process.versions.node,
  executable,
  chrome,
  directory,
  checks: [],
};
const sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

async function exists(path) {
  try {
    return (await stat(path)).isFile();
  } catch (error) {
    if (error.code === "ENOENT") return false;
    throw error;
  }
}

async function waitFor(predicate, timeoutMs, description) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await sleep(50);
  }
  throw new Error(`Timed out waiting for ${description}`);
}

function isRunning(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error.code === "ESRCH") return false;
    throw error;
  }
}

async function command(name, args, onRunning) {
  const entry = { name, args, startedAt: new Date().toISOString(), passed: false };
  evidence.checks.push(entry);
  const started = Date.now();
  const child = spawn(executable, args, {
    cwd: root,
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });
  const state = { exited: false, stdout: "", stderr: "" };
  child.stdout.setEncoding("utf8").on("data", (chunk) => state.stdout += chunk);
  child.stderr.setEncoding("utf8").on("data", (chunk) => state.stderr += chunk);
  const completed = new Promise((resolve, reject) => {
    child.once("error", reject);
    child.once("close", (code, signal) => {
      state.exited = true;
      resolve({ code, signal });
    });
  });
  let timedOut = false;
  // タイムアウト時に停止するのは、この検証が直接起動した CLI プロセスだけに限定する。
  const deadline = setTimeout(() => {
    timedOut = true;
    child.kill("SIGKILL");
  }, 20_000);
  let progressError;
  const progress = onRunning?.(state).catch((error) => {
    progressError = error;
    child.kill("SIGKILL");
  });
  try {
    const [result] = await Promise.all([completed, progress]);
    Object.assign(entry, result, { timedOut, elapsedMs: Date.now() - started });
    if (progressError) throw progressError;
    assert.equal(timedOut, false, `${name}: CLI exceeded the 20 second deadline`);
    return { entry, ...result, ...state };
  } finally {
    clearTimeout(deadline);
    entry.childPid = child.pid;
    entry.elapsedMs = Date.now() - started;
    entry.timedOut = timedOut;
    entry.chromePids = [...state.stderr.matchAll(/Recording target Chrome process: (\d+)/g)]
      .map((match) => Number(match[1]));
    entry.stdoutPath = resolve(directory, `${name}.stdout.txt`);
    entry.stderrPath = resolve(directory, `${name}.stderr.txt`);
    await writeFile(entry.stdoutPath, state.stdout);
    await writeFile(entry.stderrPath, state.stderr);
  }
}

async function recording(name, duration, useStopFile = false) {
  const raw = resolve(directory, `${name}.ndjson`);
  const stopFile = resolve(directory, `${name}.stop`);
  const args = [
    "record",
    raw,
    "--chrome",
    chrome,
    "--url",
    "data:text/html,<title>CRER Node recording test</title><style>body{margin:0;overflow:hidden}</style><p>Recording lifecycle verification</p>",
    "--content-size",
    "600,400",
    "--duration-ms",
    String(duration),
    "--name",
    `node-record-${name}-${runId}`,
    ...(useStopFile ? ["--stop-file", stopFile] : []),
  ];
  const result = await command(
    name,
    args,
    useStopFile
      ? async (state) => {
        // メタデータは停止後に書かれるため、録画用 NDJSON の作成を開始の合図とする。
        await waitFor(
          async () => {
            assert.equal(state.exited, false, "recording exited before the stop file was created");
            return /Recording target Chrome process: \d+/.test(state.stderr) && await exists(raw);
          },
          15_000,
          "recording output creation",
        );
        await writeFile(stopFile, "stop\n");
      }
      : undefined,
  );
  assert.equal(result.code, 0, result.stderr);
  const text = await readFile(raw, "utf8");
  const events = text.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line));
  const metadata = JSON.parse(await readFile(`${raw}.meta.json`, "utf8"));
  assert.match(metadata.qpc_frequency_hz, /^\d+$/);
  assert(BigInt(metadata.qpc_frequency_hz) > 0n);
  assert.deepEqual(metadata.css_viewport, { x: 600, y: 400 });
  assert.deepEqual(metadata.requested_content, { width: 600, height: 400 });
  assert.notEqual(metadata.incomplete, true);
  const chromePids = [...result.stderr.matchAll(/Recording target Chrome process: (\d+)/g)]
    .map((match) => Number(match[1]));
  assert.equal(chromePids.length, 1, "recording must identify exactly one isolated Chrome process");
  await waitFor(() => !isRunning(chromePids[0]), 8_000, "recording Chrome exit");
  if (useStopFile) {
    assert(result.entry.elapsedMs < 20_000, "stop file must stop before the 60s duration");
  }
  Object.assign(result.entry, {
    passed: true,
    raw,
    metadataPath: `${raw}.meta.json`,
    eventCount: events.length,
    chromePids,
    chromeExited: true,
    ...(useStopFile ? { stopFile } : {}),
  });
  console.log(`PASS: ${name} (${result.entry.elapsedMs}ms, ${events.length} recorded events)`);
}

async function invalidDuration(duration) {
  const result = await command(`invalid-duration-${duration}`, [
    "record",
    resolve(directory, "invalid.ndjson"),
    "--chrome",
    resolve(directory, "does-not-exist.exe"),
    "--duration-ms",
    duration,
  ]);
  assert.equal(result.code, 2, result.stderr);
  assert.match(result.stderr, /--duration-ms/);
  assert.doesNotMatch(result.stderr, /Recording target Chrome process:/);
  result.entry.passed = true;
  console.log(`PASS: invalid duration ${duration} rejected before Chrome startup`);
}

async function normalizeAndValidate() {
  const raw = resolve(directory, "representative.ndjson");
  const yaml = resolve(directory, "representative.crer.yaml");
  const events = [
    { qpc: "0", kind: 2, data: 0, x: 20, y: 20 },
    { qpc: "50000", kind: 3, data: 0, x: 20, y: 20 },
    { qpc: "100000", kind: 9, data: 97, x: 20, y: 20 },
    { qpc: "150000", kind: 7, data: 9 << 16, x: 20, y: 20 },
    { qpc: "160000", kind: 8, data: 9 << 16, x: 20, y: 20 },
  ];
  await writeFile(raw, events.map((event) => JSON.stringify(event)).join("\n") + "\n");
  await writeFile(
    `${raw}.meta.json`,
    JSON.stringify({
      qpc_frequency_hz: "1000000",
      content_rect_screen_px: { x: 0, y: 0, width: 600, height: 400 },
      css_viewport: { x: 600, y: 400 },
      screen_pixels_per_css_pixel: { x: 1, y: 1 },
    }),
  );
  const normalized = await command("normalize", [
    "normalize",
    raw,
    "--output",
    yaml,
    "--url",
    "http://127.0.0.1:1/",
    "--name",
    "recording-normalize",
  ]);
  assert.equal(normalized.code, 0, normalized.stderr);
  const text = await readFile(yaml, "utf8");
  for (const operation of ["click", "text", "key"]) {
    assert.match(text, new RegExp(`do: ${operation}`));
  }
  Object.assign(normalized.entry, { passed: true, raw, yaml });
  const validated = await command("validate", ["validate", yaml]);
  assert.equal(validated.code, 0, validated.stderr);
  validated.entry.passed = true;
  console.log("PASS: representative NDJSON normalized and validated through the built CLI");
}

await mkdir(directory, { recursive: true });
try {
  assert.equal(process.platform, "win32", "recording verification requires Windows");
  assert(chrome, "Set CRER_TEST_CHROME or pass --chrome with Chrome for Testing");
  assert(await exists(executable), `Built executable not found: ${executable}`);
  assert(await exists(chrome), `Chrome for Testing not found: ${chrome}`);
  await recording("duration-zero", 0);
  await recording("duration-500", 500);
  await recording("stop-file", 60_000, true);
  await invalidDuration("NaN");
  await invalidDuration("-1");
  await normalizeAndValidate();
  evidence.passed = true;
} catch (error) {
  evidence.passed = false;
  evidence.error = error.stack ?? String(error);
  console.error(evidence.error);
  process.exitCode = 1;
} finally {
  evidence.finishedAt = new Date().toISOString();
  await writeFile(evidencePath, JSON.stringify(evidence, null, 2) + "\n");
  console.log(`Evidence: ${evidencePath}`);
}
