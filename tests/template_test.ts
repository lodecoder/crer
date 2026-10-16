import { assertEquals, assertRejects } from "@std/assert";
import { rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { test } from "node:test";
import {
  matchTemplate,
  matchTemplates,
  randomPointInMatch,
  TemplateMatchError,
  templatePath,
} from "../src/template.ts";
import { temporaryFile } from "./helpers.ts";

test("selects a deterministic point within a template match", () => {
  assertEquals(
    randomPointInMatch({ x: 10, y: 20, width: 40, height: 30, similarity: 1 }, 2, () => 0.5),
    { x: 30, y: 35 },
  );
});

test("retains the in-memory screenshot when template evaluation fails", async () => {
  const path = await temporaryFile();
  try {
    await writeFile(path, new Uint8Array([0]));
    const error = await assertRejects(
      () =>
        matchTemplate(
          <T>(method: string): Promise<T> => {
            if (method === "Page.captureScreenshot") {
              return Promise.resolve({ data: btoa("failure") } as T);
            }
            return Promise.resolve({
              result: { description: "Error" },
              exceptionDetails: { exception: { description: "template is too large" } },
            } as T);
          },
          { path },
          undefined,
        ),
      TemplateMatchError,
      "template match evaluation failed",
    );
    assertEquals((error as TemplateMatchError).screenshot, btoa("failure"));
  } finally {
    await rm(path);
  }
});

test("resolves a relative template against the scenario directory", () => {
  assertEquals(
    templatePath("fixtures/playback", "templates/button.png"),
    resolve("fixtures/playback", "templates/button.png"),
  );
});

test("returns all template matches supplied by the browser evaluation", async () => {
  const path = await temporaryFile();
  try {
    await writeFile(path, new Uint8Array([0]));
    const matches = [
      { x: 10, y: 20, width: 30, height: 40, similarity: 0.99 },
      { x: 50, y: 60, width: 30, height: 40, similarity: 0.98 },
    ];
    const result = await matchTemplates(
      <T>(method: string): Promise<T> => {
        if (method === "Page.captureScreenshot") {
          return Promise.resolve({ data: btoa("screen") } as T);
        }
        return Promise.resolve({ result: { value: matches } } as T);
      },
      { path },
      undefined,
      0.9,
      5,
    );
    assertEquals(result.matches, matches);
    assertEquals(result.screenshot, btoa("screen"));
  } finally {
    await rm(path);
  }
});
