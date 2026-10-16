import { readdir, readFile, stat as fsStat } from "node:fs/promises";

type Json = Record<string, unknown>;

async function readJson(path: string): Promise<Json | undefined> {
  try {
    const value = JSON.parse(await readFile(path, "utf8"));
    return value && typeof value === "object" && !Array.isArray(value) ? value as Json : undefined;
  } catch {
    return undefined;
  }
}

async function stepSummary(path: string) {
  try {
    const entries = (await readFile(path, "utf8")).trim().split("\n").filter(Boolean).map((line) =>
      JSON.parse(line) as Json
    );
    return {
      count: entries.length,
      failed: entries.filter((entry) => entry.status === "failed").length,
    };
  } catch {
    return undefined;
  }
}

export async function inspectRun(runDir: string) {
  const stat = await fsStat(runDir);
  if (!stat.isDirectory()) throw new Error(`artifact directory is not a directory: ${runDir}`);
  const files: Array<{ name: string; bytes: number }> = [];
  for (const entry of await readdir(runDir, { withFileTypes: true })) {
    if (!entry.isFile()) continue;
    const info = await fsStat(`${runDir}/${entry.name}`);
    files.push({ name: entry.name, bytes: info.size });
  }
  files.sort((a, b) => a.name.localeCompare(b.name));
  const screenshot = files.filter((file) => file.name.endsWith(".png")).map((file) => file.name);
  return {
    runDir,
    run: await readJson(`${runDir}/run.json`),
    display: await readJson(`${runDir}/display.json`),
    foreground: await readJson(`${runDir}/foreground.json`),
    launch: await readJson(`${runDir}/launch.json`),
    steps: await stepSummary(`${runDir}/steps.ndjson`),
    screenshots: screenshot,
    files,
  };
}
