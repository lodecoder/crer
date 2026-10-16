import { mkdir, readFile, realpath, writeFile } from "node:fs/promises";

function isNotFound(error: unknown): boolean {
  return !!error && typeof error === "object" && "code" in error && error.code === "ENOENT";
}

/** Resolves a reusable profile while preventing access to normal Chrome profiles. */
export async function persistentProfileDirectory(configured: string): Promise<string> {
  const root = `${process.cwd()}\\.crer\\profiles`;
  await mkdir(root, { recursive: true });
  const resolvedRoot = await realpath(root);
  const raw = configured.trim();
  if (!raw) throw new Error("--profile-dir requires a directory");
  const value = raw.replaceAll("/", "\\");
  if (value.split("\\").includes("..")) {
    throw new Error("--profile-dir must not contain .. and must be under .crer\\profiles");
  }
  const absolute = /^(?:[A-Za-z]:\\|\\\\)/.test(value);
  if (!absolute && !/^(?:\.\\)?\.crer\\profiles(?:\\|$)/i.test(value)) {
    throw new Error("--profile-dir must be under .crer\\profiles");
  }
  const candidate = absolute ? value : `${process.cwd()}\\${value}`;
  const inside = (path: string, parent: string) => {
    const normalizedPath = path.replaceAll("/", "\\").toLowerCase();
    const normalizedParent = parent.replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase();
    return normalizedPath === normalizedParent
      || normalizedPath.startsWith(`${normalizedParent}\\`);
  };
  if (!inside(candidate, resolvedRoot)) {
    throw new Error("--profile-dir must be under .crer\\profiles");
  }
  await mkdir(candidate, { recursive: true });
  const resolved = await realpath(candidate);
  if (!inside(resolved, resolvedRoot)) {
    throw new Error("--profile-dir resolves outside .crer\\profiles");
  }
  return resolved;
}

/** Applies CRER's browser-UI privacy defaults without discarding persistent profile data. */
export async function prepareChromeProfile(directory: string): Promise<void> {
  await mkdir(`${directory}/Default`, { recursive: true });
  const path = `${directory}/Default/Preferences`;
  let preferences: Record<string, unknown> = {};
  try {
    const parsed = JSON.parse(await readFile(path, "utf8"));
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      preferences = parsed as Record<string, unknown>;
    }
  } catch (error) {
    if (!isNotFound(error)) throw error;
  }
  const profile = preferences.profile && typeof preferences.profile === "object"
      && !Array.isArray(preferences.profile)
    ? preferences.profile as Record<string, unknown>
    : {};
  const translate = preferences.translate && typeof preferences.translate === "object"
      && !Array.isArray(preferences.translate)
    ? preferences.translate as Record<string, unknown>
    : {};
  preferences.translate = { ...translate, enabled: false };
  preferences.credentials_enable_service = false;
  preferences.profile = {
    ...profile,
    password_manager_enabled: false,
    password_manager_leak_detection: false,
  };
  await writeFile(path, JSON.stringify(preferences), "utf8");
}
