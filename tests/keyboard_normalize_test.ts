import { assertEquals } from "@std/assert";
import { normalizeRaw } from "../src/normalize.ts";

Deno.test("recorded Unicode and virtual-key spaces preserve key activation and delays", async () => {
  const path = await Deno.makeTempFile();
  try {
    for (const kind of [9, 7]) {
      await Deno.writeTextFile(
        path,
        [
          { kind: 9, data: 97 },
          { kind, data: kind === 9 ? 32 : 32 << 16 },
          { kind: 8, data: 32 << 16 },
          { kind: 9, data: 98 },
          { kind: 7, data: 9 << 16 },
          { kind: 8, data: 9 << 16 },
          { kind, data: kind === 9 ? 32 : 32 << 16 },
          { kind: 8, data: 32 << 16 },
        ].map((event, index) => JSON.stringify({ ...event, qpc: String(index + 1), x: 0, y: 0 }))
          .join("\n"),
      );
      const scenario = await normalizeRaw(
        path,
        "https://example.test",
        "keyboard",
        undefined,
        1000n,
      );
      assertEquals(scenario.steps, [
        { do: "text", value: "a", delay_ms: 1 },
        { do: "key", key: "Space", delay_ms: 2 },
        { do: "text", value: "b", delay_ms: 1 },
        { do: "key", key: "Tab", delay_ms: 2 },
        { do: "key", key: "Space" },
      ]);
    }
  } finally {
    await Deno.remove(path);
  }
});
