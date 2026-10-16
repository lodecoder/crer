import { assertEquals } from "@std/assert";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { inspectRun } from "../src/inspect.ts";

test("inspects run artifacts without exposing profile contents", async () => {
  const dir = await mkdtemp(join(tmpdir(), "crer-test-"));
  try {
    await writeFile(`${dir}/run.json`, JSON.stringify({ seed: "42" }));
    await writeFile(`${dir}/foreground.json`, JSON.stringify({ restoreStatus: 0 }));
    await writeFile(`${dir}/steps.ndjson`, "{\"status\":\"ok\"}\n{\"status\":\"failed\"}\n");
    await writeFile(`${dir}/result.png`, new Uint8Array([1, 2, 3]));
    await mkdir(`${dir}/profile`);
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
    await rm(dir, { recursive: true });
  }
});
