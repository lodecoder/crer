import { assertEquals, assertStringIncludes, assertThrows } from "@std/assert";
import { validateCommandOptions } from "../src/cli.ts";
import { EnvironmentError, exitCodeFor, InterruptedError, ValidationError } from "../src/errors.ts";

Deno.test("validates command-specific options", () => {
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

Deno.test("maps validation failures to exit code 2 without an uncaught stack", async () => {
  const result = await new Deno.Command(Deno.execPath(), {
    args: ["run", "-A", "src/main.ts", "validate", "missing.crer.yaml"],
    cwd: Deno.cwd(),
    stdout: "piped",
    stderr: "piped",
  }).output();
  const stderr = new TextDecoder().decode(result.stderr);
  assertEquals(result.code, 2);
  assertStringIncludes(stderr, "invalid scenario or plan");
  assertEquals(stderr.includes("Uncaught"), false);
});

Deno.test("maps typed failures to the documented process exit codes", () => {
  assertEquals(exitCodeFor(new ValidationError("bad input")), 2);
  assertEquals(exitCodeFor(new EnvironmentError("browser lost")), 3);
  assertEquals(exitCodeFor(new InterruptedError("cancelled")), 5);
});
