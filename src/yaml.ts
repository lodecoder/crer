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

export function scenarioFrom(value: unknown): Scenario {
  const v = object(value, "scenario");
  if (v.version !== 1 || typeof v.name !== "string" || !Array.isArray(v.steps)) {
    throw new Error("scenario requires version: 1, name, and steps");
  }
  const browser = object(v.browser, "browser");
  if (typeof browser.initial_url !== "string") throw new Error("browser.initial_url is required");
  const playback = v.playback ? object(v.playback, "playback") : {};
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
    if (s.jitter) {
      for (
        const key of [
          "enabled",
          "distribution",
          "radius_px",
          "min_distance_from_edge_px",
          "out_of_bounds",
        ]
      ) {
        if (!(key in object(s.jitter, `steps[${index}].jitter`))) {
          throw new Error(`steps[${index}].jitter.${key} is required`);
        }
      }
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
