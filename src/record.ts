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
    const writeMetadata = async () => {
      const rectBytes = new Uint8Array(16);
      const rect = new DataView(rectBytes.buffer);
      const rectStatus = lib.symbols.crer_input_get_content_rect(rectBytes);
      const validRect = rectStatus === 0 && rect.getInt32(8, true) >= 32
        && rect.getInt32(12, true) >= 32;
      if (!validRect && !viewport) return { rectStatus, validRect };
      await Deno.writeTextFile(
        `${output}.meta.json`,
        JSON.stringify(
          {
            qpc_frequency_hz: qpcFrequencyHz,
            ...(validRect
              ? {
                content_rect_screen_px: {
                  x: rect.getInt32(0, true),
                  y: rect.getInt32(4, true),
                  width: rect.getInt32(8, true),
                  height: rect.getInt32(12, true),
                },
              }
              : {}),
            ...(viewport ? { css_viewport: viewport } : {}),
            ...(markerCalibration ? { marker_calibration: markerCalibration } : {}),
          },
          null,
          2,
        ) + "\n",
      );
      return { rectStatus, validRect };
    };
    console.error(
      "Click the 8x8 magenta marker at the page's upper-left corner to calibrate and begin recording.",
    );
    const file = await Deno.open(output, { create: true, write: true, append: true });
    try {
      while (!signal?.aborted) {
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
