import { assertRejects } from "@std/assert";
import { test } from "node:test";
import { EnvironmentError } from "../src/errors.ts";
import { awaitChromeEndpoint } from "../src/runtime.ts";

test("classifies Chrome exit before its endpoint as an environment failure", async () => {
  await assertRejects(
    () =>
      awaitChromeEndpoint(
        new Promise(() => {}),
        Promise.resolve({ code: 42, signal: null }),
      ),
    EnvironmentError,
    "Chrome exited before the CDP endpoint was ready",
  );
});
