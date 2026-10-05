import { assertEquals, assertMatch } from "@std/assert";
import { createRunId } from "../src/run_id.ts";

Deno.test("creates a Windows-safe UTC-prefixed run ID", () => {
  const id = createRunId(
    "daily-check",
    new Date("2026-09-13T04:15:23.123Z"),
    "550e8400-e29b-41d4-a716-446655440000",
  );
  assertEquals(id, "20260913T041523123Z-daily-check-550e8400-e29b-41d4-a716-446655440000");
  assertMatch(id, /^[\p{L}\p{N}_-]+$/u);
});

Deno.test("run IDs sort by UTC timestamp", () => {
  const uuid = "550e8400-e29b-41d4-a716-446655440000";
  const later = createRunId("work", new Date("2026-09-13T04:15:23.124Z"), uuid);
  const earlier = createRunId("work", new Date("2026-09-13T04:15:23.123Z"), uuid);
  assertEquals([later, earlier].sort(), [earlier, later]);
});

Deno.test("sanitizes scenario names for Windows paths and preserves Japanese names", () => {
  const uuid = "550e8400-e29b-41d4-a716-446655440000";
  assertEquals(
    createRunId("日次/レポート:確認", new Date("2026-09-13T04:15:23.123Z"), uuid),
    "20260913T041523123Z-日次-レポート-確認-550e8400-e29b-41d4-a716-446655440000",
  );
  assertMatch(createRunId("../", new Date("2026-09-13T04:15:23.123Z"), uuid), /-run-/);
  assertMatch(
    createRunId("x".repeat(200), new Date("2026-09-13T04:15:23.123Z"), uuid),
    /-x{64}-550e8400/,
  );
});
