import { assertEquals, assertThrows } from "@std/assert";
import {
  parsePlanWindowBoundsOverride,
  parsePlanWindowOpacityOverride,
  parseTemplateScreenshotPolicy,
} from "../src/options.ts";

Deno.test("parses a run-only window bounds override", () => {
  assertEquals(parsePlanWindowBoundsOverride(undefined), undefined);
  assertEquals(parsePlanWindowBoundsOverride("-800,40"), { left: -800, top: 40 });
  assertEquals(
    parsePlanWindowBoundsOverride("10,20,1280,900"),
    { left: 10, top: 20, width: 1280, height: 900 },
  );
  assertThrows(
    () => parsePlanWindowBoundsOverride("10,20,30"),
    Error,
    "must be left,top or left,top,width,height",
  );
  assertThrows(
    () => parsePlanWindowBoundsOverride("10,20,0,900"),
    Error,
    "width and height must be positive",
  );
});

Deno.test("parses template screenshot policy", () => {
  assertEquals(parseTemplateScreenshotPolicy(undefined), undefined);
  assertEquals(parseTemplateScreenshotPolicy("all"), "all");
  assertEquals(parseTemplateScreenshotPolicy("failure-only"), "failure-only");
  assertThrows(
    () => parseTemplateScreenshotPolicy("none"),
    Error,
    "must be all or failure-only",
  );
});

Deno.test("parses window opacity overrides including transparent and opaque endpoints", () => {
  assertEquals(parsePlanWindowOpacityOverride(undefined), undefined);
  for (const value of ["0", "0.5", "1", " 0.25 "]) {
    assertEquals(parsePlanWindowOpacityOverride(value), Number(value));
  }
  for (const value of ["", " ", "NaN", "Infinity", "-0.1", "1.01", "false", "0.5,1"]) {
    assertThrows(
      () => parsePlanWindowOpacityOverride(value),
      Error,
      "--plan-window-opacity-override",
    );
  }
});
