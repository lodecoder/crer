import { assert, assertEquals, assertThrows } from "@std/assert";
import { test } from "node:test";
import { loadNativeLibrary } from "../src/native.ts";

const skip = process.platform !== "win32";

test("native output buffers preserve subarray offsets and adjacent bytes", { skip }, () => {
  const library = loadNativeLibrary("kernel32.dll", {
    QueryPerformanceCounter: { parameters: ["buffer"], result: "i32" },
  });
  try {
    const bytes = new Uint8Array(24).fill(0xa5);
    assertEquals(library.symbols.QueryPerformanceCounter(bytes.subarray(8, 16)), 1);
    assert(new DataView(bytes.buffer).getBigInt64(8, true) > 0n);
    assertEquals([...bytes.subarray(0, 8)], Array(8).fill(0xa5));
    assertEquals([...bytes.subarray(16)], Array(8).fill(0xa5));
  } finally {
    library.close();
  }
});

test("native 64 bit values retain BigInt precision and pointer high bits", { skip }, () => {
  const library = loadNativeLibrary("kernel32.dll", {
    GetTickCount64: { parameters: [], result: "u64" },
    GetCurrentProcess: { parameters: [], result: "usize" },
    GetCurrentThread: { parameters: [], result: "isize" },
  });
  try {
    const ticks = library.symbols.GetTickCount64();
    assertEquals(typeof ticks, "bigint");
    assert(ticks >= 0n);
    const pointerBits = process.arch === "ia32" ? 32 : 64;
    assertEquals(library.symbols.GetCurrentProcess(), BigInt.asUintN(pointerBits, -1n));
    assertEquals(library.symbols.GetCurrentThread(), -2n);
  } finally {
    library.close();
  }
});

test("native library close is idempotent and prevents calls after unload", { skip }, () => {
  const library = loadNativeLibrary("kernel32.dll", {
    GetTickCount64: { parameters: [], result: "u64" },
  });
  assertEquals(typeof library.symbols.GetTickCount64(), "bigint");
  library.close();
  library.close();
  assertThrows(() => library.symbols.GetTickCount64(), Error, "native library is closed");
});

test("failed native symbol lookup leaves later library loads usable", { skip }, () => {
  assertThrows(() =>
    loadNativeLibrary("kernel32.dll", {
      crer_nonexistent_native_test_symbol: { parameters: [], result: "u32" },
    })
  );
  const library = loadNativeLibrary("kernel32.dll", {
    GetCurrentProcessId: { parameters: [], result: "u32" },
  });
  try {
    assertEquals(library.symbols.GetCurrentProcessId(), process.pid);
  } finally {
    library.close();
  }
});
