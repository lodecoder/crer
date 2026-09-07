import { assertEquals } from "@std/assert";
import { jitter, Random } from "../src/prng.ts";
Deno.test("jitter is deterministic for an identical seed", () => {
  const config = { enabled: true, distribution: "uniform" as const, radius_px: 3, min_distance_from_edge_px: 0, out_of_bounds: "fail" as const };
  const a = jitter({ x: 50, y: 50 }, config, new Random(42n), { x: 100, y: 100 });
  const b = jitter({ x: 50, y: 50 }, config, new Random(42n), { x: 100, y: 100 });
  assertEquals(a, b);
});
