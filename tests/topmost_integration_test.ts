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
  name: "Chrome stays topmost during standalone and reused playback waits without stealing focus",
  ignore: Deno.build.os !== "windows" || !chrome || Deno.env.get("CRER_TEST_WINDOWS") !== "1",
  async fn() {
    const title = `crer-topmost-${crypto.randomUUID()}`;
    const user32 = Deno.dlopen("user32.dll", {
      EnumWindows: { parameters: ["function", "isize"], result: "i32" },
      GetWindowTextW: { parameters: ["pointer", "buffer", "i32"], result: "i32" },
      IsWindowVisible: { parameters: ["pointer"], result: "i32" },
      GetWindowLongPtrW: { parameters: ["pointer", "i32"], result: "isize" },
      GetForegroundWindow: { parameters: [], result: "pointer" },
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
    const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, () =>
      new Response(
        `<!doctype html><title>${title}</title><p>Topmost regression test</p>`,
        { headers: { "content-type": "text/html" } },
      ));
    const sharedSession = createSharedBrowserSession("once");
    const profileDir = `${Deno.cwd()}/.crer/profiles/topmost-${crypto.randomUUID()}`;
    try {
      for (const mode of ["standalone", "shared", "reused", "disabled"] as const) {
        const enabled = mode !== "disabled";
        const scenario: Scenario = {
          version: 1,
          name: `topmost-${mode}`,
          browser: {
            chrome: "chrome-for-testing@pinned",
            initial_url: `http://127.0.0.1:${server.addr.port}/?mode=${mode}`,
            // Exercise the empty app-window launch path used for URL blocking too.
            block_urls: ["/blocked-image"],
            window: { foreground: enabled, bounds: { left: 30, top: 30, width: 400, height: 300 } },
          },
          steps: [{ do: "sleep", ms: 4000 }],
        };
        const playback = playScenario(scenario, {
          chromePath: chrome!,
          inputDllPath: "native/bin/Release/net10.0/win-x64/publish/crer-win-input.dll",
          ...(mode === "standalone" ? {} : { profileDir, sharedSession }),
        });
        let failure: unknown;
        try {
          const deadline = Date.now() + 10_000;
          let window: Deno.PointerValue | undefined;
          while (Date.now() < deadline) {
            window = findWindow();
            if (window && isTopmost(window) === enabled) break;
            await sleep(50);
          }
          assert(window, `${mode}: browser window was not found`);
          assertEquals(isTopmost(window), enabled, mode);
          if (enabled) {
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
        if (enabled) assert(diagnostic.topmostAttempts > 2, "watchdog must run while sleeping");
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
