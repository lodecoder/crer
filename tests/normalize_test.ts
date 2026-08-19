import { assertEquals } from "jsr:@std/assert@^1.0.14";
import { normalizeRaw } from "../src/normalize.ts";

Deno.test("normalizes mouse, wheel, and key events into steps", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(
    path,
    [
      { qpc: "1", x: 100, y: 200, kind: 2, data: 0 },
      { qpc: "2", x: 100, y: 200, kind: 3, data: 0 },
      { qpc: "3", x: 120, y: 220, kind: 6, data: 120 },
      { qpc: "4", x: 120, y: 220, kind: 7, data: 13 << 16 },
    ].map((event) => JSON.stringify(event)).join("\n"),
  );
  try {
    const scenario = await normalizeRaw(path, "https://example.test", "sample");
    assertEquals(scenario.steps, [
      { do: "click", at: { x: 100, y: 200 } },
      { do: "scroll", at: { x: 120, y: 220 }, delta: { x: 0, y: -120 } },
      { do: "key", key: "Enter" },
    ]);
  } finally {
    await Deno.remove(path);
  }
});
