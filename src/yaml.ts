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
const parameterReference = (value: unknown, parameters: ReadonlySet<string>) =>
  typeof value === "string" && /^\$\{([A-Za-z_][A-Za-z0-9_-]*)\}$/.test(value)
  && parameters.has(value.slice(2, -1));
const nonNegativeNumberOrParameter = (value: unknown, parameters: ReadonlySet<string>) =>
  typeof value === "number" && Number.isFinite(value) && value >= 0
  || parameterReference(value, parameters);
function validateJitter(
  value: unknown,
  label: string,
  parameters: ReadonlySet<string> = new Set(),
) {
  const jitter = object(value, label);
  validateKeys(jitter, label, [
    "enabled",
    "distribution",
    "radius_px",
    "min_distance_from_edge_px",
    "out_of_bounds",
  ]);
  if (jitter.enabled === false) return;
  if (
    jitter.enabled !== true
    || !["none", "uniform", "normal"].includes(String(jitter.distribution))
    || !nonNegativeNumberOrParameter(jitter.radius_px, parameters)
    || !nonNegativeNumberOrParameter(jitter.min_distance_from_edge_px, parameters)
    || !["fail", "disable-for-step"].includes(String(jitter.out_of_bounds))
  ) throw new Error(`${label} is invalid`);
}
function validateTemplateOptions(
  value: unknown,
  label: string,
  pathRequired: boolean,
  parameters: ReadonlySet<string> = new Set(),
) {
  const template = object(value, label);
  validateKeys(template, label, ["path", "min_similarity", "random_inset_px", "on_missing"]);
  if (pathRequired && (typeof template.path !== "string" || !template.path)) {
    throw new Error(`${label}.path is required`);
  }
  if (
    template.min_similarity !== undefined
    && !(typeof template.min_similarity === "number" && Number.isFinite(template.min_similarity)
      && template.min_similarity >= 0 && template.min_similarity <= 1)
    && !parameterReference(template.min_similarity, parameters)
  ) throw new Error(`${label}.min_similarity must be between 0 and 1`);
  if (
    template.random_inset_px !== undefined
    && !nonNegativeNumberOrParameter(template.random_inset_px, parameters)
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

function validateKeys(value: Record<string, unknown>, label: string, allowed: readonly string[]) {
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) throw new Error(`${label}.${key} is not supported`);
  }
}

function validateHttpUrl(value: unknown, label: string, wildcard = false) {
  if (typeof value !== "string" || !value) throw new Error(`${label} must be a URL string`);
  const candidate = wildcard ? value.replaceAll("*", "x") : value;
  let url: URL;
  try {
    url = new URL(candidate);
  } catch {
    throw new Error(`${label} must be a valid http or https URL`);
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error(`${label} must use http or https`);
  }
}

function validatePoint(
  value: unknown,
  label: string,
  parameters: ReadonlySet<string> = new Set(),
) {
  const point = object(value, label);
  validateKeys(point, label, ["x", "y"]);
  for (const axis of ["x", "y"]) {
    if (
      !(typeof point[axis] === "number" && Number.isFinite(point[axis]))
      && !parameterReference(point[axis], parameters)
    ) throw new Error(`${label}.${axis} must be a finite number`);
  }
}

const supportedSteps = [
  "navigate",
  "wait_for",
  "click",
  "double_click",
  "mouse_move",
  "drag",
  "scroll",
  "text",
  "key",
  "key_chord",
  "screenshot",
  "assert",
  "sleep",
  "log",
  "if",
  "repeat",
  "repeat_until",
  "for_each_template",
  "call",
  "break",
] as const;

const withDelay = (...keys: string[]) => ["do", ...keys, "delay_ms"];
const stepKeys: Record<string, readonly string[]> = {
  navigate: withDelay("url"),
  wait_for: withDelay("url", "state", "locator_hint"),
  click: withDelay("at", "template", "jitter", "count", "hold_ms", "then"),
  double_click: withDelay("at", "jitter"),
  mouse_move: withDelay("at"),
  drag: withDelay("from", "to"),
  scroll: withDelay("at", "delta"),
  text: withDelay("value"),
  key: withDelay("key"),
  key_chord: withDelay("keys"),
  screenshot: withDelay("name"),
  assert: withDelay("url", "state", "locator_hint"),
  sleep: ["do", "ms"],
  log: withDelay("message"),
  if: withDelay("template", "weekdays", "time_zone", "equals", "then", "else"),
  repeat: withDelay("count", "steps"),
  repeat_until: withDelay("template", "state", "max_attempts", "on_limit", "steps"),
  for_each_template: withDelay("template", "max_matches", "steps"),
  call: withDelay("function", "args"),
  break: ["do"],
};

