import { assertEquals, assertRejects } from "@std/assert";
import { ExecutionContext, StepExecutor } from "../src/executor.ts";
import type { Step } from "../src/types.ts";

Deno.test("step executor preserves nested indexes and one-child cache scope", async () => {
  const visited: Array<{ index: string; cache?: string }> = [];
  const context = new ExecutionContext();
  const executor = new StepExecutor<string>(context, async ({ step, index, cachedTemplate }, run) => {
    visited.push({ index, ...(cachedTemplate ? { cache: cachedTemplate } : {}) });
    if (step.do === "parent") {
      await run([{ do: "child" }, { do: "sibling" }], index, "cached");
    }
  });
  await executor.execute([{ do: "parent" }, { do: "last" }] as Step[]);
  assertEquals(visited, [
    { index: "0" },
    { index: "0.0", cache: "cached" },
    { index: "0.1" },
    { index: "1" },
  ]);
});

Deno.test("execution context stops traversal and rejects recursive calls", async () => {
  const context = new ExecutionContext();
  const visited: string[] = [];
  const executor = new StepExecutor<never>(context, ({ index }) => {
    visited.push(index);
    context.stop();
    return Promise.resolve();
  });
  await executor.execute([{ do: "first" }, { do: "second" }] as Step[]);
  assertEquals(visited, ["0"]);

  const leave = context.enterFunction("search");
  await assertRejects(
    () => Promise.resolve().then(() => context.enterFunction("search")),
    Error,
    "recursive function call",
  );
  leave();
});
