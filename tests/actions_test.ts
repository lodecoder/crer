import { assertEquals, assertRejects } from "@std/assert";
import { executeAtomicAction, type AtomicActionAdapter } from "../src/actions.ts";
import { Random } from "../src/prng.ts";
import type { Step } from "../src/types.ts";

function adapter(calls: Array<[string, Record<string, unknown>]>): AtomicActionAdapter {
  return {
    call(method, params) {
      calls.push([method, params]);
      return Promise.resolve({});
    },
    viewport: { x: 800, y: 600 },
    waitFor: () => Promise.resolve(),
    assertState: () => Promise.resolve(),
    capture: () => Promise.resolve(),
    sleep: () => Promise.resolve(),
  };
}

Deno.test("routes navigation through the atomic action adapter", async () => {
  const calls: Array<[string, Record<string, unknown>]> = [];
  await executeAtomicAction(
    adapter(calls),
    { do: "navigate", url: "https://example.com" } as Step,
    undefined,
    undefined,
    new Random(1n),
    1000,
  );
  assertEquals(calls, [["Page.navigate", { url: "https://example.com" }]]);
});

Deno.test("keeps paired mouse input inside an atomic handler", async () => {
  const calls: Array<[string, Record<string, unknown>]> = [];
  await executeAtomicAction(
    adapter(calls),
    { do: "click" } as Step,
    { x: 10, y: 20 },
    undefined,
    new Random(1n),
    1000,
  );
  assertEquals(calls.map(([method]) => method), [
    "Input.dispatchMouseEvent",
    "Input.dispatchMouseEvent",
    "Input.dispatchMouseEvent",
  ]);
});

Deno.test("rejects control-flow steps at the atomic handler boundary", async () => {
  await assertRejects(
    () =>
      executeAtomicAction(
        adapter([]),
        { do: "repeat", times: 1, steps: [] } as Step,
        undefined,
        undefined,
        new Random(1n),
        1000,
      ),
    Error,
    "unsupported atomic step",
  );
});

Deno.test("releases the mouse when a drag move fails", async () => {
  const events: Array<Record<string, unknown>> = [];
  let moved = 0;
  const value = adapter([]);
  value.call = (method, params) => {
    events.push(params);
    if (method === "Input.dispatchMouseEvent" && params.type === "mouseMoved" && ++moved === 2) {
      return Promise.reject(new Error("drag move failed"));
    }
    return Promise.resolve({});
  };
  await assertRejects(
    () =>
      executeAtomicAction(
        value,
        { do: "drag", from: { x: 10, y: 10 }, to: { x: 20, y: 20 } } as Step,
        undefined,
        undefined,
        new Random(1n),
        1000,
      ),
    Error,
    "drag move failed",
  );
  assertEquals(events.at(-1)?.type, "mouseReleased");
});

Deno.test("retries key release after the first keyUp failure", async () => {
  const events: Array<Record<string, unknown>> = [];
  let failed = false;
  const value = adapter([]);
  value.call = (_method, params) => {
    events.push(params);
    if (params.type === "keyUp" && !failed) {
      failed = true;
      return Promise.reject(new Error("key up failed"));
    }
    return Promise.resolve({});
  };
  await assertRejects(
    () =>
      executeAtomicAction(
        value,
        { do: "key", key: "A" } as Step,
        undefined,
        undefined,
        new Random(1n),
        1000,
      ),
    Error,
    "key up failed",
  );
  assertEquals(events.slice(-2).map((event) => event.type), ["keyUp", "keyUp"]);
});

Deno.test("releases a modifier when the final chord keyDown fails", async () => {
  const events: Array<Record<string, unknown>> = [];
  const value = adapter([]);
  value.call = (_method, params) => {
    events.push(params);
    if (params.type === "rawKeyDown" && params.code === "KeyA") {
      return Promise.reject(new Error("final key failed"));
    }
    return Promise.resolve({});
  };
  await assertRejects(
    () =>
      executeAtomicAction(
        value,
        { do: "key_chord", keys: ["Control", "A"] } as Step,
        undefined,
        undefined,
        new Random(1n),
        1000,
      ),
    Error,
    "final key failed",
  );
  assertEquals(events.at(-1)?.type, "keyUp");
  assertEquals(events.at(-1)?.code, "ControlLeft");
});
