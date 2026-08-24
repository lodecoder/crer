export type FocusCalibration = {
  screenClick: { x: number; y: number };
  cssRect: { x: number; y: number; width: number; height: number };
};

export async function recordRaw(
  dllPath: string,
  pid: number,
  output: string,
  signal?: AbortSignal,
  viewport?: { x: number; y: number },
  focusedElement?: () => Promise<
    { x: number; y: number; width: number; height: number } | undefined
  >,
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
    let focusCalibration: FocusCalibration | undefined;
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
            ...(focusCalibration ? { focus_calibration: focusCalibration } : {}),
          },
          null,
          2,
        ) + "\n",
      );
      return { rectStatus, validRect };
    };
    console.error(
      "Recording. Press Ctrl+C to stop, or use the caller's configured stop mechanism.",
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
          await file.write(new TextEncoder().encode(JSON.stringify(event) + "\n"));
          // A CfT information bar is browser chrome, and can make a compositor HWND
          // larger than the DOM viewport.  An editable element that received this
          // click gives us a reliable screen-to-CSS calibration point.
          if (event.kind === 3 && focusedElement && viewport) {
            try {
              await new Promise((resolve) => setTimeout(resolve, 20));
              const cssRect = await focusedElement();
              if (cssRect && cssRect.width > 0 && cssRect.height > 0) {
                focusCalibration = { screenClick: { x: event.x, y: event.y }, cssRect };
              }
            } catch {
              // Recording must continue when the page navigates or CDP momentarily disconnects.
            }
          }
        }
        await new Promise((r) => setTimeout(r, 16));
      }
    } finally {
      file.close();
    }
    const metadata = await writeMetadata();
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
