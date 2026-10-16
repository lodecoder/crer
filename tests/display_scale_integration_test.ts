import { assert, assertEquals } from "@std/assert";
import koffi from "koffi";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { test } from "node:test";
import { loadNativeLibrary } from "../src/native.ts";
import {
  closeSharedBrowserSession,
  createSharedBrowserSession,
  playScenario,
} from "../src/runtime.ts";
import { runCli } from "./helpers.ts";

const chrome = process.env.CRER_TEST_CHROME;
test("CfT CSS pixels match physical window pixels independently of Windows display scaling", {
  skip: process.platform !== "win32" || !chrome,
}, async () => {
  const inheritedLayer = process.env.__COMPAT_LAYER;
  process.env.__COMPAT_LAYER = "GDIDPISCALING DPIUNAWARE";
  const session = createSharedBrowserSession();
  const profile = `.crer/profiles/display-scale-${crypto.randomUUID()}`;
  const user32 = loadNativeLibrary("user32.dll", {
    EnumWindows: { parameters: ["function", "isize"], result: "i32" },
    GetWindowThreadProcessId: { parameters: ["pointer", "buffer"], result: "u32" },
    IsWindowVisible: { parameters: ["pointer"], result: "i32" },
    GetWindow: { parameters: ["pointer", "u32"], result: "pointer" },
    GetWindowRect: { parameters: ["pointer", "buffer"], result: "i32" },
    GetDpiForWindow: { parameters: ["pointer"], result: "u32" },
    GetWindowDpiAwarenessContext: { parameters: ["pointer"], result: "isize" },
    GetAwarenessFromDpiAwarenessContext: { parameters: ["isize"], result: "i32" },
    SetThreadDpiAwarenessContext: { parameters: ["isize"], result: "isize" },
  });
  try {
    await mkdir(`${profile}/Default`, { recursive: true });
    await writeFile(
      `${profile}/Default/Preferences`,
      JSON.stringify({
        browser: {
          app_window_placement: {
            "_text/html,": {
              bottom: 1112,
              left: 25,
              maximized: false,
              right: 1371,
              top: 12,
              work_area_bottom: 1032,
              work_area_left: 0,
              work_area_right: 1920,
              work_area_top: 0,
            },
          },
          window_placement: {
            bottom: 2054,
            left: 10,
            maximized: false,
            right: 1915,
            top: 10,
            work_area_bottom: 2064,
            work_area_left: 0,
            work_area_right: 3840,
            work_area_top: 0,
          },
        },
      }),
    );
    const result = await playScenario({
      version: 1,
      name: "display-scale",
      browser: {
        chrome: "chrome-for-testing@pinned",
        initial_url:
          "data:text/html,<style>body{margin:0;overflow:hidden}</style><div style='width:100px;height:100px;background:red'></div>",
        block_urls: ["never-block-test"],
        window: { bounds: { left: 25, top: 12 }, content: { width: 1330, height: 1200 } },
      },
      steps: [],
    }, {
      chromePath: chrome!,
      profileDir: profile,
      sharedSession: session,
      ignoreViewportMismatch: true,
    });
    assertEquals(result.code, 0, JSON.stringify(result));
    const browser = session.browser!;
    let window: bigint | null = null;
    const callbackType = koffi.proto(
      "int __stdcall CrerScaleEnumWindow(void *window, intptr_t data)",
    );
    const callback = koffi.register(
      (candidate: bigint | null) => {
        const pid = new Uint32Array(1);
        user32.symbols.GetWindowThreadProcessId(candidate, pid);
        if (
          pid[0] === browser.process.pid && user32.symbols.IsWindowVisible(candidate)
          && !user32.symbols.GetWindow(candidate, 4)
        ) {
          window = candidate;
          return 0;
        }
        return 1;
      },
      koffi.pointer(callbackType),
    );
    try {
      user32.symbols.EnumWindows(callback, 0n);
    } finally {
      koffi.unregister(callback);
    }
    assert(window);
    const context = user32.symbols.GetWindowDpiAwarenessContext(window);
    const awareness = user32.symbols.GetAwarenessFromDpiAwarenessContext(context);
    const previous = user32.symbols.SetThreadDpiAwarenessContext(-4n);
    const rect = new Int32Array(4);
    try {
      assertEquals(user32.symbols.GetWindowRect(window, rect), 1);
    } finally {
      user32.symbols.SetThreadDpiAwarenessContext(previous);
    }
    const bounds = await browser.cdp.call<{ bounds: { width: number; height: number } }>(
      "Browser.getWindowBounds",
      { windowId: browser.windowId },
    );
    const display = JSON.parse(await readFile(`${result.runDir}/display.json`, "utf8"));
    console.log({ dpi: user32.symbols.GetDpiForWindow(window), awareness });
    assert(awareness > 0, "Windows must not bitmap-scale a DPI-unaware CfT window");
    assertEquals(rect[2] - rect[0], bounds.bounds.width);
    assertEquals(rect[3] - rect[1], bounds.bounds.height);
    assertEquals([...rect].slice(0, 2), [25, 12]);
    assertEquals(display.actual.dpr, 1);
    assertEquals(display.actual.scale, 1);
    assertEquals(display.actual.width, 1330);
    assertEquals(display.actual.height, 1200);
  } finally {
    await closeSharedBrowserSession(session);
    user32.close();
    await rm(profile, { recursive: true }).catch(() => {});
    if (inheritedLayer === undefined) delete process.env.__COMPAT_LAYER;
    else process.env.__COMPAT_LAYER = inheritedLayer;
  }
});

test("recording ignores shell DPI compatibility layers and keeps physical and CSS pixels aligned", {
  skip: process.platform !== "win32" || !chrome,
}, async () => {
  const directory = await mkdtemp(".crer/record-scale-");
  const raw = `${directory}/raw.ndjson`;
  try {
    const output = await runCli([
      "record",
      raw,
      "--chrome",
      chrome!,
      "--url",
      "data:text/html,<style>body{margin:0;overflow:hidden}</style>",
      "--position",
      "25,12",
      "--content-size",
      "600,400",
      "--duration-ms",
      "1500",
    ], { env: { __COMPAT_LAYER: "DPIUNAWARE" } });
    assertEquals(output.code, 0, new TextDecoder().decode(output.stderr));
    const metadata = JSON.parse(await readFile(`${raw}.meta.json`, "utf8"));
    assertEquals(metadata.content_rect_screen_px.width, metadata.css_viewport.x);
    assertEquals(metadata.content_rect_screen_px.height, metadata.css_viewport.y);
    assertEquals(metadata.css_viewport, { x: 600, y: 400 });
  } finally {
    await rm(directory, { recursive: true });
  }
});
