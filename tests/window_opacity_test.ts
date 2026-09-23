import { assertEquals, assertThrows } from "@std/assert";
import { EnvironmentError } from "../src/errors.ts";
import { opacityAlpha, WindowOpacity } from "../src/window_opacity.ts";
import { scenarioFrom } from "../src/yaml.ts";

Deno.test("window opacity validates YAML values and converts to native alpha", () => {
  const scenario = (opacity: unknown) =>
    scenarioFrom({
      version: 1,
      name: "opacity",
      browser: {
        chrome: "chrome-for-testing@pinned",
        initial_url: "https://example.com",
        window: { opacity },
      },
      steps: [],
    });
  for (const [value, alpha] of [[undefined, 255], [0, 0], [0.5, 128], [1, 255]] as const) {
    assertEquals(scenario(value).browser.window?.opacity, value);
    assertEquals(opacityAlpha(value), alpha);
  }
  for (const value of [-0.01, 1.01, NaN, Infinity, -Infinity, null, "0.5", true, {}, []]) {
    assertThrows(() => scenario(value), Error, "browser.window.opacity");
  }
});

Deno.test("default opacity needs no native DLL but transparency does", () => {
  const opacity = new WindowOpacity(1);
  try {
    opacity.configure();
    opacity.configure(1);
    assertThrows(() => opacity.configure(0.5), EnvironmentError, "requires");
  } finally {
    opacity.close();
    opacity.close();
  }
});
