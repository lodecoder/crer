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
