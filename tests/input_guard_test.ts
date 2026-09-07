import { assertEquals, assertRejects } from "@std/assert";
import { InputGuard } from "../src/input_guard.ts";

Deno.test("releases a pressed mouse after an action failure", async () => {
  const events: Array<Record<string, unknown>> = [];
  const guard = new InputGuard((_method, params) => {
    events.push(params);
    if (params.type === "mouseMoved") return Promise.reject(new Error("move failed"));
    return Promise.resolve({});
  });
  await assertRejects(
    () =>
      guard.run(async (input) => {
        await input.mouseDown(10, 20);
        await Promise.reject(new Error("move failed"));
      }),
    Error,
    "move failed",
  );
  assertEquals(events.at(-1)?.type, "mouseReleased");
});

Deno.test("releases pressed keys in reverse order after a chord failure", async () => {
  const events: Array<Record<string, unknown>> = [];
  const guard = new InputGuard((_method, params) => {
    events.push(params);
    return Promise.resolve({});
  });
  await assertRejects(() =>
    guard.run(async (input) => {
      await input.keyDown({
        type: "rawKeyDown",
        key: "Control",
        code: "ControlLeft",
        windowsVirtualKeyCode: 17,
        modifiers: 0,
      });
      await input.keyDown({
        type: "rawKeyDown",
        key: "A",
        code: "KeyA",
        windowsVirtualKeyCode: 65,
        modifiers: 2,
      });
      throw new Error("chord failed");
    }), Error, "chord failed");
  assertEquals(events.slice(-2).map((event) => event.code), ["KeyA", "ControlLeft"]);
  assertEquals(events.slice(-2).every((event) => event.type === "keyUp"), true);
});
