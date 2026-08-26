import { assertEquals } from "jsr:@std/assert@^1.0.14";
import {
  normalizeRaw,
  normalizeRawWithWarnings,
  screenToCss,
  transformFromRecordingMetadata,
} from "../src/normalize.ts";

Deno.test("converts physical screen coordinates into CSS viewport coordinates", () => {
  assertEquals(
    screenToCss(
      { x: 1100, y: 700 },
      { clientOrigin: { x: 100, y: 100 }, clientSize: { x: 2000, y: 1200 }, viewport: { x: 1000, y: 600 } },
    ),
    { x: 500, y: 300 },
  );
});

Deno.test("uses complete recording metadata as a coordinate transform", () => {
  assertEquals(
    transformFromRecordingMetadata({
      content_rect_screen_px: { x: 100, y: 200, width: 2000, height: 1200 },
      css_viewport: { x: 1000, y: 600 },
    }),
    {
      clientOrigin: { x: 100, y: 200 },
      clientSize: { x: 2000, y: 1200 },
      viewport: { x: 1000, y: 600 },
    },
  );
  assertEquals(transformFromRecordingMetadata({ css_viewport: { x: 1000, y: 600 } }), undefined);
  assertEquals(
    transformFromRecordingMetadata({
      content_rect_screen_px: { x: 100, y: 200, width: 1, height: 1 },
      css_viewport: { x: 1000, y: 600 },
    }),
    undefined,
  );
});

Deno.test("calibrates the page origin from the recording marker", () => {
  assertEquals(
    transformFromRecordingMetadata({
      content_rect_screen_px: { x: 0, y: 16, width: 442, height: 331 },
      css_viewport: { x: 884, y: 661 },
      marker_calibration: {
        screenClick: { x: 86, y: 35 },
        cssPoint: { x: 3, y: 2 },
      },
    }),
    {
      clientOrigin: { x: 84.5, y: 33.998487140695915 },
      clientSize: { x: 442, y: 331 },
      viewport: { x: 884, y: 661 },
    },
  );
});

