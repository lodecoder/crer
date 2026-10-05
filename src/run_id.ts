/** Creates a readable, Windows-safe run ID ordered by its UTC millisecond timestamp. */
export function createRunId(
  name: string,
  now = new Date(),
  randomId = crypto.randomUUID(),
): string {
  const timestamp = now.toISOString().replaceAll(/[-:.]/g, "");
  const safeName = Array.from(
    Array.from(
      name.normalize("NFKC"),
      (character) => character.charCodeAt(0) < 32 ? "-" : character,
    ).join("").replace(/[<>:"/\\|?*]/gu, "-")
      .replace(/[^\p{L}\p{N}_-]+/gu, "-").replace(/^-+|-+$/gu, ""),
  ).slice(0, 64).join("")
    .replace(/-+$/gu, "") || "run";
  return `${timestamp}-${safeName}-${randomId}`;
}
