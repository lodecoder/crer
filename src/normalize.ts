import type { Point, Scenario, Step } from "./types.ts";

type RawEvent = { qpc: string; x: number; y: number; kind: number; data: number };

const keys: Record<number, string> = {
  8: "Backspace",
  9: "Tab",
  13: "Enter",
  27: "Escape",
  46: "Delete",
  37: "ArrowLeft",
  38: "ArrowUp",
  39: "ArrowRight",
  40: "ArrowDown",
};

export async function normalizeRaw(path: string, url: string, name: string): Promise<Scenario> {
  const raw = (await Deno.readTextFile(path)).split(/\r?\n/).filter(Boolean).map((line) =>
    JSON.parse(line) as RawEvent
  );
  const steps: Step[] = [];
  let mouseDown: Point | undefined;
  for (const event of raw) {
    if (event.kind === 2) mouseDown = { x: event.x, y: event.y };
    if (event.kind === 3 && mouseDown) {
      steps.push({ do: "click", at: mouseDown });
      mouseDown = undefined;
    }
    if (event.kind === 6) {
      const delta = event.data > 0x7fff ? event.data - 0x10000 : event.data;
      steps.push({ do: "scroll", at: { x: event.x, y: event.y }, delta: { x: 0, y: -delta } });
    }
    if (event.kind === 7) {
      const key = keys[event.data >>> 16] ?? String.fromCharCode(event.data >>> 16);
      if (key) steps.push({ do: "key", key });
    }
  }
  return {
    version: 1,
    name,
    browser: { chrome: "chrome-for-testing@pinned", profile: "ephemeral", initial_url: url },
    playback: {
      jitter: {
        enabled: false,
        distribution: "none",
        radius_px: 0,
        min_distance_from_edge_px: 0,
        out_of_bounds: "fail",
      },
    },
    steps,
  };
}
