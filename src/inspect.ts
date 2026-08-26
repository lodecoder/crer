type Json = Record<string, unknown>;

async function readJson(path: string): Promise<Json | undefined> {
  try {
    const value = JSON.parse(await Deno.readTextFile(path));
    return value && typeof value === "object" && !Array.isArray(value) ? value as Json : undefined;
  } catch {
    return undefined;
  }
}

async function stepSummary(path: string) {
  try {
    const entries = (await Deno.readTextFile(path)).trim().split("\n").filter(Boolean).map((line) =>
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
  const stat = await Deno.stat(runDir);
  if (!stat.isDirectory) throw new Error(`artifact directory is not a directory: ${runDir}`);
  const files: Array<{ name: string; bytes: number }> = [];
  for await (const entry of Deno.readDir(runDir)) {
    if (!entry.isFile) continue;
    const info = await Deno.stat(`${runDir}/${entry.name}`);
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
