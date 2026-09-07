export interface ArtifactSink {
  appendStep(entry: Record<string, unknown>): Promise<void>;
  writePng(name: string, base64: string): Promise<void>;
}

export class RunArtifactSink implements ArtifactSink {
  constructor(private readonly runDirectory: string) {}

  appendStep(entry: Record<string, unknown>): Promise<void> {
    return Deno.writeTextFile(
      `${this.runDirectory}/steps.ndjson`,
      JSON.stringify(entry) + "\n",
      { append: true },
    );
  }

  async writePng(name: string, base64: string): Promise<void> {
    await Deno.writeFile(
      `${this.runDirectory}/${name}`,
      Uint8Array.from(atob(base64), (value) => value.charCodeAt(0)),
    );
  }
}
