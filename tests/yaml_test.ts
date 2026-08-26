import { assertThrows } from "jsr:@std/assert@^1.0.14";
import { scenarioFrom } from "../src/yaml.ts";

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
