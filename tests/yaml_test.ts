import { assertEquals, assertThrows } from "jsr:@std/assert@^1.0.14";
import { saveYaml, scenarioFrom } from "../src/yaml.ts";
import { planFrom } from "../src/yaml.ts";

Deno.test("validates key_chord keys", () => {
  const scenario = {
    version: 1,
    name: "shortcut",
    browser: { initial_url: "https://example.test" },
    steps: [{ do: "key_chord", keys: ["Control", "A"] }],
  };
  scenarioFrom(scenario);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "key_chord", keys: ["Control"] }] }),
    Error,
    "requires at least two strings",
  );
});

Deno.test("rejects invalid jitter settings", () => {
  assertThrows(() => scenarioFrom({
    version: 1,
    name: "bad-jitter",
    browser: { initial_url: "https://example.test" },
    playback: { jitter: { enabled: true, distribution: "random", radius_px: -1, min_distance_from_edge_px: 0, out_of_bounds: "fail" } },
    steps: [],
  }), Error, "playback.jitter is invalid");
});

Deno.test("validates locator hint text", () => {
  const scenario = {
    version: 1,
    name: "result",
    browser: { initial_url: "https://example.test" },
    steps: [{ do: "assert", locator_hint: { role: "status", text: "complete" } }],
  };
  scenarioFrom(scenario);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "assert", locator_hint: { text: 1 } }] }),
    Error,
    "locator_hint.text must be a string",
  );
});

Deno.test("validates click template matching options", () => {
  const scenario = {
    version: 1,
    name: "template",
    browser: { chrome: "chrome-for-testing@pinned", initial_url: "https://example.test" },
    steps: [{ do: "click", template: { path: "templates/button.png", min_similarity: 0.8 } }],
  };
  assertEquals(scenarioFrom(scenario).steps.length, 1);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "click", at: { x: 1, y: 1 }, template: { path: "x.png" } }] }),
    Error,
    "cannot specify both at and template",
  );
});

Deno.test("writes each scenario step as a one-line flow mapping", async () => {
  const path = await Deno.makeTempFile();
  try {
    await saveYaml(path, {
      version: 1,
      name: "flow-steps",
      browser: { chrome: "chrome-for-testing@pinned", initial_url: "https://example.test" },
      steps: [{ do: "sleep", ms: 6575 }, { do: "click", at: { x: 10, y: 20 } }],
    });
    const text = await Deno.readTextFile(path);
    assertEquals(text.includes("  - {do: sleep, ms: 6575}\n"), true);
    assertEquals(text.includes("  - {do: click, at: {x: 10, 'y': 20}}\n"), true);
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("validates plan failure policies and nodes", () => {
  const plan = {
    version: 1,
    name: "plan",
    max_parallel: 2,
    on_failure: { timeout: "continue" },
    run: { serial: [{ scenario: "one.crer.yaml" }] },
  };
  planFrom(plan);
  assertThrows(
    () => planFrom({ ...plan, on_failure: { unknown: "continue" } }),
    Error,
    "plan.on_failure.unknown is not supported",
  );
  assertThrows(
    () => planFrom({ ...plan, run: { scenario: "one", serial: [] } }),
    Error,
    "exactly one",
  );
});
