import { assertEquals, assertRejects } from "jsr:@std/assert@^1.0.14";
import { persistentProfileDirectory, prepareChromeProfile } from "../src/profiles.ts";

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

Deno.test("disables password manager UI while retaining existing profile preferences", async () => {
  const directory = await Deno.makeTempDir();
  try {
    await Deno.mkdir(`${directory}/Default`);
    await Deno.writeTextFile(
      `${directory}/Default/Preferences`,
      JSON.stringify({ homepage: "https://example.test", profile: { custom: true } }),
    );
    await prepareChromeProfile(directory);
    const result = JSON.parse(await Deno.readTextFile(`${directory}/Default/Preferences`));
    assertEquals(result.homepage, "https://example.test");
    assertEquals(result.translate.enabled, false);
    assertEquals(result.credentials_enable_service, false);
    assertEquals(result.profile, {
      custom: true,
      password_manager_enabled: false,
      password_manager_leak_detection: false,
    });
  } finally {
    await Deno.remove(directory, { recursive: true });
  }
});

Deno.test("rejects parent traversal in a profile directory", async () => {
  await assertRejects(
    () => persistentProfileDirectory(".crer\\profiles\\..\\outside-profile"),
    Error,
    "must not contain ..",
  );
});
