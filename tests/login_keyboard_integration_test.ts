import { assertEquals } from "@std/assert";
import { normalizeRaw } from "../src/normalize.ts";
import {
  closeSharedBrowserSession,
  createSharedBrowserSession,
  playScenario,
} from "../src/runtime.ts";

const chrome = Deno.env.get("CRER_TEST_CHROME");
Deno.test({
  name: "recorded Tab and Space navigate and submit a login form",
  ignore: Deno.build.os !== "windows" || !chrome,
  async fn() {
    const server = Deno.serve(
      { hostname: "127.0.0.1", port: 0, onListen() {} },
      () =>
        new Response(
          `
      <!doctype html><title>CRER keyboard regression test</title>
      <style>body { margin: 0 } input, button { display: block; width: 240px; height: 30px }</style>
      <form>
        <input id="email" type="email" aria-label="Email">
        <input id="password" type="password" aria-label="Password">
        <button id="toggle" type="button">Show password</button>
        <input id="remember" type="checkbox" aria-label="Remember me">
        <button id="login" type="submit">Log in</button>
      </form>
      <script>
        window.events = []; window.submitted = null;
        for (const type of ['focusin', 'keydown', 'keyup']) document.addEventListener(type, event => {
          window.events.push({ type, id: event.target.id, key: event.key, code: event.code });
        });
        document.querySelector('form').addEventListener('submit', event => {
          event.preventDefault();
          window.submitted = {
            email: document.querySelector('#email').value,
            password: document.querySelector('#password').value,
            remember: document.querySelector('#remember').checked,
          };
        });
      </script>`,
          { headers: { "content-type": "text/html" } },
        ),
    );
    const raw = await Deno.makeTempFile();
    const profile = `.crer/profiles/keyboard-${crypto.randomUUID()}`;
    const session = createSharedBrowserSession();
    const events: Array<{ qpc: string; kind: number; data: number; x: number; y: number }> = [];
    const push = (kind: number, data: number) =>
      events.push({ qpc: String(events.length + 1), kind, data, x: 80, y: 15 });
    const key = (vk: number) => {
      push(7, vk << 16);
      push(8, vk << 16);
    };
    const text = (value: string) => {
      for (const char of value) push(9, char.codePointAt(0)!);
    };
    try {
      // Both older Unicode-space recordings and explicit VK_SPACE recordings must replay.
      for (const spaceKind of [9, 7]) {
        events.length = 0;
        const space = () => spaceKind === 9 ? text(" ") : key(32);
        push(2, 0);
        push(3, 0);
        text("demo@example.test");
        key(9);
        text("demo");
        space();
        text("password");
        key(9);
        key(9);
        space();
        key(9);
        space();
        await Deno.writeTextFile(raw, events.map((event) => JSON.stringify(event)).join("\n"));
        const scenario = await normalizeRaw(
          raw,
          `http://127.0.0.1:${server.addr.port}`,
          "login-keyboard",
        );
        const result = await playScenario(scenario, {
          chromePath: chrome!,
          profileDir: profile,
          sharedSession: session,
        });
        assertEquals(result.code, 0, JSON.stringify(result));
        const browser = session.browser!;
        const snapshot = await browser.cdp.call<
          { result: { value: { submitted: unknown; events: Array<Record<string, string>> } } }
        >(
          "Runtime.evaluate",
          { expression: "({ submitted, events })", returnByValue: true },
          browser.sessionId,
        );
        assertEquals(snapshot.result.value.submitted, {
          email: "demo@example.test",
          password: "demo password",
          remember: true,
        });
        assertEquals(
          snapshot.result.value.events.filter((event) => event.type === "focusin").map((event) =>
            event.id
          ),
          ["email", "password", "toggle", "remember", "login"],
        );
        for (const id of ["password", "remember", "login"]) {
          assertEquals(
            snapshot.result.value.events.filter((event) =>
              event.id === id && event.code === "Space"
            )
              .map((event) => [event.type, event.key]),
            [["keydown", " "], ["keyup", " "]],
          );
        }
      }
    } finally {
      await closeSharedBrowserSession(session);
      await server.shutdown();
      await Deno.remove(raw);
      await Deno.remove(profile, { recursive: true }).catch(() => {});
    }
  },
});
