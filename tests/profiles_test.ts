import { assertEquals, assertRejects } from "jsr:@std/assert@^1.0.14";
import { persistentProfileDirectory } from "../src/profiles.ts";

Deno.test("accepts a persistent profile below .crer/profiles", async () => {
  const name = `test-${crypto.randomUUID()}`;
  const path = `.crer\\profiles\\${name}`;
  try {
    const resolved = await persistentProfileDirectory(path);
    assertEquals(resolved.toLowerCase().endsWith(`\\.crer\\profiles\\${name}`.toLowerCase()), true);
  } finally {
    await Deno.remove(path, { recursive: true }).catch(() => {});
  }
});

Deno.test("rejects a profile outside .crer/profiles", async () => {
  await assertRejects(
    () => persistentProfileDirectory(".crer\\outside-profile"),
    Error,
    "must be under .crer\\profiles",
  );
});

Deno.test("rejects parent traversal in a profile directory", async () => {
  await assertRejects(
    () => persistentProfileDirectory(".crer\\profiles\\..\\outside-profile"),
    Error,
    "must not contain ..",
  );
});
