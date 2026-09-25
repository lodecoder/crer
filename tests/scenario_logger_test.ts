import { assertEquals, assertThrows } from "@std/assert";
import { formatScenarioLog } from "../src/scenario_logger.ts";
import { scenarioFrom } from "../src/yaml.ts";

Deno.test("scenario log colors reset per line and remain plain when color is disabled", () => {
  assertEquals(
    formatScenarioLog("login-a", "ready", "cyan", true),
    "\x1b[36m[login-a] ready\x1b[39m",
  );
  assertEquals(
    formatScenarioLog("login-b", "ready", "green", true),
    "\x1b[32m[login-b] ready\x1b[39m",
  );
  assertEquals(
    formatScenarioLog("login-a", "first\r\nsecond", "red", true),
    "\x1b[31m[login-a] first\x1b[39m\n\x1b[31m[login-a] second\x1b[39m",
  );
  assertEquals(formatScenarioLog("login-a", "ready", "cyan", false), "[login-a] ready");
  assertEquals(formatScenarioLog("login-a", "ready", "default", true), "[login-a] ready");
  assertEquals(formatScenarioLog("login-a", "ready"), "[login-a] ready");
});

Deno.test("YAML validates playback log colors", () => {
  const scenario = (log_color: unknown) =>
    scenarioFrom({
      version: 1,
      name: "login",
      browser: { initial_url: "https://example.test" },
      playback: { log_color },
      steps: [],
    });
  for (
    const color of [
      undefined,
      "default",
      "black",
      "red",
      "green",
      "yellow",
      "blue",
      "magenta",
      "cyan",
      "white",
    ]
  ) {
    assertEquals(scenario(color).playback?.log_color, color);
  }
  for (const color of [null, 31, {}, true, "", "toString", "constructor", "purple"]) {
    assertThrows(() => scenario(color), Error, "playback.log_color");
  }
});
