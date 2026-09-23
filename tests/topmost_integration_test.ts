import { assert, assertEquals } from "@std/assert";
import {
  closeSharedBrowserSession,
  createSharedBrowserSession,
  playScenario,
} from "../src/runtime.ts";
import type { Scenario } from "../src/types.ts";

const chrome = Deno.env.get("CRER_TEST_CHROME");
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

Deno.test({
  name: "Chrome honors always/once topmost modes across standalone playback and reused sessions",
  ignore: Deno.build.os !== "windows" || !chrome || Deno.env.get("CRER_TEST_WINDOWS") !== "1",
  async fn() {
    const title = `crer-topmost-${crypto.randomUUID()}`;
    const user32 = Deno.dlopen("user32.dll", {
      EnumWindows: { parameters: ["function", "isize"], result: "i32" },
      GetWindowTextW: { parameters: ["pointer", "buffer", "i32"], result: "i32" },
      IsWindowVisible: { parameters: ["pointer"], result: "i32" },
      GetWindowLongPtrW: { parameters: ["pointer", "i32"], result: "isize" },
      GetLayeredWindowAttributes: {
        parameters: ["pointer", "buffer", "buffer", "buffer"],
        result: "i32",
      },
      GetForegroundWindow: { parameters: [], result: "pointer" },
      GetWindow: { parameters: ["pointer", "u32"], result: "pointer" },
      CreateWindowExW: {
        parameters: [
          "u32",
          "buffer",
          "buffer",
          "u32",
          "i32",
          "i32",
          "i32",
          "i32",
          "pointer",
          "pointer",
          "pointer",
          "pointer",
        ],
        result: "pointer",
      },
      DestroyWindow: { parameters: ["pointer"], result: "i32" },
      SetWindowPos: {
        parameters: ["pointer", "isize", "i32", "i32", "i32", "i32", "u32"],
        result: "i32",
      },
    });
    const windows: Deno.PointerValue[] = [];
    const enumerate = new Deno.UnsafeCallback(
      { parameters: ["pointer", "isize"], result: "i32" },
      (window) => {
        const buffer = new Uint8Array(1024);
        const length = user32.symbols.GetWindowTextW(window, buffer, 512);
        const name = new TextDecoder("utf-16le").decode(buffer.subarray(0, length * 2));
        if (name.includes(title) && user32.symbols.IsWindowVisible(window)) windows.push(window);
        return 1;
      },
    );
    const findWindow = () => {
      windows.length = 0;
      user32.symbols.EnumWindows(enumerate.pointer, 0n);
      return windows[0];
    };
    const isTopmost = (window: Deno.PointerValue) =>
      (Number(user32.symbols.GetWindowLongPtrW(window, -20)) & 8) !== 0;
    const sharedSession = createSharedBrowserSession("before-step");
    const windowAlpha = (window: Deno.PointerValue) => {
      if (!(Number(user32.symbols.GetWindowLongPtrW(window, -20)) & 0x80000)) return 255;
      const alpha = new Uint8Array(1);
      const flags = new Uint32Array(1);
      assertEquals(
        user32.symbols.GetLayeredWindowAttributes(window, new Uint32Array(1), alpha, flags),
        1,
      );
      assertEquals(flags[0], 2);
      return alpha[0];
    };
    let ready = false;
    let attemptsAtNavigation = 0;
    const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, (request) => {
      if (new URL(request.url).pathname === "/ready") ready = true;
      else if (new URL(request.url).pathname === "/") {
        attemptsAtNavigation = sharedSession.topmostAttempts;
      }
      return new Response(
        `<!doctype html><title>${title}</title><p>Topmost regression test</p>`,
        { headers: { "content-type": "text/html" } },
      );
    });
    const isAbove = (upper: Deno.PointerValue, lower: Deno.PointerValue) => {
      for (
        let window = user32.symbols.GetWindow(lower, 3);
        window;
        window = user32.symbols.GetWindow(window, 3)
      ) {
        if (Deno.UnsafePointer.equals(window, upper)) return true;
      }
      return false;
    };
    const wide = (value: string) =>
      new Uint16Array([...value, "\0"].map((char) => char.charCodeAt(0)));
    const profileDir = `${Deno.cwd()}/.crer/profiles/topmost-${crypto.randomUUID()}`;
    try {
      for (
        const mode of [
          "standalone",
          "standalone-once",
          "shared",
          "reused-once",
          "reused-once-again",
          "reused",
          "disabled",
        ] as const
      ) {
        ready = false;
        const enabled = mode !== "disabled";
        const standalone = mode.startsWith("standalone");
        const foregroundMode = mode.includes("once") ? "once" : "always";
        const opacity = {
          standalone: 0.5,
          "standalone-once": 0,
          shared: 0.25,
          "reused-once": 0.75,
          "reused-once-again": undefined,
          reused: 1,
          disabled: 0.4,
        }[mode];
        const scenario: Scenario = {
          version: 1,
          name: `topmost-${mode}`,
          browser: {
            chrome: "chrome-for-testing@pinned",
            initial_url: `http://127.0.0.1:${server.addr.port}/?mode=${mode}`,
            // Exercise the empty app-window launch path used for URL blocking too.
            block_urls: ["/blocked-image"],
            window: {
              opacity,
              foreground: enabled,
              // Also cover the omitted/default always mode on standalone playback.
              ...(mode === "standalone" ? {} : { foreground_mode: foregroundMode }),
              bounds: { left: 30, top: 30, width: 400, height: 300 },
            },
          },
          steps: [
            { do: "navigate", url: `http://127.0.0.1:${server.addr.port}/ready` },
            { do: "sleep", ms: 1000 },
            { do: "wait_for", state: "complete" },
            { do: "sleep", ms: 1000 },
            { do: "sleep", ms: 2000 },
          ],
        };
        const playback = playScenario(scenario, {
          chromePath: chrome!,
          inputDllPath: "native/bin/Release/net10.0/win-x64/publish/crer-win-input.dll",
          ...(standalone ? {} : { profileDir, sharedSession }),
        });
        let failure: unknown;
        try {
          const deadline = Date.now() + 10_000;
          let window: Deno.PointerValue | undefined;
          while (Date.now() < deadline) {
            window = findWindow();
            if (ready && window && isTopmost(window) === enabled) break;
            await sleep(50);
          }
          assert(window, `${mode}: browser window was not found`);
          assert(ready, `${mode}: playback did not start`);
          assertEquals(isTopmost(window), enabled, mode);
          assertEquals(windowAlpha(window), Math.round((opacity ?? 1) * 255), mode);
          if (enabled && foregroundMode === "once") {
            // Another topmost window may cover CfT while it retains WS_EX_TOPMOST.
            const overlay = user32.symbols.CreateWindowExW(
              0x08000088,
              wide("STATIC"),
              wide("CRER topmost overlay test"),
              0x90000000,
              30,
              30,
              400,
              300,
              null,
              null,
              null,
              null,
            );
            assert(overlay, "could not create the test overlay");
            try {
              assertEquals(user32.symbols.SetWindowPos(overlay, -1n, 0, 0, 0, 0, 0x13), 1);
              const guard = standalone ? undefined : sharedSession.foreground;
              const originalFocus = guard?.foregroundProcess;
              let focusCalls = 0;
              if (guard && originalFocus) {
                guard.foregroundProcess = (pid) => {
                  focusCalls++;
                  return originalFocus(pid);
                };
              }
              const deadline = Date.now() + 2500;
              try {
                while (Date.now() < deadline) {
                  assertEquals(windowAlpha(window), Math.round((opacity ?? 1) * 255), mode);
                  assertEquals(
                    isTopmost(window),
                    true,
                    `${mode}: CfT must retain its topmost flag`,
                  );
                  assert(
                    isAbove(overlay, window),
                    `${mode}: CfT must not raise itself above another topmost window`,
                  );
                  // The user may change desktop focus during this test; track our own requests.
                  assertEquals(focusCalls, 0, `${mode}: a later step requested focus`);
                  await sleep(50);
                }
              } finally {
                if (guard && originalFocus) guard.foregroundProcess = originalFocus;
              }
            } finally {
              user32.symbols.DestroyWindow(overlay);
            }
          } else if (enabled) {
            await sleep(500);
            // Remove the OS flag mid-sleep: a before-step-only implementation cannot recover.
            assertEquals(user32.symbols.SetWindowPos(window, -2n, 0, 0, 0, 0, 0x13), 1);
            assertEquals(isTopmost(window), false);
            const foreground = user32.symbols.GetForegroundWindow();
            const recoveryDeadline = Date.now() + 1500;
            while (!isTopmost(window) && Date.now() < recoveryDeadline) await sleep(25);
            assertEquals(isTopmost(window), true, `${mode}: topmost did not recover during sleep`);
            assert(
              Deno.UnsafePointer.equals(user32.symbols.GetForegroundWindow(), foreground),
              `${mode}: watchdog changed focus`,
            );
          } else {
            await sleep(500);
            assertEquals(isTopmost(window), false, "previous scenario watchdog must stop");
          }
        } catch (error) {
          failure = error;
        }
        const result = await playback;
        assertEquals(result.code, 0, JSON.stringify(result));
        if (failure) throw failure;
        const diagnostic = JSON.parse(await Deno.readTextFile(`${result.runDir}/foreground.json`));
        assertEquals(diagnostic.mode, foregroundMode);
        if (enabled && foregroundMode === "once") {
          assertEquals(
            diagnostic.topmostAttempts - (standalone ? 0 : attemptsAtNavigation),
            1,
            mode,
          );
          if (!standalone) assertEquals(sharedSession.topmostTimer, undefined);
        } else if (enabled) {
          assert(diagnostic.topmostAttempts > 2, "watchdog must run while sleeping");
        }
      }
    } finally {
      await closeSharedBrowserSession(sharedSession);
      await server.shutdown();
      enumerate.close();
      user32.close();
      await Deno.remove(profileDir, { recursive: true }).catch(() => {});
    }
  },
});
