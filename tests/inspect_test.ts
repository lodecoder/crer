import { assertEquals } from "jsr:@std/assert";
import { inspectRun } from "../src/inspect.ts";

Deno.test("inspects run artifacts without exposing profile contents", async () => {
  const dir = await Deno.makeTempDir();
  try {
    await Deno.writeTextFile(`${dir}/run.json`, JSON.stringify({ seed: "42" }));
    await Deno.writeTextFile(`${dir}/foreground.json`, JSON.stringify({ restoreStatus: 0 }));
    await Deno.writeTextFile(`${dir}/steps.ndjson`, '{"status":"ok"}\n{"status":"failed"}\n');
    await Deno.writeFile(`${dir}/result.png`, new Uint8Array([1, 2, 3]));
    await Deno.mkdir(`${dir}/profile`);
    const result = await inspectRun(dir);
    assertEquals(result.run, { seed: "42" });
    assertEquals(result.foreground, { restoreStatus: 0 });
    assertEquals(result.screenshots, ["result.png"]);
    assertEquals(result.steps, { count: 2, failed: 1 });
    assertEquals(result.files.map((file) => file.name), [
      "foreground.json",
      "result.png",
      "run.json",
      "steps.ndjson",
    ]);
  } finally {
    await Deno.remove(dir, { recursive: true });
  }
});
