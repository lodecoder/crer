import { appendFile, writeFile } from "node:fs/promises";

export interface ArtifactSink {
  appendStep(entry: Record<string, unknown>): Promise<void>;
  writePng(name: string, base64: string): Promise<void>;
}

export class RunArtifactSink implements ArtifactSink {
  constructor(private readonly runDirectory: string) {}

  appendStep(entry: Record<string, unknown>): Promise<void> {
    return appendFile(
      `${this.runDirectory}/steps.ndjson`,
      JSON.stringify(entry) + "\n",
      "utf8",
    );
  }

  async writePng(name: string, base64: string): Promise<void> {
    await writeFile(
      `${this.runDirectory}/${name}`,
      Uint8Array.from(atob(base64), (value) => value.charCodeAt(0)),
    );
  }
}
