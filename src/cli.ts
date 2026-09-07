import { CliError } from "./errors.ts";

type CommandOptions = { values: readonly string[]; switches: readonly string[] };

const commonBrowserValues = ["--chrome", "--dll"];
const commands: Record<string, CommandOptions> = {
  doctor: { values: commonBrowserValues, switches: [] },
  inspect: { values: [], switches: [] },
  validate: { values: [], switches: [] },
  play: {
    values: [
      ...commonBrowserValues,
      "--seed",
      "--step-delay-ms",
      "--profile-dir",
      "--template-screenshots",
      "--position",
    ],
    switches: ["--keep-artifacts", "--ignore-viewport-mismatch", "--mute-audio"],
  },
  run: {
    values: [
      ...commonBrowserValues,
      "--profile-dir",
      "--plan-window-bounds-override",
      "--template-screenshots",
    ],
    switches: ["--ignore-viewport-mismatch", "--mute-audio"],
  },
  record: {
    values: [
      ...commonBrowserValues,
      "--profile-dir",
      "--url",
      "--content-size",
      "--position",
      "--duration-ms",
      "--stop-file",
    ],
    switches: [],
  },
  normalize: {
    values: [
      "--output",
      "--url",
      "--name",
      "--client-origin",
      "--client-size",
      "--viewport",
    ],
    switches: [],
  },
};

export function validateCommandOptions(command: string, args: readonly string[]): void {
  const spec = commands[command];
  if (!spec) throw new CliError(`unknown command: ${command}`);
  const seen = new Set<string>();
  for (let index = 0; index < args.length; index++) {
    const argument = args[index];
    if (seen.has(argument)) throw new CliError(`option specified more than once: ${argument}`);
    if (spec.switches.includes(argument)) {
      seen.add(argument);
      continue;
    }
    if (spec.values.includes(argument)) {
      seen.add(argument);
      const value = args[++index];
      if (value === undefined || value.startsWith("--")) {
        throw new CliError(`${argument} requires a value`);
      }
      continue;
    }
    throw new CliError(`unknown option for ${command}: ${argument}`);
  }
}

export function commandTakesFile(command: string | undefined): boolean {
  return command !== undefined && command !== "help" && command !== "doctor";
}
