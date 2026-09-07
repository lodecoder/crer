import { assertEquals } from "@std/assert";
import { dirname, resolve } from "node:path";
import { fileDirectory, resolveFromDirectory } from "../src/paths.ts";

Deno.test("uses cwd as the directory for a bare scenario filename", () => {
  assertEquals(fileDirectory("scenario.crer.yaml"), Deno.cwd());
});

Deno.test("resolves child files from relative and absolute parent files", () => {
  const plan = resolve("fixtures", "playback", "nightly.crer.plan.yaml");
  assertEquals(
    resolveFromDirectory(fileDirectory(plan), "child.crer.yaml"),
    resolve(dirname(plan), "child.crer.yaml"),
  );
  assertEquals(resolveFromDirectory(Deno.cwd(), plan), plan);
});
