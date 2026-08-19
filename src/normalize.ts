import type { Point, Scenario, Step } from "./types.ts";

type RawEvent = { qpc: string; x: number; y: number; kind: number; data: number };
export type CoordinateTransform = { clientOrigin: Point; clientSize: Point; viewport: Point };
type RecordingMetadata = {
  content_rect_screen_px?: { x: number; y: number; width: number; height: number };
  css_viewport?: Point;
};
export type NormalizedRecording = { scenario: Scenario; warnings: string[] };

export function transformFromRecordingMetadata(
  metadata: RecordingMetadata,
): CoordinateTransform | undefined {
  const rect = metadata.content_rect_screen_px;
  const viewport = metadata.css_viewport;
  if (
    !rect || !viewport || rect.width < 32 || rect.height < 32 || viewport.x <= 0 || viewport.y <= 0
  ) {
    return undefined;
  }
  return {
    clientOrigin: { x: rect.x, y: rect.y },
    clientSize: { x: rect.width, y: rect.height },
    viewport,
  };
}

export async function transformFromSidecar(
  rawFile: string,
): Promise<CoordinateTransform | undefined> {
  try {
    return transformFromRecordingMetadata(
      JSON.parse(await Deno.readTextFile(`${rawFile}.meta.json`)) as RecordingMetadata,
    );
  } catch (error) {
    if (error instanceof Deno.errors.NotFound) return undefined;
    throw new Error(`could not read recording metadata: ${error}`);
  }
}

export function screenToCss(point: Point, transform: CoordinateTransform): Point {
  if (transform.clientSize.x <= 0 || transform.clientSize.y <= 0) {
    throw new Error("client dimensions must be positive");
  }
  return {
    x: Math.round(
      (point.x - transform.clientOrigin.x) * transform.viewport.x / transform.clientSize.x,
    ),
    y: Math.round(
      (point.y - transform.clientOrigin.y) * transform.viewport.y / transform.clientSize.y,
    ),
  };
}

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

export async function normalizeRaw(
  path: string,
  url: string,
  name: string,
  transform?: CoordinateTransform,
): Promise<Scenario> {
  return (await normalizeRawWithWarnings(path, url, name, transform)).scenario;
}

export async function normalizeRawWithWarnings(
  path: string,
  url: string,
  name: string,
  transform?: CoordinateTransform,
): Promise<NormalizedRecording> {
  const raw = (await Deno.readTextFile(path)).split(/\r?\n/).filter(Boolean).map((line) =>
    JSON.parse(line) as RawEvent
  );
  const steps: Step[] = [];
  const warnings: string[] = [];
  let mouseDown: Point | undefined;
  let text = "";
  const flushText = () => {
    if (text) steps.push({ do: "text", value: text });
    text = "";
  };
  for (const event of raw) {
    const point = transform
      ? screenToCss({ x: event.x, y: event.y }, transform)
      : { x: event.x, y: event.y };
    const virtualKey = event.data >>> 16;
    const printable = (virtualKey >= 0x30 && virtualKey <= 0x39)
      || (virtualKey >= 0x41 && virtualKey <= 0x5a);
    if (event.kind === 7 && printable) {
      text += String.fromCharCode(virtualKey).toLowerCase();
      continue;
    }
    flushText();
    if (event.kind === 2) mouseDown = point;
    if (event.kind === 3 && mouseDown) {
      steps.push({ do: "click", at: mouseDown });
      mouseDown = undefined;
    }
    if (event.kind === 6) {
      const delta = event.data > 0x7fff ? event.data - 0x10000 : event.data;
      steps.push({ do: "scroll", at: point, delta: { x: 0, y: -delta } });
    }
    if (event.kind === 7) {
      const key = keys[virtualKey] ?? String.fromCharCode(virtualKey);
      if (key) steps.push({ do: "key", key });
    }
  }
  flushText();
  if (mouseDown) warnings.push("ignored incomplete left mouse down at end of recording");
  return {
    scenario: {
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
    },
    warnings,
  };
}
