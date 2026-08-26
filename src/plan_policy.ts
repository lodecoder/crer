import type { FailurePolicy, RunResult } from "./types.ts";

export type PlanFailureKind = "scenario_failure" | "timeout" | "environment";

export function planFailureKind(results: RunResult[]): PlanFailureKind | undefined {
  if (!results.some((result) => result.code !== 0)) return undefined;
  if (results.some((result) => result.code === 3)) return "environment";
  if (results.some((result) => result.failures.some((failure) => failure.includes(":timeout:")))) {
    return "timeout";
  }
  return "scenario_failure";
}

export function shouldAbortPlan(
  results: RunResult[],
  onFailure?: Record<string, FailurePolicy | undefined>,
): boolean {
  const kind = planFailureKind(results);
  if (!kind) return false;
  // A lost CDP connection or an unavailable browser cannot safely continue.
  if (kind === "environment") return true;
  return (onFailure?.[kind] ?? onFailure?.default ?? "abort") === "abort";
}

export function aggregatePlanExitCode(results: RunResult[]): 0 | 3 | 4 | 5 {
  if (results.some((result) => result.code === 5)) return 5;
  if (results.some((result) => result.code === 3)) return 3;
  return results.some((result) => result.code !== 0) ? 4 : 0;
}
