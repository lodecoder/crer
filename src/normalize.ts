import type { Point, Scenario, Step } from "./types.ts";

type RawEvent = { qpc: string; x: number; y: number; kind: number; data: number };
export type CoordinateTransform = { clientOrigin: Point; clientSize: Point; viewport: Point };
type RecordingMetadata = {
  content_rect_screen_px?: { x: number; y: number; width: number; height: number };
  css_viewport?: Point;
  window_bounds?: { left: number; top: number };
  qpc_frequency_hz?: string;
  marker_calibration?: {
    screenClick: Point;
    cssPoint: Point;
  };
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
  const calibration = metadata.marker_calibration;
  // The marker is a fixed 8x8 CSS-pixel overlay at the page origin. CfT may place
  // its mandatory information bar in the compositor surface, but CDP input
  // coordinates begin at this DOM viewport origin.
  const clientOrigin = calibration && calibration.cssPoint.x >= 0 && calibration.cssPoint.y >= 0
    ? {
      x: calibration.screenClick.x
        - calibration.cssPoint.x * rect.width / viewport.x,
      y: calibration.screenClick.y
        - calibration.cssPoint.y * rect.height / viewport.y,
    }
    : { x: rect.x, y: rect.y };
  return {
    clientOrigin,
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

export async function qpcFrequencyFromSidecar(rawFile: string): Promise<bigint | undefined> {
  try {
    const value = (JSON.parse(await Deno.readTextFile(`${rawFile}.meta.json`)) as RecordingMetadata)
      .qpc_frequency_hz;
    return value && /^\d+$/.test(value) && BigInt(value) > 0n ? BigInt(value) : undefined;
  } catch (error) {
    if (error instanceof Deno.errors.NotFound) return undefined;
    throw new Error(`could not read recording metadata: ${error}`);
  }
}

export async function windowBoundsFromSidecar(
  rawFile: string,
): Promise<{ left: number; top: number } | undefined> {
  try {
    const bounds =
      (JSON.parse(await Deno.readTextFile(`${rawFile}.meta.json`)) as RecordingMetadata)
        .window_bounds;
    return bounds && Number.isFinite(bounds.left) && Number.isFinite(bounds.top)
      ? bounds
      : undefined;
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
  qpcFrequencyHz?: bigint,
): Promise<Scenario> {
  return (await normalizeRawWithWarnings(path, url, name, transform, qpcFrequencyHz)).scenario;
}

export async function normalizeRawWithWarnings(
  path: string,
  url: string,
  name: string,
  transform?: CoordinateTransform,
  qpcFrequencyHz?: bigint,
): Promise<NormalizedRecording> {
  const raw = (await Deno.readTextFile(path)).split(/\r?\n/).filter(Boolean).map((line) =>
    JSON.parse(line) as RawEvent
  );
  const steps: Step[] = [];
  const warnings: string[] = [];
  let mouseDown: Point | undefined;
  let mouseLast: Point | undefined;
  let text = "";
  let textQpc = 0n;
  let lastActionQpc: bigint | undefined;
  let shift = false;
  const modifiers = new Set<string>();
  const addStep = (step: Step, qpc: bigint) => {
    if (qpcFrequencyHz && lastActionQpc !== undefined && qpc >= lastActionQpc) {
      const delayMs = Number((qpc - lastActionQpc) * 1000n / qpcFrequencyHz);
      if (delayMs > 0) steps.push({ do: "sleep", ms: delayMs });
    }
    steps.push(step);
    lastActionQpc = qpc;
  };
  const flushText = () => {
    if (text) addStep({ do: "text", value: text }, textQpc);
    text = "";
  };
  for (const event of raw) {
    const qpc = BigInt(event.qpc);
    const point = transform
      ? screenToCss({ x: event.x, y: event.y }, transform)
      : { x: event.x, y: event.y };
    const virtualKey = event.data >>> 16;
    if (virtualKey === 16 || virtualKey === 160 || virtualKey === 161) {
      shift = event.kind === 7;
      continue;
    }
    const modifier =
      ({ 17: "Control", 18: "Alt", 91: "Meta", 92: "Meta" } as Record<number, string>)[virtualKey];
    if (modifier) {
      if (event.kind === 7) modifiers.add(modifier);
      if (event.kind === 8) modifiers.delete(modifier);
      continue;
    }
    if (event.kind === 8) continue;
    const printable = (virtualKey >= 0x30 && virtualKey <= 0x39)
      || (virtualKey >= 0x41 && virtualKey <= 0x5a);
    if (event.kind === 7 && modifiers.size) {
      flushText();
      const key = printable
        ? (shift
          ? String.fromCharCode(virtualKey).toUpperCase()
          : String.fromCharCode(virtualKey).toLowerCase())
        : keys[virtualKey] ?? String.fromCharCode(virtualKey);
      if (key) addStep({ do: "key_chord", keys: [...modifiers, key] }, qpc);
      continue;
    }
    if (event.kind === 7 && printable) {
      const character = String.fromCharCode(virtualKey);
      text += shift ? character.toUpperCase() : character.toLowerCase();
      textQpc = qpc;
      continue;
    }
    flushText();
    if (event.kind === 1 && mouseDown) mouseLast = point;
    if (event.kind === 2) {
      if (mouseDown) warnings.push("ignored incomplete left mouse down before next mouse down");
      mouseDown = point;
      mouseLast = point;
    }
    if (event.kind === 3 && mouseDown) {
      if (mouseLast && (mouseLast.x !== mouseDown.x || mouseLast.y !== mouseDown.y)) {
        addStep({ do: "drag", from: mouseDown, to: mouseLast }, qpc);
      } else {
        addStep({ do: "click", at: mouseDown }, qpc);
      }
      mouseDown = undefined;
      mouseLast = undefined;
    }
    if (event.kind === 6) {
      const delta = event.data > 0x7fff ? event.data - 0x10000 : event.data;
      addStep({ do: "scroll", at: point, delta: { x: 0, y: -delta } }, qpc);
    }
    if (event.kind === 7) {
      const key = keys[virtualKey] ?? String.fromCharCode(virtualKey);
      if (key) addStep({ do: "key", key }, qpc);
    }
  }
  flushText();
  if (mouseDown) warnings.push("ignored incomplete left mouse down at end of recording");
  return {
    scenario: {
      version: 1,
      name,
      browser: {
        chrome: "chrome-for-testing@pinned",
        profile: "ephemeral",
        initial_url: url,
        ...(transform
          ? { window: { content: { width: transform.viewport.x, height: transform.viewport.y } } }
          : {}),
      },
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
