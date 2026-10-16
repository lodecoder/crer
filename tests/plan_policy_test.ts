import { assertEquals } from "@std/assert";
import { test } from "node:test";
import { aggregatePlanExitCode, planFailureKind, shouldAbortPlan } from "../src/plan_policy.ts";

test("classifies and continues a plan timeout", () => {
  const results = [{ code: 4 as const, failures: ["plan:timeout:worker_ms"] }];
  assertEquals(planFailureKind(results), "timeout");
  assertEquals(shouldAbortPlan(results, { timeout: "continue" }), false);
  assertEquals(aggregatePlanExitCode(results), 4);
});

test("always aborts an environment failure", () => {
  const results = [{ code: 3 as const, failures: ["CDP disconnected"] }];
  assertEquals(planFailureKind(results), "environment");
  assertEquals(shouldAbortPlan(results, { environment: "continue" }), true);
  assertEquals(aggregatePlanExitCode(results), 3);
});

test("prefers an interrupt exit code", () => {
  assertEquals(aggregatePlanExitCode([{ code: 5, failures: ["interrupted"] }]), 5);
});
