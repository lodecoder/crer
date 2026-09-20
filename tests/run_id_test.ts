import { assertEquals, assertMatch } from "@std/assert";
import { createRunId } from "../src/run_id.ts";

Deno.test("creates a Windows-safe UTC-prefixed run ID", () => {
  const id = createRunId(
    new Date("2026-09-13T04:15:23.123Z"),
    "550e8400-e29b-41d4-a716-446655440000",
  );
  assertEquals(id, "20260913T041523123Z-550e8400-e29b-41d4-a716-446655440000");
  assertMatch(id, /^[A-Za-z0-9-]+$/);
});

Deno.test("run IDs sort by UTC timestamp", () => {
  const uuid = "550e8400-e29b-41d4-a716-446655440000";
  const later = createRunId(new Date("2026-09-13T04:15:23.124Z"), uuid);
  const earlier = createRunId(new Date("2026-09-13T04:15:23.123Z"), uuid);
  assertEquals([later, earlier].sort(), [earlier, later]);
});
