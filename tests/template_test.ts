import { assertEquals, assertRejects } from "jsr:@std/assert@^1.0.14";
import {
  matchTemplate,
  matchTemplates,
  randomPointInMatch,
  TemplateMatchError,
  templatePath,
} from "../src/template.ts";

Deno.test("selects a deterministic point within a template match", () => {
  assertEquals(
    randomPointInMatch({ x: 10, y: 20, width: 40, height: 30, similarity: 1 }, 2, () => 0.5),
    { x: 30, y: 35 },
  );
});

Deno.test("retains the in-memory screenshot when template evaluation fails", async () => {
  const path = await Deno.makeTempFile();
  try {
    await Deno.writeFile(path, new Uint8Array([0]));
    const error = await assertRejects(
      () =>
        matchTemplate(
          async <T>(method: string) => {
            if (method === "Page.captureScreenshot") return { data: btoa("failure") } as T;
            return {
              result: { description: "Error" },
              exceptionDetails: { exception: { description: "template is too large" } },
            } as T;
          },
          { path },
          undefined,
        ),
      TemplateMatchError,
      "template match evaluation failed",
    );
    assertEquals((error as TemplateMatchError).screenshot, btoa("failure"));
  } finally {
    await Deno.remove(path);
  }
});

Deno.test("resolves a relative template against the scenario directory", () => {
  assertEquals(templatePath("fixtures/playback", "templates/button.png"), "fixtures/playback/templates/button.png");
});

Deno.test("returns all template matches supplied by the browser evaluation", async () => {
  const path = await Deno.makeTempFile();
  try {
    await Deno.writeFile(path, new Uint8Array([0]));
    const matches = [
      { x: 10, y: 20, width: 30, height: 40, similarity: 0.99 },
      { x: 50, y: 60, width: 30, height: 40, similarity: 0.98 },
    ];
    const result = await matchTemplates(
      async <T>(method: string) => {
        if (method === "Page.captureScreenshot") return { data: btoa("screen") } as T;
        return { result: { value: matches } } as T;
      },
      { path },
      undefined,
      0.9,
      5,
    );
    assertEquals(result.matches, matches);
    assertEquals(result.screenshot, btoa("screen"));
  } finally {
    await Deno.remove(path);
  }
});
