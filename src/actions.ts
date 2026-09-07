import { InputGuard } from "./input_guard.ts";
import { jitter, type Random } from "./prng.ts";
import type { Jitter, Step } from "./types.ts";

type KeyInfo = { key: string; vk: number; code: string; text?: string };
const keys: Record<string, KeyInfo> = {
  Enter: { key: "Enter", vk: 13, code: "Enter", text: "\r" },
  Tab: { key: "Tab", vk: 9, code: "Tab" },
  Escape: { key: "Escape", vk: 27, code: "Escape" },
  Backspace: { key: "Backspace", vk: 8, code: "Backspace" },
  Delete: { key: "Delete", vk: 46, code: "Delete" },
  ArrowDown: { key: "ArrowDown", vk: 40, code: "ArrowDown" },
  ArrowUp: { key: "ArrowUp", vk: 38, code: "ArrowUp" },
  ArrowLeft: { key: "ArrowLeft", vk: 37, code: "ArrowLeft" },
  ArrowRight: { key: "ArrowRight", vk: 39, code: "ArrowRight" },
  Control: { key: "Control", vk: 17, code: "ControlLeft" },
  Alt: { key: "Alt", vk: 18, code: "AltLeft" },
  Shift: { key: "Shift", vk: 16, code: "ShiftLeft" },
  Meta: { key: "Meta", vk: 91, code: "MetaLeft" },
};
const modifierBits: Record<string, number> = { Alt: 1, Control: 2, Meta: 4, Shift: 8 };
const keyInfo = (key: string): KeyInfo =>
  keys[key] ?? {
    key,
    vk: key.length === 1 ? key.toUpperCase().charCodeAt(0) : 0,
    code: key.length === 1 && /^[a-z]$/i.test(key)
      ? `Key${key.toUpperCase()}`
      : key.length === 1 && /^\d$/.test(key)
      ? `Digit${key}`
      : key,
  };
const keyEvent = (type: "rawKeyDown" | "keyUp", info: KeyInfo, modifiers = 0) => ({
  type,
  key: info.key,
  code: info.code,
  windowsVirtualKeyCode: info.vk,
  modifiers,
});
const keyDownEvent = (info: KeyInfo, modifiers = 0) =>
  info.text && modifiers === 0
    ? {
      type: "keyDown",
      key: info.key,
      code: info.code,
      windowsVirtualKeyCode: info.vk,
      text: info.text,
      unmodifiedText: info.text,
      modifiers,
    }
    : keyEvent("rawKeyDown", info, modifiers);

export type AtomicActionAdapter = {
  call: (method: string, params: Record<string, unknown>) => Promise<unknown>;
  viewport: { x: number; y: number };
  waitFor: (step: Step, timeout: number, signal?: AbortSignal) => Promise<void>;
  assertState: (step: Step) => Promise<void>;
  capture: (name: string) => Promise<unknown>;
  sleep: (ms: number, signal?: AbortSignal) => Promise<void>;
};