Deno.test("normalizes mouse, wheel, and key events into steps", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(
    path,
    [
      { qpc: "1", x: 100, y: 200, kind: 2, data: 0 },
      { qpc: "2", x: 100, y: 200, kind: 3, data: 0 },
      { qpc: "3", x: 120, y: 220, kind: 6, data: 120 },
      { qpc: "4", x: 120, y: 220, kind: 7, data: 65 << 16 },
      { qpc: "5", x: 120, y: 220, kind: 8, data: 65 << 16 },
      { qpc: "6", x: 120, y: 220, kind: 7, data: 66 << 16 },
      { qpc: "7", x: 120, y: 220, kind: 8, data: 66 << 16 },
      { qpc: "8", x: 120, y: 220, kind: 7, data: 13 << 16 },
    ].map((event) => JSON.stringify(event)).join("\n"),
  );
  try {
    const scenario = await normalizeRaw(path, "https://example.test", "sample");
    assertEquals(scenario.steps, [
      { do: "click", at: { x: 100, y: 200 } },
      { do: "scroll", at: { x: 120, y: 220 }, delta: { x: 0, y: -120 } },
      { do: "text", value: "ab" },
      { do: "key", key: "Enter" },
    ]);
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("preserves recorded pauses between logical actions", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(path, [
    { qpc: "1000000", x: 10, y: 20, kind: 2, data: 0 },
    { qpc: "1000100", x: 10, y: 20, kind: 3, data: 0 },
    { qpc: "6000100", x: 30, y: 40, kind: 2, data: 0 },
    { qpc: "6000200", x: 30, y: 40, kind: 3, data: 0 },
  ].map((event) => JSON.stringify(event)).join("\n"));
  try {
    const scenario = await normalizeRaw(path, "https://example.test", "sample", undefined, 1_000_000n);
    assertEquals(scenario.steps, [
      { do: "click", at: { x: 10, y: 20 } },
      { do: "sleep", ms: 5000 },
      { do: "click", at: { x: 30, y: 40 } },
    ]);
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("applies a coordinate transform while normalizing", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(
    path,
    [
      { qpc: "1", x: 1100, y: 700, kind: 2, data: 0 },
      { qpc: "2", x: 1100, y: 700, kind: 3, data: 0 },
    ].map((event) => JSON.stringify(event)).join("\n"),
  );
  try {
    const scenario = await normalizeRaw(path, "https://example.test", "sample", {
      clientOrigin: { x: 100, y: 100 },
      clientSize: { x: 2000, y: 1200 },
      viewport: { x: 1000, y: 600 },
    });
    assertEquals(scenario.steps, [{ do: "click", at: { x: 500, y: 300 } }]);
    assertEquals(scenario.browser.window?.content, { width: 1000, height: 600 });
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("warns and omits an incomplete mouse click", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(path, JSON.stringify({ qpc: "1", x: 100, y: 200, kind: 2, data: 0 }));
  try {
    const normalized = await normalizeRawWithWarnings(path, "https://example.test", "sample");
    assertEquals(normalized.scenario.steps, []);
    assertEquals(normalized.warnings, ["ignored incomplete left mouse down at end of recording"]);
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("warns when a mouse down overlaps an unfinished click", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(path, [
    { qpc: "1", x: 10, y: 20, kind: 2, data: 0 },
    { qpc: "2", x: 30, y: 40, kind: 2, data: 0 },
    { qpc: "3", x: 30, y: 40, kind: 3, data: 0 },
  ].map((event) => JSON.stringify(event)).join("\n"));
  try {
    const normalized = await normalizeRawWithWarnings(path, "https://example.test", "sample");
    assertEquals(normalized.scenario.steps, [{ do: "click", at: { x: 30, y: 40 } }]);
    assertEquals(normalized.warnings, ["ignored incomplete left mouse down before next mouse down"]);
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("normalizes movement while pressed as a drag", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(path, [
    { qpc: "1", x: 100, y: 200, kind: 2, data: 0 },
    { qpc: "2", x: 150, y: 230, kind: 1, data: 0 },
    { qpc: "3", x: 150, y: 230, kind: 3, data: 0 },
  ].map((event) => JSON.stringify(event)).join("\n"));
  try {
    const scenario = await normalizeRaw(path, "https://example.test", "sample");
    assertEquals(scenario.steps, [{ do: "drag", from: { x: 100, y: 200 }, to: { x: 150, y: 230 } }]);
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("preserves Shift for recorded text", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(path, [
    { qpc: "1", x: 0, y: 0, kind: 7, data: 160 << 16 },
    { qpc: "2", x: 0, y: 0, kind: 7, data: 65 << 16 },
    { qpc: "3", x: 0, y: 0, kind: 8, data: 160 << 16 },
    { qpc: "4", x: 0, y: 0, kind: 7, data: 66 << 16 },
  ].map((event) => JSON.stringify(event)).join("\n"));
  try {
    const scenario = await normalizeRaw(path, "https://example.test", "sample");
    assertEquals(scenario.steps, [{ do: "text", value: "Ab" }]);
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("normalizes Ctrl shortcuts as key chords", async () => {
  const path = await Deno.makeTempFile();
  await Deno.writeTextFile(path, [
    { qpc: "1", x: 0, y: 0, kind: 7, data: 17 << 16 },
    { qpc: "2", x: 0, y: 0, kind: 7, data: 65 << 16 },
    { qpc: "3", x: 0, y: 0, kind: 8, data: 17 << 16 },
  ].map((event) => JSON.stringify(event)).join("\n"));
  try {
    const scenario = await normalizeRaw(path, "https://example.test", "sample");
    assertEquals(scenario.steps, [{ do: "key_chord", keys: ["Control", "a"] }]);
  } finally {
    await Deno.remove(path);
  }
});
