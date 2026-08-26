import { parse, stringify } from "@std/yaml";
import type { Plan, Scenario } from "./types.ts";

export async function loadYaml(path: string): Promise<unknown> {
  return parse(await Deno.readTextFile(path));
}

export async function saveYaml(path: string, value: unknown): Promise<void> {
  await Deno.writeTextFile(path, stringify(value));
}

function object(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be a mapping`);
  }
  return value as Record<string, unknown>;
}
function validateJitter(value: unknown, label: string) {
  const jitter = object(value, label);
  if (
    typeof jitter.enabled !== "boolean"
    || !["none", "uniform", "normal"].includes(String(jitter.distribution))
    || typeof jitter.radius_px !== "number" || jitter.radius_px < 0
    || typeof jitter.min_distance_from_edge_px !== "number" || jitter.min_distance_from_edge_px < 0
    || !["fail", "disable-for-step"].includes(String(jitter.out_of_bounds))
  ) throw new Error(`${label} is invalid`);
}
function validateFailurePolicies(value: unknown, label: string, allowed: readonly string[]) {
  const policies = object(value, label);
  for (const [kind, policy] of Object.entries(policies)) {
    if (!allowed.includes(kind)) throw new Error(`${label}.${kind} is not supported`);
    if (policy !== "abort" && policy !== "continue") {
      throw new Error(`${label}.${kind} must be abort or continue`);
    }
  }
}

export function scenarioFrom(value: unknown): Scenario {
  const v = object(value, "scenario");
  if (v.version !== 1 || typeof v.name !== "string" || !Array.isArray(v.steps)) {
    throw new Error("scenario requires version: 1, name, and steps");
  }
  const browser = object(v.browser, "browser");
  if (typeof browser.initial_url !== "string") throw new Error("browser.initial_url is required");
  if (browser.profile !== undefined && typeof browser.profile !== "string") {
    throw new Error("browser.profile must be a string");
  }
  if (
    typeof browser.profile === "string" && browser.profile.startsWith("persistent:")
    && !browser.profile.slice("persistent:".length).trim()
  ) {
    throw new Error("browser.profile persistent: requires a directory");
  }
  if (browser.window !== undefined) {
    const window = object(browser.window, "browser.window");
    for (const name of ["content", "viewport"]) {
      if (window[name] === undefined) continue;
      const size = object(window[name], `browser.window.${name}`);
      if (
        typeof size.width !== "number" || !Number.isFinite(size.width) || size.width <= 0
        || typeof size.height !== "number" || !Number.isFinite(size.height) || size.height <= 0
      ) {
        throw new Error(`browser.window.${name} requires positive width and height`);
      }
    }
  }
  const playback = v.playback ? object(v.playback, "playback") : {};
  if (playback.jitter) validateJitter(playback.jitter, "playback.jitter");
  if (playback.on_failure) {
    validateFailurePolicies(
      playback.on_failure,
      "playback.on_failure",
      ["default", "navigation", "timeout", "action", "assertion", "jitter_bounds"],
    );
  }
  if (
    playback.step_delay_ms !== undefined
    && (typeof playback.step_delay_ms !== "number" || !Number.isFinite(playback.step_delay_ms)
      || playback.step_delay_ms < 0)
  ) throw new Error("playback.step_delay_ms must be a non-negative number");
  if (
    playback.seed !== undefined
    && (typeof playback.seed !== "string" || !/^\d+$/.test(playback.seed))
  ) throw new Error("playback.seed must be a uint64 decimal string");
  if (playback.seed && BigInt(playback.seed) > 0xffff_ffff_ffff_ffffn) {
    throw new Error("playback.seed exceeds uint64");
  }
  for (const [index, step] of v.steps.entries()) {
    const s = object(step, `steps[${index}]`);
    if (typeof s.do !== "string") throw new Error(`steps[${index}].do is required`);
    if (s.do === "key_chord") {
      if (
        !Array.isArray(s.keys) || s.keys.length < 2
        || !s.keys.every((key) => typeof key === "string")
      ) {
        throw new Error(`steps[${index}].keys requires at least two strings`);
      }
    }
    if (s.do === "sleep" && (typeof s.ms !== "number" || !Number.isFinite(s.ms) || s.ms < 0)) {
      throw new Error(`steps[${index}].ms must be a non-negative number for sleep`);
    }
    if (s.locator_hint) {
      const hint = object(s.locator_hint, `steps[${index}].locator_hint`);
      for (const key of ["role", "name", "text"]) {
        if (hint[key] !== undefined && typeof hint[key] !== "string") {
          throw new Error(`steps[${index}].locator_hint.${key} must be a string`);
        }
      }
    }
    if (s.jitter) {
      validateJitter(s.jitter, `steps[${index}].jitter`);
    }
  }
  return v as unknown as Scenario;
}

export function planFrom(value: unknown): Plan {
  const v = object(value, "plan");
  if (v.version !== 1 || typeof v.name !== "string" || !v.run) {
    throw new Error("plan requires version: 1, name, and run");
  }
  if (
    v.max_parallel !== undefined && (
      typeof v.max_parallel !== "number" || !Number.isInteger(v.max_parallel) || v.max_parallel < 1
    )
  ) throw new Error("plan.max_parallel must be a positive integer");
  if (v.timeouts) {
    const timeouts = object(v.timeouts, "plan.timeouts");
    if (
      timeouts.worker_ms !== undefined
      && (typeof timeouts.worker_ms !== "number" || !Number.isFinite(timeouts.worker_ms)
        || timeouts.worker_ms < 0)
    ) throw new Error("plan.timeouts.worker_ms must be a non-negative number");
  }
  if (v.on_failure) {
    validateFailurePolicies(
      v.on_failure,
      "plan.on_failure",
      ["default", "scenario_failure", "timeout", "environment"],
    );
  }
  validatePlanNode(v.run, "plan.run");
  return v as unknown as Plan;
}

function validatePlanNode(value: unknown, label: string) {
  const node = object(value, label);
  const variants = ["scenario", "serial", "parallel"].filter((key) => node[key] !== undefined);
  if (variants.length !== 1) {
    throw new Error(`${label} must have exactly one of scenario, serial, or parallel`);
  }
  if (node.scenario !== undefined) {
    if (typeof node.scenario !== "string") throw new Error(`${label}.scenario must be a string`);
    return;
  }
  if (node.serial !== undefined) {
    if (!Array.isArray(node.serial)) throw new Error(`${label}.serial must be an array`);
    node.serial.forEach((child, index) => validatePlanNode(child, `${label}.serial[${index}]`));
    return;
  }
  const parallel = object(node.parallel, `${label}.parallel`);
  if (!Array.isArray(parallel.jobs)) throw new Error(`${label}.parallel.jobs must be an array`);
  if (parallel.fail_fast !== undefined && typeof parallel.fail_fast !== "boolean") {
    throw new Error(`${label}.parallel.fail_fast must be a boolean`);
  }
  parallel.jobs.forEach((child, index) =>
    validatePlanNode(child, `${label}.parallel.jobs[${index}]`)
  );
}
