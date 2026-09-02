import { assertEquals, assertThrows } from "jsr:@std/assert@^1.0.14";
import { loadYaml, saveYaml, scenarioFrom } from "../src/yaml.ts";
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
  const disabled = {
    version: 1,
    name: "no-jitter",
    browser: { initial_url: "https://example.test" },
    playback: { jitter: { enabled: false } },
    steps: [{ do: "click", at: { x: 1, y: 2 }, jitter: { enabled: false } }],
  };
  assertEquals(scenarioFrom(disabled).steps.length, 1);
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
    playback: { template: { min_similarity: 0.8, random_inset_px: 2, on_missing: "skip" } },
    steps: [{ do: "click", template: { path: "templates/button.png", min_similarity: 0.8 } }],
  };
  assertEquals(scenarioFrom(scenario).steps.length, 1);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "click", at: { x: 1, y: 1 }, template: { path: "x.png" } }] }),
    Error,
    "cannot specify both at and template",
  );
  assertThrows(
    () => scenarioFrom({ ...scenario, playback: { template: { on_missing: "continue" } } }),
    Error,
    "playback.template.on_missing must be fail or skip",
  );
});

Deno.test("validates a post-operation delay", () => {
  const scenario = {
    version: 1,
    name: "delayed-click",
    browser: { initial_url: "https://example.test" },
    steps: [{ do: "click", at: { x: 1, y: 2 }, delay_ms: 1040 }],
  };
  scenarioFrom(scenario);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "sleep", ms: 10, delay_ms: 1 }] }),
    Error,
    "delay_ms must be a non-negative number on a non-sleep step",
  );
});

Deno.test("validates console log steps", () => {
  const scenario = {
    version: 1,
    name: "progress",
    browser: { initial_url: "https://example.test" },
    steps: [{ do: "log", message: "loaded sign-in page" }],
  };
  scenarioFrom(scenario);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "log" }] }),
    Error,
    "steps[0].message must be a string for log",
  );
});

Deno.test("validates repeat steps", () => {
  const scenario = {
    version: 1,
    name: "repeat",
    browser: { initial_url: "https://example.test" },
    steps: [{ do: "repeat", count: 3, steps: [{ do: "log", message: "retry" }] }],
  };
  assertEquals(scenarioFrom(scenario).steps.length, 1);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "repeat", count: 0, steps: [] }] }),
    Error,
    "steps[0].count must be a positive integer for repeat",
  );
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "repeat", count: 1 }] }),
    Error,
    "steps[0].steps must be a step array for repeat",
  );
});

Deno.test("validates named function calls", () => {
  const scenario = {
    version: 1,
    name: "functions",
    browser: { initial_url: "https://example.test" },
    functions: { login: [{ do: "log", message: "start login" }] },
    steps: [{ do: "call", function: "login" }],
  };
  assertEquals(scenarioFrom(scenario).steps.length, 1);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "call", function: "missing" }] }),
    Error,
    "steps[0].function must name a defined function for call",
  );
  assertThrows(
    () => scenarioFrom({ ...scenario, functions: { "not valid": [] } }),
    Error,
    "functions.not valid must be an identifier",
  );
});

Deno.test("validates function call arguments", () => {
  const scenario = {
    version: 1,
    name: "parameterized-functions",
    browser: { initial_url: "https://example.test" },
    functions: {
      search: {
        params: ["query"],
        steps: [{ do: "text", value: "${query}" }],
      },
    },
    steps: [{ do: "call", function: "search", args: { query: "crer" } }],
  };
  assertEquals(scenarioFrom(scenario).steps.length, 1);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "call", function: "search", args: {} }] }),
    Error,
    "steps[0].args must provide exactly the function parameters",
  );
  assertThrows(
    () => scenarioFrom({ ...scenario, functions: { search: { params: ["query", "query"], steps: [] } } }),
    Error,
    "functions.search.params must be unique identifiers",
  );
});

Deno.test("validates template conditional branches", () => {
  const scenario = {
    version: 1,
    name: "conditional-template",
    browser: { initial_url: "https://example.test" },
    steps: [{
      do: "if",
      template: { path: "templates/signed-in.png" },
      then: [{ do: "click", at: { x: 1, y: 2 } }],
      else: [{ do: "log", message: "not signed in" }],
    }],
  };
  assertEquals(scenarioFrom(scenario).steps.length, 1);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "if", then: [] }] }),
    Error,
    "steps[0] requires exactly one of template or weekdays for if",
  );
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "if", template: { path: "x.png" } }] }),
    Error,
    "steps[0].then must be a step array for if",
  );
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "if", template: { path: "x.png" }, then: [], else: {} }] }),
    Error,
    "steps[0].else must be a step array for if",
  );
});

Deno.test("validates weekday conditional branches", () => {
  const scenario = {
    version: 1,
    name: "conditional-weekday",
    browser: { initial_url: "https://example.test" },
    steps: [{ do: "if", weekdays: ["mon", "wed", "fri"], time_zone: "Asia/Tokyo", then: [] }],
  };
  assertEquals(scenarioFrom(scenario).steps.length, 1);
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "if", weekdays: ["monday"], then: [] }] }),
    Error,
    "steps[0].weekdays must be a non-empty array of mon through sun",
  );
  assertThrows(
    () => scenarioFrom({ ...scenario, steps: [{ do: "if", weekdays: ["mon"], time_zone: "JST", then: [] }] }),
    Error,
    "steps[0].time_zone must be an IANA time zone string",
  );
});

Deno.test("writes each scenario step as a one-line flow mapping", async () => {
  const path = await Deno.makeTempFile();
  try {
    await saveYaml(path, {
      version: 1,
      name: "flow-steps",
      browser: { chrome: "chrome-for-testing@pinned", initial_url: "https://example.test" },
      steps: [
        { do: "sleep", ms: 6575 },
        { do: "click", at: { x: 10, y: 20 }, delay_ms: 1040 },
      ],
    });
    const text = await Deno.readTextFile(path);
    assertEquals(text.includes("  - { do: sleep, ms: 6575 }\n"), true);
    assertEquals(
      text.includes("  - { do: click, at: { x: 10, y: 20 }, delay_ms: 1040 }\n"),
      true,
    );
    assertEquals(scenarioFrom(await loadYaml(path)).steps.length, 2);
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
