/** Creates a Windows-safe run ID whose lexical order follows its UTC millisecond timestamp. */
export function createRunId(
  now = new Date(),
  randomId = crypto.randomUUID(),
): string {
  const timestamp = now.toISOString().replaceAll(/[-:.]/g, "");
  return `${timestamp}-${randomId}`;
}
