import { assertEquals } from "@std/assert";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { RunArtifactSink } from "../src/artifacts.ts";

test("artifact sink preserves one JSON object per step line", async () => {
  const directory = await mkdtemp(join(tmpdir(), "crer-test-"));
  try {
    const sink = new RunArtifactSink(directory);
    await sink.appendStep({ index: "0", status: "ok" });
    await sink.appendStep({ index: "1", status: "failed" });
    assertEquals(
      (await readFile(`${directory}/steps.ndjson`, "utf8")).trim().split("\n").map((line) =>
        JSON.parse(line)
      ),
      [{ index: "0", status: "ok" }, { index: "1", status: "failed" }],
    );
  } finally {
    await rm(directory, { recursive: true });
  }
});
