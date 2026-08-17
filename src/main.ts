import { playScenario } from "./runtime.ts";
import type { PlanNode, RunResult } from "./types.ts";
import { loadYaml, planFrom, scenarioFrom } from "./yaml.ts";
const [command, file, ...args] = Deno.args;
const option = (name: string) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const chromePath = () => option("--chrome") ?? Deno.env.get("CRER_CHROME") ?? "chrome.exe";
async function runNode(node: PlanNode, base: string): Promise<RunResult[]> {
  if ("scenario" in node) {
    return [
      await playScenario(scenarioFrom(await loadYaml(`${base}/${node.scenario}`)), {
        chromePath: chromePath(),
      }),
    ];
  }
  if ("serial" in node) {
    const out: RunResult[] = [];
    for (const child of node.serial) out.push(...await runNode(child, base));
    return out;
  }
  const results = await Promise.all(node.parallel.jobs.map((x) => runNode(x, base)));
  return results.flat();
}
async function main() {
  if (!command || command === "help") {
    console.log("crer <doctor|validate|play|run|record> <file> [--chrome PATH] [--seed UINT64]");
    return;
  }
  if (command === "doctor") {
    console.log(
      JSON.stringify(
        {
          deno: Deno.version.deno,
          os: Deno.build,
          chrome: chromePath(),
          ffi: "native/crer-win-input.dll",
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
    const r = await playScenario(scenarioFrom(await loadYaml(file)), {
      chromePath: chromePath(),
      seed: option("--seed"),
      keepArtifacts: args.includes("--keep-artifacts"),
    });
    console.log(JSON.stringify(r, null, 2));
    Deno.exitCode = r.code;
    return;
  }
  if (command === "run") {
    const p = planFrom(await loadYaml(file));
    const results = await runNode(p.run, file.replace(/[\\/][^\\/]+$/, ""));
    const code = results.some((r) => r.code === 3) ? 3 : results.some((r) => r.code !== 0) ? 4 : 0;
    console.log(JSON.stringify(results, null, 2));
    Deno.exitCode = code;
    return;
  }
  if (command === "record") {
    throw new Error(
      "record requires crer-win-input.dll; build native/ then invoke the packaged binary (playback MVP is available now)",
    );
  }
  throw new Error(`unknown command: ${command}`);
}
await main();