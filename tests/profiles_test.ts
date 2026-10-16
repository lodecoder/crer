import { assertEquals, assertRejects } from "@std/assert";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { persistentProfileDirectory, prepareChromeProfile } from "../src/profiles.ts";

test("accepts a persistent profile below .crer/profiles", async () => {
  const name = `test-${crypto.randomUUID()}`;
  const path = `.crer\\profiles\\${name}`;
  try {
    const resolved = await persistentProfileDirectory(path);
    assertEquals(resolved.toLowerCase().endsWith(`\\.crer\\profiles\\${name}`.toLowerCase()), true);
  } finally {
    await rm(path, { recursive: true }).catch(() => {});
  }
});

test("rejects a profile outside .crer/profiles", async () => {
  await assertRejects(
    () => persistentProfileDirectory(".crer\\outside-profile"),
    Error,
    "must be under .crer\\profiles",
  );
});

test("disables password manager UI while retaining existing profile preferences", async () => {
  const directory = await mkdtemp(join(tmpdir(), "crer-test-"));
  try {
    await mkdir(`${directory}/Default`);
    await writeFile(
      `${directory}/Default/Preferences`,
      JSON.stringify({ homepage: "https://example.test", profile: { custom: true } }),
    );
    await prepareChromeProfile(directory);
    const result = JSON.parse(await readFile(`${directory}/Default/Preferences`, "utf8"));
    assertEquals(result.homepage, "https://example.test");
    assertEquals(result.translate.enabled, false);
    assertEquals(result.credentials_enable_service, false);
    assertEquals(result.profile, {
      custom: true,
      password_manager_enabled: false,
      password_manager_leak_detection: false,
    });
  } finally {
    await rm(directory, { recursive: true });
  }
});

test("rejects parent traversal in a profile directory", async () => {
  await assertRejects(
    () => persistentProfileDirectory(".crer\\profiles\\..\\outside-profile"),
    Error,
    "must not contain ..",
  );
});
