import { assertEquals } from "@std/assert";
import { RunArtifactSink } from "../src/artifacts.ts";

Deno.test("artifact sink preserves one JSON object per step line", async () => {
  const directory = await Deno.makeTempDir();
  try {
    const sink = new RunArtifactSink(directory);
    await sink.appendStep({ index: "0", status: "ok" });
    await sink.appendStep({ index: "1", status: "failed" });
    assertEquals(
      (await Deno.readTextFile(`${directory}/steps.ndjson`)).trim().split("\n").map((line) =>
        JSON.parse(line)
      ),
      [{ index: "0", status: "ok" }, { index: "1", status: "failed" }],
    );
  } finally {
    await Deno.remove(directory, { recursive: true });
  }
});
