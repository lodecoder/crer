import { assertEquals, assertStringIncludes, assertThrows } from "@std/assert";
import { test } from "node:test";
import { validateCommandOptions } from "../src/cli.ts";
import { EnvironmentError, exitCodeFor, InterruptedError, ValidationError } from "../src/errors.ts";
import { runCli } from "./helpers.ts";

test("validates command-specific options", () => {
  validateCommandOptions("play", ["--seed", "42", "--mute-audio"]);
  assertThrows(
    () => validateCommandOptions("play", ["--seed"]),
    Error,
    "--seed requires a value",
  );
  assertThrows(
    () => validateCommandOptions("play", ["--mute-audio", "--mute-audio"]),
    Error,
    "specified more than once",
  );
  assertThrows(
    () => validateCommandOptions("validate", ["--mute-audio"]),
    Error,
    "unknown option",
  );
});

test("maps validation failures to exit code 2 without an uncaught stack", async () => {
  const result = await runCli(["validate", "missing.crer.yaml"]);
  const stderr = new TextDecoder().decode(result.stderr);
  assertEquals(result.code, 2);
  assertStringIncludes(stderr, "invalid scenario or plan");
  assertEquals(stderr.includes("Uncaught"), false);
});

test("window opacity override is a run-only option with a required value", () => {
  validateCommandOptions("run", ["--plan-window-opacity-override", "0"]);
  assertThrows(
    () => validateCommandOptions("run", ["--plan-window-opacity-override"]),
    Error,
    "requires a value",
  );
  for (const command of ["play", "record", "validate"]) {
    assertThrows(
      () => validateCommandOptions(command, ["--plan-window-opacity-override", "0.5"]),
      Error,
      "unknown option",
    );
  }
});

test("maps typed failures to the documented process exit codes", () => {
  assertEquals(exitCodeFor(new ValidationError("bad input")), 2);
  assertEquals(exitCodeFor(new EnvironmentError("browser lost")), 3);
  assertEquals(exitCodeFor(new InterruptedError("cancelled")), 5);
});

for (const duration of ["NaN", "-1"]) {
  test(
    `record rejects duration ${duration} before resolving Chrome`,
    { timeout: 10_000 },
    async (context) => {
      const result = await runCli([
        "record",
        ".crer/invalid-duration.ndjson",
        "--chrome",
        "does-not-exist.exe",
        "--duration-ms",
        duration,
      ], { signal: context.signal });
      assertEquals(result.code, 2, result.stderr.toString("utf8"));
      assertStringIncludes(result.stderr.toString("utf8"), "--duration-ms");
    },
  );
}
