export async function recordRaw(
  dllPath: string,
  pid: number,
  output: string,
  signal?: AbortSignal,
) {
  const lib = Deno.dlopen(dllPath, {
    crer_input_abi_version: { parameters: [], result: "u32" },
    crer_input_start: { parameters: ["u32"], result: "i32" },
    crer_input_stop: { parameters: [], result: "i32" },
    crer_input_read: { parameters: ["buffer", "u32"], result: "u32" },
    crer_input_last_error: { parameters: [], result: "i32" },
  });
  try {
    if (lib.symbols.crer_input_abi_version() !== 1) {
      throw new Error("unsupported crer-win-input ABI");
    }
    const start = lib.symbols.crer_input_start(pid);
    if (start) throw new Error(`Raw Input start failed: ${start}`);
    console.error("Recording. Press Ctrl+C to stop.");
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
        }
        await new Promise((r) => setTimeout(r, 16));
      }
    } finally {
      file.close();
    }
  } finally {
    lib.symbols.crer_input_stop();
    lib.close();
  }
}
