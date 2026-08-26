import { assertEquals } from "jsr:@std/assert@^1.0.14";
import { aggregatePlanExitCode, planFailureKind, shouldAbortPlan } from "../src/plan_policy.ts";

Deno.test("classifies and continues a plan timeout", () => {
  const results = [{ code: 4 as const, failures: ["plan:timeout:worker_ms"] }];
  assertEquals(planFailureKind(results), "timeout");
  assertEquals(shouldAbortPlan(results, { timeout: "continue" }), false);
  assertEquals(aggregatePlanExitCode(results), 4);
});

Deno.test("always aborts an environment failure", () => {
  const results = [{ code: 3 as const, failures: ["CDP disconnected"] }];
  assertEquals(planFailureKind(results), "environment");
  assertEquals(shouldAbortPlan(results, { environment: "continue" }), true);
  assertEquals(aggregatePlanExitCode(results), 3);
});

Deno.test("prefers an interrupt exit code", () => {
  assertEquals(aggregatePlanExitCode([{ code: 5, failures: ["interrupted"] }]), 5);
});
