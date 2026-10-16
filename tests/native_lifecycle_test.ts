import { assertEquals } from "@std/assert";
import { stat } from "node:fs/promises";
import { test } from "node:test";
import { loadNativeLibrary } from "../src/native.ts";

const nativeDll = "native/bin/Release/net10.0/win-x64/publish/crer-win-input.dll";
const nativeDllExists = await stat(nativeDll).then((value) => value.isFile()).catch(() => false);

test("native recorder rejects duplicate start and supports consecutive sessions", {
  skip: process.platform !== "win32" || !nativeDllExists,
}, async () => {
  const lib = loadNativeLibrary(nativeDll, {
    crer_input_abi_version: { parameters: [], result: "u32" },
    crer_input_start: { parameters: ["u32"], result: "i32" },
    crer_input_is_running: { parameters: [], result: "i32" },
    crer_input_stop: { parameters: [], result: "i32" },
  });
  try {
    assertEquals(lib.symbols.crer_input_abi_version(), 2);
    for (let session = 0; session < 2; session++) {
      assertEquals(lib.symbols.crer_input_start(process.pid), 0);
      assertEquals(lib.symbols.crer_input_start(process.pid), 183);
      await new Promise((resolve) => setTimeout(resolve, 25));
      assertEquals(lib.symbols.crer_input_is_running(), 1);
      assertEquals(lib.symbols.crer_input_stop(), 0);
      assertEquals(lib.symbols.crer_input_is_running(), 0);
    }
  } finally {
    if (lib.symbols.crer_input_is_running()) lib.symbols.crer_input_stop();
    lib.close();
  }
});
