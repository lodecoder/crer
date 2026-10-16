import koffi from "koffi";

type NativeType =
  | "u8"
  | "u32"
  | "i32"
  | "u64"
  | "i64"
  | "usize"
  | "isize"
  | "buffer"
  | "pointer"
  | "function"
  | "void";
type Definition = { readonly parameters: readonly NativeType[]; readonly result: NativeType };
type Definitions = Record<string, Definition>;
type NativeValue<T extends NativeType> = T extends "buffer" ? ArrayBufferView
  : T extends "pointer" | "function" ? bigint | null
  : T extends "u64" | "i64" | "usize" | "isize" ? bigint
  : T extends "void" ? void
  : number;
type Parameters<T extends readonly NativeType[]> = { -readonly [K in keyof T]: NativeValue<T[K]> };
export type NativeLibrary<T extends Definitions> = {
  symbols: {
    [K in keyof T]: (...args: Parameters<T[K]["parameters"]>) => NativeValue<T[K]["result"]>;
  };
  close(): void;
};

const nativeTypes: Record<NativeType, string> = {
  u8: "uint8_t",
  u32: "uint32_t",
  i32: "int32_t",
  u64: "uint64_t",
  i64: "int64_t",
  usize: "uintptr_t",
  isize: "intptr_t",
  buffer: "void *",
  pointer: "void *",
  function: "void *",
  void: "void",
};

/** C ABI とバッファの寿命を一か所で管理し、64 bit 整数を BigInt に統一する。 */
export function loadNativeLibrary<const T extends Definitions>(
  path: string,
  definitions: T,
): NativeLibrary<T> {
  const library = koffi.load(path);
  let closed = false;
  try {
    const symbols = Object.fromEntries(
      Object.entries(definitions).map(([name, definition]) => {
        const binding = library.func(
          name,
          nativeTypes[definition.result],
          definition.parameters.map((type) => nativeTypes[type]),
        );
        return [name, (...args: unknown[]) => {
          if (closed) throw new Error("native library is closed");
          const result: unknown = binding(...args);
          return ["u64", "i64", "usize", "isize"].includes(definition.result)
            ? BigInt(result as number | bigint)
            : result;
        }];
      }),
    ) as NativeLibrary<T>["symbols"];
    return {
      symbols,
      close() {
        if (closed) return;
        closed = true;
        library.unload();
      },
    };
  } catch (error) {
    library.unload();
    throw error;
  }
}
