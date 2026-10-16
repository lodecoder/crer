import { assertEquals } from "@std/assert";
import { dirname, resolve } from "node:path";
import { test } from "node:test";
import { fileDirectory, resolveFromDirectory } from "../src/paths.ts";

test("uses cwd as the directory for a bare scenario filename", () => {
  assertEquals(fileDirectory("scenario.crer.yaml"), process.cwd());
});

test("resolves child files from relative and absolute parent files", () => {
  const plan = resolve("fixtures", "playback", "nightly.crer.plan.yaml");
  assertEquals(
    resolveFromDirectory(fileDirectory(plan), "child.crer.yaml"),
    resolve(dirname(plan), "child.crer.yaml"),
  );
  assertEquals(resolveFromDirectory(process.cwd(), plan), plan);
});
