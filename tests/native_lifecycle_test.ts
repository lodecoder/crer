import { assertEquals } from "@std/assert";

const nativeDll = "native/bin/Release/net10.0/win-x64/publish/crer-win-input.dll";
const nativeDllExists = await Deno.stat(nativeDll).then((value) => value.isFile).catch(() => false);

Deno.test({
  name: "native recorder rejects duplicate start and supports consecutive sessions",
  ignore: Deno.build.os !== "windows" || !nativeDllExists,
  fn: async () => {
    const lib = Deno.dlopen(nativeDll, {
      crer_input_start: { parameters: ["u32"], result: "i32" },
      crer_input_is_running: { parameters: [], result: "i32" },
      crer_input_stop: { parameters: [], result: "i32" },
    });
    try {
      for (let session = 0; session < 2; session++) {
        assertEquals(lib.symbols.crer_input_start(Deno.pid), 0);
        assertEquals(lib.symbols.crer_input_start(Deno.pid), 183);
        await new Promise((resolve) => setTimeout(resolve, 25));
        assertEquals(lib.symbols.crer_input_is_running(), 1);
        assertEquals(lib.symbols.crer_input_stop(), 0);
        assertEquals(lib.symbols.crer_input_is_running(), 0);
      }
    } finally {
      if (lib.symbols.crer_input_is_running()) lib.symbols.crer_input_stop();
      lib.close();
    }
  },
});
