const colors = {
  default: 39,
  black: 30,
  red: 31,
  green: 32,
  yellow: 33,
  blue: 34,
  magenta: 35,
  cyan: 36,
  white: 37,
} as const;

export type LogColor = keyof typeof colors;
export type ScenarioLogger = { log: (message: string) => void; warn: (message: string) => void };

export function validateLogColor(value: unknown): void {
  if (value !== undefined && (typeof value !== "string" || !Object.hasOwn(colors, value))) {
    throw new Error(`playback.log_color must be one of: ${Object.keys(colors).join(", ")}`);
  }
}

export function formatScenarioLog(
  name: string,
  message: string,
  color: LogColor = "default",
  colored = false,
): string {
  const prefix = `[${name.replace(/[\r\n]/g, " ")}]`;
  return message.split(/\r?\n/).map((line) => {
    const text = `${prefix} ${line}`;
    return colored && color !== "default" ? `\x1b[${colors[color]}m${text}\x1b[39m` : text;
  }).join("\n");
}

export function createScenarioLogger(name: string, color?: LogColor): ScenarioLogger {
  return {
    log: (message) =>
      console.log(
        formatScenarioLog(name, message, color, !Deno.noColor && Deno.stdout.isTerminal()),
      ),
    warn: (message) =>
      console.warn(
        formatScenarioLog(name, message, color, !Deno.noColor && Deno.stderr.isTerminal()),
      ),
  };
}