export function scenarioFrom(value: unknown): Scenario {
  const v = object(value, "scenario");
  validateKeys(v, "scenario", ["version", "name", "browser", "playback", "functions", "steps"]);
  if (v.version !== 1 || typeof v.name !== "string" || !Array.isArray(v.steps)) {
    throw new Error("scenario requires version: 1, name, and steps");
  }
  const browser = object(v.browser, "browser");
  validateKeys(browser, "browser", ["chrome", "profile", "initial_url", "window", "display"]);
  if (browser.chrome !== undefined && typeof browser.chrome !== "string") {
    throw new Error("browser.chrome must be a string");
  }
  if (typeof browser.initial_url !== "string") throw new Error("browser.initial_url is required");
  validateHttpUrl(browser.initial_url, "browser.initial_url");
  if (browser.profile !== undefined && typeof browser.profile !== "string") {
    throw new Error("browser.profile must be a string");
  }
  if (
    typeof browser.profile === "string" && browser.profile !== "ephemeral"
    && !browser.profile.startsWith("persistent:")
  ) throw new Error("browser.profile must be ephemeral or persistent:<directory>");
  if (
    typeof browser.profile === "string" && browser.profile.startsWith("persistent:")
    && !browser.profile.slice("persistent:".length).trim()
  ) {
    throw new Error("browser.profile persistent: requires a directory");
  }
  if (browser.window !== undefined) {
    const window = object(browser.window, "browser.window");
    validateKeys(window, "browser.window", ["bounds", "foreground", "content", "viewport"]);
    if (window.bounds !== undefined) {
      const bounds = object(window.bounds, "browser.window.bounds");
      validateKeys(bounds, "browser.window.bounds", ["left", "top", "width", "height"]);
      for (const key of ["left", "top"]) {
        if (typeof bounds[key] !== "number" || !Number.isInteger(bounds[key])) {
          throw new Error(`browser.window.bounds.${key} must be an integer`);
        }
      }
      for (const key of ["width", "height"]) {
        if (
          bounds[key] !== undefined
          && (typeof bounds[key] !== "number" || !Number.isInteger(bounds[key]) || bounds[key] <= 0)
        ) throw new Error(`browser.window.bounds.${key} must be a positive integer`);
      }
    }
    if (window.foreground !== undefined && typeof window.foreground !== "boolean") {
      throw new Error("browser.window.foreground must be a boolean");
    }
    for (const name of ["content", "viewport"]) {
      if (window[name] === undefined) continue;
      const size = object(window[name], `browser.window.${name}`);
      validateKeys(size, `browser.window.${name}`, ["width", "height"]);
      if (
        typeof size.width !== "number" || !Number.isFinite(size.width) || size.width <= 0
        || typeof size.height !== "number" || !Number.isFinite(size.height) || size.height <= 0
      ) {
        throw new Error(`browser.window.${name} requires positive width and height`);
      }
    }
  }
  if (browser.display !== undefined) {
    const display = object(browser.display, "browser.display");
    validateKeys(display, "browser.display", ["expected_dpr", "browser_zoom", "zoom_check"]);
    for (const key of ["expected_dpr", "browser_zoom"]) {
      if (
        display[key] !== undefined
        && (typeof display[key] !== "number" || !Number.isFinite(display[key]) || display[key] <= 0)
      ) throw new Error(`browser.display.${key} must be a positive number`);
    }
    if (
      display.zoom_check !== undefined
      && !["strict", "advisory", "off"].includes(String(display.zoom_check))
    ) throw new Error("browser.display.zoom_check must be strict, advisory, or off");
  }
  const playback = v.playback ? object(v.playback, "playback") : {};
  validateKeys(playback, "playback", [
    "seed",
    "speed",
    "step_delay_ms",
    "template",
    "artifacts",
    "jitter",
    "timeouts",
    "on_failure",
  ]);
  if (
    playback.speed !== undefined
    && (typeof playback.speed !== "number" || !Number.isFinite(playback.speed)
      || playback.speed <= 0)
  ) throw new Error("playback.speed must be a positive number");
  if (playback.timeouts !== undefined) {
    const timeouts = object(playback.timeouts, "playback.timeouts");
    validateKeys(timeouts, "playback.timeouts", ["navigation_ms", "action_ms"]);
    for (const key of ["navigation_ms", "action_ms"]) {
      if (
        timeouts[key] !== undefined
        && (typeof timeouts[key] !== "number" || !Number.isFinite(timeouts[key])
          || timeouts[key] < 0)
      ) throw new Error(`playback.timeouts.${key} must be a non-negative number`);
    }
  }
  if (playback.jitter) validateJitter(playback.jitter, "playback.jitter");
  if (playback.template) validateTemplateOptions(playback.template, "playback.template", false);
  if (playback.artifacts !== undefined) {
    const artifacts = object(playback.artifacts, "playback.artifacts");
    validateKeys(artifacts, "playback.artifacts", ["template_screenshots"]);
    if (
      artifacts.template_screenshots !== undefined
      && artifacts.template_screenshots !== "all"
      && artifacts.template_screenshots !== "failure-only"
    ) {
      throw new Error("playback.artifacts.template_screenshots must be all or failure-only");
    }
  }
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
  const functions = v.functions === undefined ? {} : object(v.functions, "functions");
  const definitions: Record<string, { params: string[]; steps: unknown[] }> = {};
  for (const [name, raw] of Object.entries(functions)) {
    if (!/^[A-Za-z_][A-Za-z0-9_-]*$/.test(name)) {
      throw new Error(`functions.${name} must be an identifier`);
    }
    if (Array.isArray(raw)) {
      definitions[name] = { params: [], steps: raw };
      continue;
    }
    const definition = object(raw, `functions.${name}`);
    validateKeys(definition, `functions.${name}`, ["params", "steps"]);
    if (!Array.isArray(definition.steps)) {
      throw new Error(`functions.${name}.steps must be a step array`);
    }
    const params = definition.params === undefined ? [] : definition.params;
    if (
      !Array.isArray(params) || !params.every((param) =>
        typeof param === "string" && /^[A-Za-z_][A-Za-z0-9_-]*$/.test(param)
      ) || new Set(params).size !== params.length
    ) {
      throw new Error(`functions.${name}.params must be unique identifiers`);
    }
    definitions[name] = { params: params as string[], steps: definition.steps };
  }
  const templateMatchParameters = new Set([
    "match_left",
    "match_top",
    "match_width",
    "match_height",
    "match_center_x",
    "match_center_y",
    "match_similarity",
  ]);
  const validateStep = (
    step: unknown,
    label: string,
    parameters: ReadonlySet<string> = new Set(),
    breakAllowed = false,
  ): void => {
    const s = object(step, label);
    if (typeof s.do !== "string") throw new Error(`${label}.do is required`);
    if (!(supportedSteps as readonly string[]).includes(s.do)) {
      throw new Error(`${label}.do is not supported: ${s.do}`);
    }
    if (s.at !== undefined) validatePoint(s.at, `${label}.at`, parameters);
    if (s.from !== undefined) validatePoint(s.from, `${label}.from`, parameters);
    if (s.to !== undefined) validatePoint(s.to, `${label}.to`, parameters);
    if (s.delta !== undefined) validatePoint(s.delta, `${label}.delta`, parameters);
    if (s.do === "navigate") validateHttpUrl(s.url, `${label}.url`);
    if (s.do === "wait_for" || s.do === "assert") {
      if (s.url !== undefined) validateHttpUrl(s.url, `${label}.url`, true);
      if (s.url === undefined && s.state === undefined && s.locator_hint === undefined) {
        throw new Error(`${label} requires url, state, or locator_hint`);
      }
      const states = s.do === "wait_for" ? ["complete", "network_idle", "visible"] : ["complete"];
      if (s.state !== undefined && !states.includes(String(s.state))) {
        throw new Error(`${label}.state is not supported for ${s.do}`);
      }
      if (s.state === "visible" && s.locator_hint === undefined) {
        throw new Error(`${label}.state visible requires locator_hint`);
      }
    }
    if (["click", "double_click", "mouse_move", "scroll"].includes(s.do)) {
      const hasPoint = s.at !== undefined;
      const hasTemplate = s.do === "click" && s.template !== undefined;
      if (!hasPoint && !hasTemplate) throw new Error(`${label} requires at or template`);
    }
    if (s.do === "drag" && (s.from === undefined || s.to === undefined)) {
      throw new Error(`${label} requires from and to`);
    }
    if (s.do === "scroll" && s.delta === undefined) throw new Error(`${label}.delta is required`);
    if (s.do === "text" && typeof s.value !== "string") {
      throw new Error(`${label}.value must be a string for text`);
    }
    if (s.do === "key" && (typeof s.key !== "string" || !s.key)) {
      throw new Error(`${label}.key must be a non-empty string for key`);
    }
    if (s.do === "screenshot") {
      if (typeof s.name !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(s.name)) {
        throw new Error(`${label}.name must be a safe artifact filename`);
      }
      if (s.name === "." || s.name === "..") {
        throw new Error(`${label}.name must be a safe artifact filename`);
      }
    }
    if (s.do === "break" && !breakAllowed) {
      throw new Error(`${label}.break is supported only inside for_each_template.steps`);
    }
    if (s.do === "key_chord") {
      if (
        !Array.isArray(s.keys) || s.keys.length < 2
        || !s.keys.every((key) => typeof key === "string")
      ) {
        throw new Error(`${label}.keys requires at least two strings`);
      }
      const modifiers = s.keys.slice(0, -1);
      if (!modifiers.every((key) => ["Alt", "Control", "Meta", "Shift"].includes(key))) {
        throw new Error(`${label}.keys must contain modifiers followed by a final key`);
      }
    }
    if (s.do === "sleep" && !nonNegativeNumberOrParameter(s.ms, parameters)) {
      throw new Error(`${label}.ms must be a non-negative number for sleep`);
    }
    if (s.do === "log" && typeof s.message !== "string") {
      throw new Error(`${label}.message must be a string for log`);
    }
    if (s.do === "call") {
      if (typeof s.function !== "string" || !Object.hasOwn(definitions, s.function)) {
        throw new Error(`${label}.function must name a defined function for call`);
      }
      const args = s.args === undefined ? {} : object(s.args, `${label}.args`);
      if (
        !Object.values(args).every((value) =>
          typeof value === "string" || typeof value === "number" && Number.isFinite(value)
        )
      ) {
        throw new Error(`${label}.args values must be strings or finite numbers`);
      }
      const params = definitions[s.function].params;
      const names = Object.keys(args);
      if (names.length !== params.length || !params.every((param) => Object.hasOwn(args, param))) {
        throw new Error(`${label}.args must provide exactly the function parameters`);
      }
    } else if (s.function !== undefined) {
      throw new Error(`${label}.function is supported only for call`);
    } else if (s.args !== undefined) {
      throw new Error(`${label}.args is supported only for call`);
    }
    if (
      s.delay_ms !== undefined
      && (s.do === "sleep" || !nonNegativeNumberOrParameter(s.delay_ms, parameters))
    ) {
      throw new Error(`${label}.delay_ms must be a non-negative number on a non-sleep step`);
    }
    if (
      s.hold_ms !== undefined
      && (s.do !== "click" || !nonNegativeNumberOrParameter(s.hold_ms, parameters))
    ) {
      throw new Error(`${label}.hold_ms must be a non-negative number on a click step`);
    }
    if (s.locator_hint) {
      const hint = object(s.locator_hint, `${label}.locator_hint`);
      validateKeys(hint, `${label}.locator_hint`, ["role", "name", "text"]);
      for (const key of ["role", "name", "text"]) {
        if (hint[key] !== undefined && typeof hint[key] !== "string") {
          throw new Error(`${label}.locator_hint.${key} must be a string`);
        }
      }
    }
    if (s.jitter) {
      validateJitter(s.jitter, `${label}.jitter`, parameters);
    }
    if (s.template) {
      if (
        s.do !== "click" && s.do !== "if" && s.do !== "repeat_until" && s.do !== "for_each_template"
      ) {
        throw new Error(
          `${label}.template is supported only for click, if, repeat_until, or for_each_template`,
        );
      }
      if (s.at) throw new Error(`${label} cannot specify both at and template`);
      if (s.jitter) throw new Error(`${label} cannot specify both jitter and template`);
      validateTemplateOptions(s.template, `${label}.template`, true, parameters);
    }
    if (s.on_missing !== undefined) {
      throw new Error(`${label}.on_missing must be nested under template`);
    }
    if (s.do === "if") {
      const hasTemplate = s.template !== undefined;
      const hasWeekdays = s.weekdays !== undefined;
      const hasEquals = s.equals !== undefined;
      if ([hasTemplate, hasWeekdays, hasEquals].filter(Boolean).length !== 1) {
        throw new Error(`${label} requires exactly one of template, weekdays, or equals for if`);
      }
      if (hasEquals) {
        const equals = object(s.equals, `${label}.equals`);
        validateKeys(equals, `${label}.equals`, ["left", "right"]);
        if (
          ![equals.left, equals.right].every((part) =>
            typeof part === "string" || typeof part === "number" && Number.isFinite(part)
          )
        ) {
          throw new Error(`${label}.equals requires string or finite-number left and right`);
        }
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
      for (const [index, child] of s.then.entries()) {
        validateStep(child, `${label}.then[${index}]`, parameters, breakAllowed);
      }
      if (s.else !== undefined) {
        if (!Array.isArray(s.else)) throw new Error(`${label}.else must be a step array for if`);
        for (const [index, child] of s.else.entries()) {
          validateStep(child, `${label}.else[${index}]`, parameters, breakAllowed);
        }
      }
    } else if (s.do === "click" && s.template && s.then !== undefined) {
      if (!Array.isArray(s.then)) {
        throw new Error(`${label}.then must be a step array for template click`);
      }
      for (const [index, child] of s.then.entries()) {
        validateStep(child, `${label}.then[${index}]`, parameters, breakAllowed);
      }
      if (
        s.else !== undefined || s.weekdays !== undefined || s.time_zone !== undefined
        || s.equals !== undefined
      ) {
        throw new Error(
          `${label}.else, weekdays, time_zone, and equals are supported only for if`,
        );
      }
    } else if (
      s.then !== undefined || s.else !== undefined || s.weekdays !== undefined
      || s.time_zone !== undefined || s.equals !== undefined
    ) {
      throw new Error(
        `${label}.then is supported for if or template click; else, weekdays, time_zone, and equals are supported only for if`,
      );
    }
    if (s.do === "repeat") {
      if (
        !(typeof s.count === "number" && Number.isInteger(s.count) && s.count >= 0)
        && !parameterReference(s.count, parameters)
      ) {
        throw new Error(`${label}.count must be a non-negative integer for repeat`);
      }
      if (!Array.isArray(s.steps)) {
        throw new Error(`${label}.steps must be a step array for repeat`);
      }
      for (const [index, child] of s.steps.entries()) {
        validateStep(child, `${label}.steps[${index}]`, parameters, breakAllowed);
      }
    } else if (s.do === "click") {
      if (
        s.count !== undefined
        && !(typeof s.count === "number" && Number.isInteger(s.count) && s.count >= 1)
        && !parameterReference(s.count, parameters)
      ) {
        throw new Error(`${label}.count must be a positive integer for click`);
      }
    } else if (s.count !== undefined) {
      throw new Error(`${label}.count is supported only for repeat or click`);
    }
    if (s.do === "repeat_until") {
      if (!s.template) throw new Error(`${label}.template is required for repeat_until`);
      if (s.state !== "visible" && s.state !== "hidden") {
        throw new Error(`${label}.state must be visible or hidden for repeat_until`);
      }
      if (
        !(typeof s.max_attempts === "number" && Number.isInteger(s.max_attempts)
          && s.max_attempts >= 1)
        && !parameterReference(s.max_attempts, parameters)
      ) {
        throw new Error(`${label}.max_attempts must be a positive integer for repeat_until`);
      }
      if (s.on_limit !== "fail" && s.on_limit !== "continue") {
        throw new Error(`${label}.on_limit must be fail or continue for repeat_until`);
      }
      if (!Array.isArray(s.steps)) {
        throw new Error(`${label}.steps must be a step array for repeat_until`);
      }
      for (const [index, child] of s.steps.entries()) {
        validateStep(child, `${label}.steps[${index}]`, parameters, breakAllowed);
      }
    } else if (s.do === "for_each_template") {
      if (!s.template) throw new Error(`${label}.template is required for for_each_template`);
      if (
        !(typeof s.max_matches === "number" && Number.isInteger(s.max_matches)
          && s.max_matches >= 1 && s.max_matches <= 100)
        && !parameterReference(s.max_matches, parameters)
      ) {
        throw new Error(
          `${label}.max_matches must be an integer from 1 through 100 for for_each_template`,
        );
      }
      if (!Array.isArray(s.steps)) {
        throw new Error(`${label}.steps must be a step array for for_each_template`);
      }
      const childParameters = new Set([...parameters, ...templateMatchParameters]);
      for (const [index, child] of s.steps.entries()) {
        validateStep(child, `${label}.steps[${index}]`, childParameters, true);
      }
    } else {
      if (s.max_attempts !== undefined || s.on_limit !== undefined || s.max_matches !== undefined) {
        throw new Error(
          `${label}.max_attempts, on_limit, and max_matches are supported only for repeat_until or for_each_template`,
        );
      }
      if (s.steps !== undefined && s.do !== "repeat") {
        throw new Error(
          `${label}.steps is supported only for repeat, repeat_until, or for_each_template`,
        );
      }
    }
    validateKeys(s, label, stepKeys[s.do]);
  };
  for (const [name, definition] of Object.entries(definitions)) {
    for (const [index, step] of definition.steps.entries()) {
      validateStep(step, `functions.${name}[${index}]`, new Set(definition.params));
    }
  }
  for (const [index, step] of v.steps.entries()) validateStep(step, `steps[${index}]`);
  return v as unknown as Scenario;
}

export function planFrom(value: unknown): Plan {
  const v = object(value, "plan");
  validateKeys(v, "plan", [
    "version",
    "name",
    "max_parallel",
    "browser_session",
    "timeouts",
    "on_failure",
    "run",
  ]);
  if (v.version !== 1 || typeof v.name !== "string" || !v.run) {
    throw new Error("plan requires version: 1, name, and run");
  }
  if (
    v.max_parallel !== undefined && (
      typeof v.max_parallel !== "number" || !Number.isInteger(v.max_parallel) || v.max_parallel < 1
    )
  ) throw new Error("plan.max_parallel must be a positive integer");
  if (v.browser_session !== undefined) {
    const session = object(v.browser_session, "plan.browser_session");
    validateKeys(session, "plan.browser_session", ["reuse", "focus"]);
    if (session.reuse !== "same-profile") {
      throw new Error("plan.browser_session.reuse must be same-profile");
    }
    if (
      session.focus !== undefined && session.focus !== "once" && session.focus !== "before-step"
    ) {
      throw new Error("plan.browser_session.focus must be once or before-step");
    }
    if ((v.max_parallel ?? 1) !== 1) {
      throw new Error("plan.browser_session requires max_parallel: 1");
    }
  }
  if (v.timeouts) {
    const timeouts = object(v.timeouts, "plan.timeouts");
    validateKeys(timeouts, "plan.timeouts", ["worker_ms"]);
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
  validateKeys(node, label, ["scenario", "serial", "parallel"]);
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
  validateKeys(parallel, `${label}.parallel`, ["fail_fast", "jobs"]);
  if (!Array.isArray(parallel.jobs)) throw new Error(`${label}.parallel.jobs must be an array`);
  if (parallel.fail_fast !== undefined && typeof parallel.fail_fast !== "boolean") {
    throw new Error(`${label}.parallel.fail_fast must be a boolean`);
  }
  parallel.jobs.forEach((child, index) =>
    validatePlanNode(child, `${label}.parallel.jobs[${index}]`)
  );
}
