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

export function scenarioFrom(value: unknown): Scenario {
  const v = object(value, "scenario");
  if (v.version !== 1 || typeof v.name !== "string" || !Array.isArray(v.steps)) {
    throw new Error("scenario requires version: 1, name, and steps");
  }
  const browser = object(v.browser, "browser");
  if (typeof browser.initial_url !== "string") throw new Error("browser.initial_url is required");
  const playback = v.playback ? object(v.playback, "playback") : {};
  if (playback.jitter) validateJitter(playback.jitter, "playback.jitter");
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
  return v as unknown as Plan;
}
