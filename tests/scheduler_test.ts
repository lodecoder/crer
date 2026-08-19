import { assertEquals } from "jsr:@std/assert@^1.0.14";
import { mapWithConcurrency } from "../src/scheduler.ts";

Deno.test("limits concurrent work while preserving result order", async () => {
  let active = 0;
  let peak = 0;
  const result = await mapWithConcurrency([1, 2, 3, 4], 2, async (value) => {
    active++;
    peak = Math.max(peak, active);
    await new Promise((resolve) => setTimeout(resolve, 5));
    active--;
    return value * 10;
  });
  assertEquals(peak, 2);
  assertEquals(result, [10, 20, 30, 40]);
});