/** Executes only leaf actions. Control-flow expansion and artifacts stay in the runtime orchestrator. */
export async function executeAtomicAction(
  adapter: AtomicActionAdapter,
  step: Step,
  at: { x: number; y: number } | undefined,
  jitterConfig: Jitter | undefined,
  rng: Random,
  timeout: number,
  signal?: AbortSignal,
): Promise<unknown> {
  const { call } = adapter;
  switch (step.do) {
    case "navigate":
      return await call("Page.navigate", { url: step.url });
    case "wait_for":
      return await adapter.waitFor(step, timeout, signal);
    case "assert":
      return await adapter.assertState(step);
    case "click":
    case "double_click": {
      const count = step.do === "double_click" ? 2 : 1;
      const holdMs = Number(step.hold_ms ?? 0);
      if (!Number.isFinite(holdMs) || holdMs < 0) {
        throw new Error("click hold_ms must be a non-negative number after argument expansion");
      }
      for (let clickCount = 1; clickCount <= count; clickCount++) {
        const input = new InputGuard(call);
        await input.run(async (guard) => {
          await call("Input.dispatchMouseEvent", { type: "mouseMoved", x: at!.x, y: at!.y });
          await guard.mouseDown(at!.x, at!.y, clickCount);
          if (holdMs > 0) await adapter.sleep(holdMs, signal);
          await guard.mouseUp(at!.x, at!.y);
        });
      }
      return;
    }
    case "mouse_move":
      return await call("Input.dispatchMouseEvent", { type: "mouseMoved", x: at!.x, y: at!.y });
    case "drag": {
      const from = step.from as { x?: number; y?: number } | undefined;
      const to = step.to as { x?: number; y?: number } | undefined;
      if (
        !from || !to || !Number.isFinite(from.x) || !Number.isFinite(from.y)
        || !Number.isFinite(to.x) || !Number.isFinite(to.y)
      ) throw new Error("drag requires from and to points");
      const fromPoint = { x: from.x!, y: from.y! };
      const toPoint = { x: to.x!, y: to.y! };
      const jitteredFrom = jitter(fromPoint, jitterConfig, rng, adapter.viewport);
      if (!jitteredFrom) throw new Error("jitter bounds failure");
      const offset = { x: jitteredFrom.x - fromPoint.x, y: jitteredFrom.y - fromPoint.y };
      const jitteredTo = { x: toPoint.x + offset.x, y: toPoint.y + offset.y };
      if (
        jitteredTo.x < 0 || jitteredTo.y < 0 || jitteredTo.x > adapter.viewport.x
        || jitteredTo.y > adapter.viewport.y
      ) throw new Error("jitter bounds failure");
      const input = new InputGuard(call);
      return await input.run(async (guard) => {
        await call("Input.dispatchMouseEvent", {
          type: "mouseMoved",
          x: jitteredFrom.x,
          y: jitteredFrom.y,
        });
        await guard.mouseDown(jitteredFrom.x, jitteredFrom.y);
        for (let index = 1; index <= 10; index++) {
          const ratio = index / 10;
          await call("Input.dispatchMouseEvent", {
            type: "mouseMoved",
            x: jitteredFrom.x + (jitteredTo.x - jitteredFrom.x) * ratio,
            y: jitteredFrom.y + (jitteredTo.y - jitteredFrom.y) * ratio,
            button: "left",
            buttons: 1,
          });
        }
        await guard.mouseUp(jitteredTo.x, jitteredTo.y);
      });
    }
    case "scroll":
      return await call("Input.dispatchMouseEvent", {
        type: "mouseWheel",
        x: at!.x,
        y: at!.y,
        deltaX: step.delta?.x ?? 0,
        deltaY: step.delta?.y ?? 0,
      });
    case "text":
      return await call("Input.insertText", { text: step.value ?? "" });
    case "key": {
      const info = keyInfo(step.key ?? "");
      const input = new InputGuard(call);
      return await input.run(async (guard) => {
        await guard.keyDown(keyDownEvent(info));
        await guard.keyUp(keyEvent("keyUp", info, 0));
      });
    }
    case "key_chord": {
      const chord = step.keys;
      if (
        !Array.isArray(chord) || chord.length < 2 || !chord.every((key) => typeof key === "string")
      ) {
        throw new Error("key_chord requires keys with one or more modifiers and a final key");
      }
      const modifiers = chord.slice(0, -1) as string[];
      if (!modifiers.every((key) => key in modifierBits)) {
        throw new Error("key_chord modifiers must be Alt, Control, Meta, or Shift");
      }
      const input = new InputGuard(call);
      return await input.run(async (guard) => {
        let mask = 0;
        for (const modifier of modifiers) {
          await guard.keyDown(keyDownEvent(keyInfo(modifier), mask));
          mask |= modifierBits[modifier];
        }
        const info = keyInfo(chord.at(-1)! as string);
        await guard.keyDown(keyDownEvent(info, mask));
        await guard.keyUp(keyEvent("keyUp", info, mask));
        for (const modifier of modifiers.toReversed()) {
          mask &= ~modifierBits[modifier];
          await guard.keyUp(keyEvent("keyUp", keyInfo(modifier), mask));
        }
      });
    }
    case "sleep": {
      const ms = Number(step.ms ?? 0);
      if (!Number.isFinite(ms) || ms < 0) {
        throw new Error("sleep ms must be a non-negative number after argument expansion");
      }
      return await adapter.sleep(ms, signal);
    }
    case "log":
      console.log(`[crer] ${step.message}`);
      return;
    case "screenshot":
      return await adapter.capture(String(step.name ?? "screenshot"));
    default:
      throw new Error(`unsupported atomic step: ${step.do}`);
  }
}
