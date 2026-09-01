import { parse, stringify } from "@std/yaml";
import type { Plan, Scenario } from "./types.ts";

export async function loadYaml(path: string): Promise<unknown> {
  return parse(await Deno.readTextFile(path));
}

export async function saveYaml(path: string, value: unknown): Promise<void> {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    const record = value as Record<string, unknown>;
    if (Array.isArray(record.steps)) {
      const { steps, ...header } = record;
      const stepLines = steps.map((step) => `  - ${flowYaml(step)}`);
      await Deno.writeTextFile(path, `${stringify(header)}steps:\n${stepLines.join("\n")}\n`);
      return;
    }
  }
  await Deno.writeTextFile(path, stringify(value));
}

function flowYaml(value: unknown): string {
  if (Array.isArray(value)) return `[ ${value.map(flowYaml).join(", ")} ]`;
  if (value && typeof value === "object") {
    return `{ ${
      Object.entries(value as Record<string, unknown>).map(([key, item]) =>
        `${flowKey(key)}: ${flowYaml(item)}`
      ).join(", ")
    } }`;
  }
  return stringify(value, { flowLevel: 0 }).trim();
}
function flowKey(key: string) {
  return /^[A-Za-z_][A-Za-z0-9_-]*$/.test(key) ? key : stringify(key, { flowLevel: 0 }).trim();
}

function object(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be a mapping`);
  }
  return value as Record<string, unknown>;
}
function validateJitter(value: unknown, label: string) {
  const jitter = object(value, label);
  if (jitter.enabled === false) return;
  if (
    jitter.enabled !== true
    || !["none", "uniform", "normal"].includes(String(jitter.distribution))
    || typeof jitter.radius_px !== "number" || jitter.radius_px < 0
    || typeof jitter.min_distance_from_edge_px !== "number" || jitter.min_distance_from_edge_px < 0
    || !["fail", "disable-for-step"].includes(String(jitter.out_of_bounds))
  ) throw new Error(`${label} is invalid`);
}
function validateTemplateOptions(value: unknown, label: string, pathRequired: boolean) {
  const template = object(value, label);
  if (pathRequired && (typeof template.path !== "string" || !template.path)) {
    throw new Error(`${label}.path is required`);
  }
  if (
    template.min_similarity !== undefined
    && (typeof template.min_similarity !== "number" || !Number.isFinite(template.min_similarity)
      || template.min_similarity < 0 || template.min_similarity > 1)
  ) throw new Error(`${label}.min_similarity must be between 0 and 1`);
  if (
    template.random_inset_px !== undefined
    && (typeof template.random_inset_px !== "number" || !Number.isFinite(template.random_inset_px)
      || template.random_inset_px < 0)
  ) throw new Error(`${label}.random_inset_px must be non-negative`);
  if (
    template.on_missing !== undefined && template.on_missing !== "fail"
    && template.on_missing !== "skip"
  ) {
    throw new Error(`${label}.on_missing must be fail or skip`);
  }
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
  if (playback.template) validateTemplateOptions(playback.template, "playback.template", false);
  if (playback.on_failure) {
    validateFailurePolicies(
      playback.on_failure,
      "playback.on_failure",
      ["default", "navigation", "timeout", "action", "assertion", "jitter_bounds", "template"],
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
  const validateStep = (step: unknown, label: string): void => {
    const s = object(step, label);
    if (typeof s.do !== "string") throw new Error(`${label}.do is required`);
    if (s.do === "key_chord") {
      if (
        !Array.isArray(s.keys) || s.keys.length < 2
        || !s.keys.every((key) => typeof key === "string")
      ) {
        throw new Error(`${label}.keys requires at least two strings`);
      }
    }
    if (s.do === "sleep" && (typeof s.ms !== "number" || !Number.isFinite(s.ms) || s.ms < 0)) {
      throw new Error(`${label}.ms must be a non-negative number for sleep`);
    }
    if (s.do === "log" && typeof s.message !== "string") {
      throw new Error(`${label}.message must be a string for log`);
    }
    if (
      s.delay_ms !== undefined
      && (s.do === "sleep" || typeof s.delay_ms !== "number" || !Number.isFinite(s.delay_ms)
        || s.delay_ms < 0)
    ) {
      throw new Error(`${label}.delay_ms must be a non-negative number on a non-sleep step`);
    }
    if (s.locator_hint) {
      const hint = object(s.locator_hint, `${label}.locator_hint`);
      for (const key of ["role", "name", "text"]) {
        if (hint[key] !== undefined && typeof hint[key] !== "string") {
          throw new Error(`${label}.locator_hint.${key} must be a string`);
        }
      }
    }
    if (s.jitter) {
      validateJitter(s.jitter, `${label}.jitter`);
    }
    if (s.template) {
      if (s.do !== "click" && s.do !== "if") {
        throw new Error(`${label}.template is supported only for click or if`);
      }
      if (s.at) throw new Error(`${label} cannot specify both at and template`);
      if (s.jitter) throw new Error(`${label} cannot specify both jitter and template`);
      validateTemplateOptions(s.template, `${label}.template`, true);
    }
    if (s.do === "if") {
      const hasTemplate = s.template !== undefined;
      const hasWeekdays = s.weekdays !== undefined;
      if (hasTemplate === hasWeekdays) {
        throw new Error(`${label} requires exactly one of template or weekdays for if`);
      }
      if (
        hasWeekdays && (!Array.isArray(s.weekdays) || s.weekdays.length === 0
          || !s.weekdays.every((day) =>
            typeof day === "string" && [
              "mon",
              "tue",
              "wed",
              "thu",
              "fri",
              "sat",
              "sun",
            ].includes(day)
          ))
      ) {
        throw new Error(`${label}.weekdays must be a non-empty array of mon through sun`);
      }
      if (s.time_zone !== undefined) {
        if (typeof s.time_zone !== "string" || !s.time_zone) {
          throw new Error(`${label}.time_zone must be an IANA time zone string`);
        }
        if (!Intl.supportedValuesOf("timeZone").includes(s.time_zone)) {
          throw new Error(`${label}.time_zone must be an IANA time zone string`);
        }
      }
      if (!Array.isArray(s.then)) throw new Error(`${label}.then must be a step array for if`);
      for (const [index, child] of s.then.entries()) validateStep(child, `${label}.then[${index}]`);
      if (s.else !== undefined) {
        if (!Array.isArray(s.else)) throw new Error(`${label}.else must be a step array for if`);
        for (const [index, child] of s.else.entries()) {
          validateStep(child, `${label}.else[${index}]`);
        }
      }
    } else if (
      s.then !== undefined || s.else !== undefined || s.weekdays !== undefined
      || s.time_zone !== undefined
    ) {
      throw new Error(`${label}.then, else, weekdays, and time_zone are supported only for if`);
    }
    if (s.do === "repeat") {
      if (!Number.isInteger(s.count) || (s.count as number) < 1) {
        throw new Error(`${label}.count must be a positive integer for repeat`);
      }
      if (!Array.isArray(s.steps)) {
        throw new Error(`${label}.steps must be a step array for repeat`);
      }
      for (const [index, child] of s.steps.entries()) {
        validateStep(child, `${label}.steps[${index}]`);
      }
    } else if (s.count !== undefined || s.steps !== undefined) {
      throw new Error(`${label}.count and steps are supported only for repeat`);
    }
  };
  for (const [index, step] of v.steps.entries()) validateStep(step, `steps[${index}]`);
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
