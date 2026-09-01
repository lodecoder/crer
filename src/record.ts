export type MarkerCalibration = {
  screenClick: { x: number; y: number };
  cssPoint: { x: number; y: number };
};

export async function recordRaw(
  dllPath: string,
  pid: number,
  output: string,
  signal?: AbortSignal,
  viewport?: { x: number; y: number },
  markerClick?: () => Promise<{ x: number; y: number } | undefined>,
  readViewport?: () => Promise<{ x: number; y: number }>,
  windowBounds?: { left: number; top: number },
  requestedContent?: { x: number; y: number },
  profileDir?: string,
) {
  const lib = Deno.dlopen(dllPath, {
    crer_input_abi_version: { parameters: [], result: "u32" },
    crer_input_qpc_frequency: { parameters: [], result: "u64" },
    crer_input_start: { parameters: ["u32"], result: "i32" },
    crer_input_stop: { parameters: [], result: "i32" },
    crer_input_read: { parameters: ["buffer", "u32"], result: "u32" },
    crer_input_get_content_rect: { parameters: ["buffer"], result: "i32" },
    crer_input_last_error: { parameters: [], result: "i32" },
  });
  try {
    if (lib.symbols.crer_input_abi_version() !== 1) {
      throw new Error("unsupported crer-win-input ABI");
    }
    const qpcFrequencyHz = lib.symbols.crer_input_qpc_frequency().toString();
    const start = lib.symbols.crer_input_start(pid);
    if (start) throw new Error(`Raw Input start failed: ${start}`);
    console.error(`Recording target Chrome process: ${pid}`);
    let markerCalibration: MarkerCalibration | undefined;
    let activeViewport = viewport;
    let nextViewportCheck = Date.now();
    let mouseDown: { x: number; y: number } | undefined;
    let mouseMoved = false;
    const contentRect = () => {
      const rectBytes = new Uint8Array(16);
      const rect = new DataView(rectBytes.buffer);
      const status = lib.symbols.crer_input_get_content_rect(rectBytes);
      return {
        status,
        x: rect.getInt32(0, true),
        y: rect.getInt32(4, true),
        width: rect.getInt32(8, true),
        height: rect.getInt32(12, true),
      };
    };
    const logClick = (point: { x: number; y: number }) => {
      const rect = contentRect();
      if (
        markerCalibration && activeViewport && rect.status === 0 && rect.width >= 32
        && rect.height >= 32
      ) {
        const origin = {
          x: markerCalibration.screenClick.x
            - markerCalibration.cssPoint.x * rect.width / activeViewport.x,
          y: markerCalibration.screenClick.y
            - markerCalibration.cssPoint.y * rect.height / activeViewport.y,
        };
        const css = {
          x: Math.round((point.x - origin.x) * activeViewport.x / rect.width),
          y: Math.round((point.y - origin.y) * activeViewport.y / rect.height),
        };
        console.log(`[crer] click: { x: ${css.x}, y: ${css.y} }`);
      } else {
        console.log(`[crer] click screen_px: { x: ${point.x}, y: ${point.y} }`);
      }
    };
    const writeMetadata = async () => {
      const rect = contentRect();
      const validRect = rect.status === 0 && rect.width >= 32 && rect.height >= 32;
      if (!validRect && !viewport) return { rectStatus: rect.status, validRect };
      await Deno.writeTextFile(
        `${output}.meta.json`,
        JSON.stringify(
          {
            qpc_frequency_hz: qpcFrequencyHz,
            ...(validRect
              ? {
                content_rect_screen_px: {
                  x: rect.x,
                  y: rect.y,
                  width: rect.width,
                  height: rect.height,
                },
              }
              : {}),
            ...(viewport ? { css_viewport: viewport } : {}),
            ...(requestedContent
              ? { requested_content: { width: requestedContent.x, height: requestedContent.y } }
              : {}),
            ...(profileDir ? { profile_dir: profileDir } : {}),
            ...(windowBounds ? { window_bounds: windowBounds } : {}),
            ...(markerCalibration ? { marker_calibration: markerCalibration } : {}),
          },
          null,
          2,
        ) + "\n",
      );
      return { rectStatus: rect.status, validRect };
    };
    console.error(
      "Click the 64x64 magenta marker at the page's upper-left corner to calibrate and begin recording.",
    );
    const file = await Deno.open(output, { create: true, write: true, append: true });
    try {
      while (!signal?.aborted) {
        if (readViewport && Date.now() >= nextViewportCheck) {
          const nextViewport = await readViewport();
          if (
            activeViewport
            && (nextViewport.x !== activeViewport.x || nextViewport.y !== activeViewport.y)
          ) {
            console.error(
              `Note: recording viewport changed from ${activeViewport.x}x${activeViewport.y} to ${nextViewport.x}x${nextViewport.y}; subsequent input will use the new coordinate space.`,
            );
          }
          activeViewport = nextViewport;
          nextViewportCheck = Date.now() + 250;
        }
        const bytes = new Uint8Array(24 * 256);
        const n = lib.symbols.crer_input_read(bytes, 256);
        const view = new DataView(bytes.buffer);
        for (let i = 0; i < n; i++) {
          const o = i * 24;
          const event = {
            qpc: view.getBigUint64(o, true).toString(),
            x: view.getInt32(o + 8, true),
            y: view.getInt32(o + 12, true),
            kind: view.getUint32(o + 16, true),
            data: view.getUint32(o + 20, true),
            ...(activeViewport && viewport
                && (activeViewport.x !== viewport.x || activeViewport.y !== viewport.y)
              ? { css_viewport: activeViewport }
              : {}),
          };
          if (!markerCalibration && markerClick && viewport) {
            try {
              if (event.kind === 3) {
                await new Promise((resolve) => setTimeout(resolve, 20));
                const cssPoint = await markerClick();
                if (cssPoint) {
                  markerCalibration = {
                    screenClick: { x: event.x, y: event.y },
                    cssPoint,
                  };
                  console.error("Calibration complete. Recording browser interactions now.");
                }
              }
            } catch {
              // The recording can still be normalized with explicit coordinates.
            }
            continue;
          }
          if (event.kind === 2) {
            mouseDown = { x: event.x, y: event.y };
            mouseMoved = false;
          } else if (
            event.kind === 1 && mouseDown && (event.x !== mouseDown.x || event.y !== mouseDown.y)
          ) {
            mouseMoved = true;
          } else if (event.kind === 3) {
            if (mouseDown && !mouseMoved) logClick({ x: event.x, y: event.y });
            mouseDown = undefined;
            mouseMoved = false;
          }
          await file.write(new TextEncoder().encode(JSON.stringify(event) + "\n"));
        }
        await new Promise((r) => setTimeout(r, 16));
      }
    } finally {
      file.close();
    }
    const metadata = await writeMetadata();
    if (markerClick && !markerCalibration) {
      throw new Error("recording calibration marker was not clicked");
    }
    if (!metadata.validRect) {
      console.error(
        `Warning: CfT content bounds were unavailable (Win32 status ${metadata.rectStatus}); normalize may require explicit coordinate options.`,
      );
    }
  } finally {
    lib.symbols.crer_input_stop();
    lib.close();
  }
}
