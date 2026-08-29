import { assertEquals } from "jsr:@std/assert@^1.0.14";
import { randomPointInMatch, templatePath } from "../src/template.ts";

Deno.test("selects a deterministic point within a template match", () => {
  assertEquals(
    randomPointInMatch({ x: 10, y: 20, width: 40, height: 30, similarity: 1 }, 2, () => 0.5),
    { x: 30, y: 35 },
  );
});

Deno.test("resolves a relative template against the scenario directory", () => {
  assertEquals(templatePath("fixtures/playback", "templates/button.png"), "fixtures/playback/templates/button.png");
});
