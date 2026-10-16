import { assertEquals } from "@std/assert";
import { test } from "node:test";
import { chromeEnvironment } from "../src/chrome_environment.ts";

test("CfT drops inherited DPI shims but preserves unrelated compatibility settings", () => {
  for (
    const layer of ["DPIUNAWARE", "GDIDPISCALING DPIUNAWARE", "~ HIGHDPIAWARE", "dpiunaware", ""]
  ) {
    assertEquals(chromeEnvironment(layer), { __COMPAT_LAYER: "" });
  }
  assertEquals(chromeEnvironment("~ WIN7RTM DPIUNAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE"), {
    __COMPAT_LAYER: "~ WIN7RTM DISABLEDXMAXIMIZEDWINDOWEDMODE",
  });
  assertEquals(chromeEnvironment("RUNASINVOKER"), { __COMPAT_LAYER: "RUNASINVOKER" });
});
